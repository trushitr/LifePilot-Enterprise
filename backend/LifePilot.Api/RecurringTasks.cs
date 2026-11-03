using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

// Recurring tasks & smart reminders.
//
// A RecurringTask is the *schedule* (rule + time of day + time zone + reminder lead time). The scheduler materialises
// TaskOccurrence rows from it: every occurrence whose due time or reminder time has passed, plus at least one upcoming
// occurrence, so each due date has its own completion state, long reminder leads fire on time, and nothing is skipped
// while the API is stopped. When an occurrence's reminder
// time passes, a TaskReminder row (the in-app notification) is created exactly once; several occurrences of one schedule
// that were all missed while the API was stopped share a single catch-up reminder instead of flooding the list.
//
// Duplicate protection is layered: the worker and the API mutate a schedule inside a serializable (BEGIN IMMEDIATE)
// transaction, and the database enforces UNIQUE (RecurringTaskId, SequenceNumber) for occurrences and UNIQUE
// (OccurrenceId) for reminders, so even a concurrent pass cannot insert a second copy.

// ----- Scheduler -----

/// <summary>
/// Materialises occurrences and fires reminders. Scoped (it owns a <see cref="LifeDb"/>); the background worker and the
/// API endpoints both use it, so creating a schedule through the API immediately yields its first occurrence and a
/// worker pass after a restart catches up on everything that was missed. All decisions use the injected
/// <see cref="TimeProvider"/>, which tests replace with a controllable clock.
/// </summary>
public sealed class RecurringTaskScheduler(LifeDb db, TimeProvider clock, ILogger<RecurringTaskScheduler> logger)
{
    public const int MaxOccurrencesPerSchedulePerPass = 500;
    public const int MaxRemindersPerPass = 500;
    public const int PreviewCount = 3;

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>One scheduler pass: catch up occurrences for every active schedule, then fire due reminders.</summary>
    public async Task<SchedulerPassResult> RunOnceAsync(CancellationToken ct)
    {
        var now = UtcNow;
        var created = 0;
        var ids = await db.RecurringTasks.Where(t => t.IsActive).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try
            {
                // Serializable maps to BEGIN IMMEDIATE in Microsoft.Data.Sqlite: the writer lock is taken before the
                // schedule is read, so an API request touching the same schedule waits instead of racing.
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var task = await db.RecurringTasks.SingleOrDefaultAsync(t => t.Id == id, ct);
                if (task is not null && task.IsActive)
                {
                    created += await MaterializeAsync(task, now, ct);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A unique-index violation here means another writer materialised the same occurrence first; the
                // transaction is rolled back by disposal and the next pass simply re-reads the schedule.
                logger.LogWarning(ex, "Could not materialise occurrences for recurring task {RecurringTaskId}", id);
            }
            finally { db.ChangeTracker.Clear(); }
        }
        var (reminders, reminded) = await FireRemindersAsync(now, ct);
        return new SchedulerPassResult(created, reminders, reminded);
    }

    /// <summary>
    /// Ensures the occurrences a schedule needs right now exist: every occurrence that is already due, every occurrence
    /// whose reminder time has already arrived (so a long lead time is honoured even though the due date is days away),
    /// and at least one occurrence with a future due time. Must be called with <paramref name="task"/> tracked by
    /// <see cref="LifeDb"/> inside the caller's transaction; the caller saves and commits.
    /// </summary>
    public async Task<int> MaterializeAsync(RecurringTask task, DateTime now, CancellationToken ct)
    {
        if (!task.IsActive) return 0;
        var rule = task.ToRule();
        var time = task.ParseDueTime();
        var zone = ResolveZone(task);
        // The latest stored occurrence is the single source of truth for where to continue. Never hand out a sequence
        // number that already exists (the unique index would reject it anyway).
        var last = await db.TaskOccurrences.Where(o => o.RecurringTaskId == task.Id).OrderByDescending(o => o.SequenceNumber).FirstOrDefaultAsync(ct);
        if (last is not null && last.SequenceNumber + 1 > task.NextSequence) task.NextSequence = last.SequenceNumber + 1;
        var haveFuture = last is not null && last.DueUtc > now;
        var created = 0;
        while (created < MaxOccurrencesPerSchedulePerPass)
        {
            var date = rule.DateAt(task.NextSequence);
            if (date is null) break; // rule exhausted (end date or calendar limit)
            var dueUtc = Recurrence.ToUtc(date.Value, time, zone);
            DateTime? reminderAt = task.ReminderLeadMinutes is int lead ? Recurrence.ReminderAt(date.Value, time, zone, lead) : null;
            var triggered = dueUtc <= now || (reminderAt is DateTime r && r <= now);
            if (haveFuture && !triggered) break; // one future occurrence exists and this one is not needed yet
            db.TaskOccurrences.Add(new TaskOccurrence
            {
                RecurringTaskId = task.Id,
                UserId = task.UserId,
                SequenceNumber = task.NextSequence,
                DueDate = Recurrence.FormatDate(date.Value),
                DueUtc = dueUtc,
                ReminderAtUtc = reminderAt,
                CreatedUtc = now
            });
            task.NextSequence++;
            created++;
            if (dueUtc > now) haveFuture = true;
        }
        return created;
    }

    /// <summary>
    /// Sequence number a schedule resumes from when it is (re-)enabled: the first occurrence that is still in the future,
    /// but never one that already exists as history (overdue or completed occurrences are kept when a schedule is disabled).
    /// Returns null when the rule has no remaining occurrences.
    /// </summary>
    public async Task<int?> ResumeSequenceAsync(RecurringTask task, DateTime now, CancellationToken ct)
    {
        var maxExisting = await db.TaskOccurrences.Where(o => o.RecurringTaskId == task.Id).MaxAsync(o => (int?)o.SequenceNumber, ct);
        return Recurrence.FirstSequenceAfter(task.ToRule(), task.ParseDueTime(), ResolveZone(task), now, (maxExisting ?? -1) + 1);
    }

    /// <summary>
    /// Handles every uncompleted occurrence whose reminder time has passed and that has not been reminded yet. Occurrences
    /// that are still upcoming get one reminder each. Occurrences that are already overdue are the ones missed while the
    /// API was stopped (while running, the worker reminds before or at the due time): when a schedule has several of them
    /// in one pass they share a single catch-up reminder ("3 missed occurrences, Jan 16 – Jan 18") linked to the most
    /// recent one, so weeks of downtime do not produce dozens of notifications while every occurrence stays listed.
    /// Returns (reminders created, occurrences handled).
    /// </summary>
    public async Task<(int Reminders, int Occurrences)> FireRemindersAsync(DateTime now, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var due = await db.TaskOccurrences.Include(o => o.RecurringTask)
                .Where(o => !o.Completed && o.ReminderSentUtc == null && o.ReminderAtUtc != null && o.ReminderAtUtc <= now && o.RecurringTask!.IsActive)
                .OrderBy(o => o.ReminderAtUtc).ThenBy(o => o.Id).Take(MaxRemindersPerPass).ToListAsync(ct);
            if (due.Count == 0) return (0, 0);
            var occurrenceIds = due.Select(o => o.Id).ToList();
            var alreadyReminded = (await db.TaskReminders.Where(r => occurrenceIds.Contains(r.OccurrenceId)).Select(r => r.OccurrenceId).ToListAsync(ct)).ToHashSet();
            var raised = new List<(TaskReminder Reminder, List<TaskOccurrence> Covers)>();
            foreach (var occurrence in due) occurrence.ReminderSentUtc = now;
            foreach (var group in due.Where(o => !alreadyReminded.Contains(o.Id)).GroupBy(o => o.RecurringTaskId))
            {
                var task = group.First().RecurringTask!;
                var missed = group.Where(o => o.DueUtc <= now).OrderBy(o => o.DueUtc).ThenBy(o => o.SequenceNumber).ToList();
                if (missed.Count > 1)
                    raised.Add((NewReminder(task, missed[^1], now, SummaryMessage(task.Title, missed.Count, missed[0].DueDate, missed[^1].DueDate, missed.Count)), missed));
                else if (missed.Count == 1)
                    raised.Add((NewReminder(task, missed[0], now, BuildMessage(task, missed[0], now)), missed));
                foreach (var upcoming in group.Where(o => o.DueUtc > now).OrderBy(o => o.DueUtc))
                    raised.Add((NewReminder(task, upcoming, now, BuildMessage(task, upcoming, now)), [upcoming]));
            }
            foreach (var (reminder, _) in raised) db.TaskReminders.Add(reminder);
            await db.SaveChangesAsync(ct); // assigns reminder ids
            foreach (var (reminder, covers) in raised)
                foreach (var occurrence in covers)
                    db.TaskReminderOccurrences.Add(new TaskReminderOccurrence { ReminderId = reminder.Id, OccurrenceId = occurrence.Id, RecurringTaskId = occurrence.RecurringTaskId });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (raised.Count > 0) logger.LogInformation("Raised {Reminders} reminder(s) covering {Occurrences} occurrence(s)", raised.Count, due.Count);
            return (raised.Count, due.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reminder pass failed; it will be retried on the next scheduler tick");
            return (0, 0);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private static TaskReminder NewReminder(RecurringTask task, TaskOccurrence occurrence, DateTime now, string message) => new()
    {
        UserId = occurrence.UserId,
        RecurringTaskId = occurrence.RecurringTaskId,
        OccurrenceId = occurrence.Id,
        Title = task.Title,
        Message = message,
        DueUtc = occurrence.DueUtc,
        CreatedUtc = now
    };

    /// <summary>Text of a catch-up summary; <paramref name="open"/> is refreshed as covered occurrences are completed or reopened.</summary>
    public static string SummaryMessage(string title, int total, string firstDate, string lastDate, int open)
    {
        var range = $"\"{title}\" has {total} missed occurrences ({firstDate} – {lastDate})";
        return open == total ? $"{range} that are still open." : open == 0 ? $"{range}; all of them are completed." : $"{range}; {open} of them {(open == 1 ? "is" : "are")} still open.";
    }

    /// <summary>
    /// Called after an occurrence's completion state changed (and was saved). Every reminder covering it is brought up to
    /// date: a catch-up summary's message shows how many of its occurrences are still open, and a reminder is marked read
    /// only once *all* occurrences it covers are completed. Marking read is one-way: reopening an occurrence refreshes the
    /// text but does not un-dismiss a reminder the user has already seen settle. Reminders written by earlier versions have
    /// no link rows and fall back to the single occurrence they point at.
    /// </summary>
    public async Task RefreshRemindersAsync(TaskOccurrence occurrence, DateTime now, CancellationToken ct)
    {
        var linkedReminderIds = await db.TaskReminderOccurrences.Where(l => l.OccurrenceId == occurrence.Id).Select(l => l.ReminderId).ToListAsync(ct);
        var reminders = await db.TaskReminders.Where(r => r.OccurrenceId == occurrence.Id || linkedReminderIds.Contains(r.Id)).ToListAsync(ct);
        foreach (var reminder in reminders)
        {
            var coveredIds = await db.TaskReminderOccurrences.Where(l => l.ReminderId == reminder.Id).Select(l => l.OccurrenceId).ToListAsync(ct);
            if (coveredIds.Count == 0) coveredIds = [reminder.OccurrenceId];
            var covered = await db.TaskOccurrences.Where(o => coveredIds.Contains(o.Id)).OrderBy(o => o.DueUtc).Select(o => new { o.Completed, o.DueDate }).ToListAsync(ct);
            if (covered.Count == 0) continue;
            var open = covered.Count(o => !o.Completed);
            if (covered.Count > 1) reminder.Message = SummaryMessage(reminder.Title, covered.Count, covered[0].DueDate, covered[^1].DueDate, open);
            if (open == 0 && reminder.ReadUtc is null) reminder.ReadUtc = now;
        }
    }

    private static string BuildMessage(RecurringTask task, TaskOccurrence occurrence, DateTime now)
    {
        var when = $"{occurrence.DueDate} at {Recurrence.FormatTime(task.ParseDueTime())}" + (task.TimeZoneId == "UTC" ? " UTC" : $" ({task.TimeZoneId})");
        return occurrence.DueUtc <= now
            ? $"\"{task.Title}\" was due on {when} and is still open."
            : $"\"{task.Title}\" is due on {when}.";
    }

    private TimeZoneInfo ResolveZone(RecurringTask task)
    {
        if (Recurrence.TryResolveTimeZone(task.TimeZoneId, out var zone)) return zone;
        logger.LogWarning("Time zone {TimeZoneId} of recurring task {RecurringTaskId} is unknown on this machine; using UTC", task.TimeZoneId, task.Id);
        return TimeZoneInfo.Utc;
    }

    // ----- Projections shared by the endpoints -----

    public static IReadOnlyList<UpcomingOccurrenceDto> Preview(RecurrenceRule rule, TimeOnly time, TimeZoneInfo zone, DateTime now, int fromSequence, int count)
    {
        var first = Recurrence.FirstSequenceAfter(rule, time, zone, now, fromSequence);
        if (first is null) return Array.Empty<UpcomingOccurrenceDto>();
        return rule.Enumerate(first.Value).Take(count)
            .Select(x => new UpcomingOccurrenceDto(x.Sequence, Recurrence.FormatDate(x.Date), Recurrence.ToUtc(x.Date, time, zone)))
            .ToList();
    }

    public static RecurringTaskDto ToDto(RecurringTask task, DateTime now, int pending, int completed)
    {
        var rule = task.ToRule();
        var time = task.ParseDueTime();
        var zone = task.ResolveTimeZone();
        // Scan from the anchor: several upcoming occurrences may exist (long reminder lead), so NextSequence is no shortcut.
        var upcoming = task.IsActive ? Preview(rule, time, zone, now, 0, PreviewCount) : Array.Empty<UpcomingOccurrenceDto>();
        var next = upcoming.Count > 0 ? upcoming[0] : null;
        return new RecurringTaskDto(task.Id, task.Title, task.Category, task.Frequency, task.Interval, task.StartDate, task.DueTime, task.TimeZoneId,
            task.ReminderLeadMinutes, task.EndDate, task.IsActive, Recurrence.Describe(rule, time), Utc(task.CreatedUtc), next?.Date, next?.DueUtc,
            upcoming, pending, completed);
    }

    public static OccurrenceDto ToDto(TaskOccurrence o, RecurringTask task, DateTime now) =>
        new(o.Id, o.RecurringTaskId, task.Title, task.Category, o.SequenceNumber, o.DueDate, task.DueTime, task.TimeZoneId, Utc(o.DueUtc), o.Completed,
            Utc(o.CompletedUtc), Utc(o.ReminderAtUtc), Utc(o.ReminderSentUtc), o.Completed ? "completed" : o.DueUtc <= now ? "overdue" : "upcoming", task.IsActive);

    public static TaskReminderDto ToDto(TaskReminder r) =>
        new(r.Id, r.OccurrenceId, r.RecurringTaskId, r.Title, r.Message, Utc(r.DueUtc), Utc(r.CreatedUtc), Utc(r.ReadUtc));

    // SQLite stores DateTime without a kind; re-stamp UTC so JSON carries the trailing "Z" and browsers convert correctly.
    public static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    public static DateTime? Utc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

/// <summary>Runs a scheduler pass at startup (recovering anything missed while the API was stopped) and then periodically.</summary>
public sealed class RecurringTaskWorker(IServiceScopeFactory scopeFactory, ILogger<RecurringTaskWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<RecurringTaskScheduler>().RunOnceAsync(stoppingToken);
                if (result.OccurrencesCreated > 0 || result.RemindersCreated > 0)
                    logger.LogInformation("Recurring task pass created {Occurrences} occurrence(s) and {Reminders} reminder(s) covering {Reminded} occurrence(s)",
                        result.OccurrencesCreated, result.RemindersCreated, result.OccurrencesReminded);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Recurring task worker iteration failed"); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}

// ----- Endpoints -----

public static class RecurringTaskEndpoints
{
    public const int MaxOccurrencesListed = 500;
    public const int MaxRemindersListed = 200;
    public const int PreviewLength = 6;

    public static RouteGroupBuilder MapRecurringTasks(this RouteGroupBuilder secure)
    {
        secure.MapGet("/recurring-tasks", async (LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            var tasks = await db.RecurringTasks.Where(t => t.UserId == owner).OrderByDescending(t => t.IsActive).ThenBy(t => t.Id).ToListAsync(ct);
            var counts = await db.TaskOccurrences.Where(o => o.UserId == owner)
                .GroupBy(o => new { o.RecurringTaskId, o.Completed })
                .Select(g => new { g.Key.RecurringTaskId, g.Key.Completed, Count = g.Count() }).ToListAsync(ct);
            return Results.Ok(tasks.Select(t => RecurringTaskScheduler.ToDto(t, now,
                counts.Where(c => c.RecurringTaskId == t.Id && !c.Completed).Sum(c => c.Count),
                counts.Where(c => c.RecurringTaskId == t.Id && c.Completed).Sum(c => c.Count))).ToList());
        });

        secure.MapPost("/recurring-tasks/preview", (RecurringTaskRequest input, RecurringTaskScheduler scheduler) =>
        {
            var now = scheduler.UtcNow;
            var v = RecurringTaskValidation.Validate(input, now);
            return Results.Ok(new RecurrencePreviewDto(Recurrence.Describe(v.Rule, v.DueTime), v.TimeZoneId,
                RecurringTaskScheduler.Preview(v.Rule, v.DueTime, v.Zone, now, 0, PreviewLength)));
        });

        secure.MapPost("/recurring-tasks", async (RecurringTaskRequest input, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            var v = RecurringTaskValidation.Validate(input, now);
            var task = new RecurringTask
            {
                UserId = owner, Title = v.Title, Category = v.Category, Frequency = Recurrence.FrequencyName(v.Rule.Frequency), Interval = v.Rule.Interval,
                StartDate = Recurrence.FormatDate(v.Rule.StartDate), DueTime = Recurrence.FormatTime(v.DueTime), TimeZoneId = v.TimeZoneId,
                ReminderLeadMinutes = v.ReminderLeadMinutes, EndDate = v.Rule.EndDate is DateOnly end ? Recurrence.FormatDate(end) : null,
                IsActive = true, CreatedUtc = now,
                // Occurrences that would already be in the past when the schedule is created are skipped, not generated
                // as overdue: a daily 09:00 task created at 10:00 starts tomorrow. Validation guarantees a future one exists.
                NextSequence = Recurrence.FirstSequenceAfter(v.Rule, v.DueTime, v.Zone, now) ?? 0
            };
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            db.RecurringTasks.Add(task);
            await db.SaveChangesAsync(ct);
            var created = await scheduler.MaterializeAsync(task, now, ct);
            db.AuditEvents.Add(new AuditEvent { UserId = owner, Action = "recurring.created", EntityId = task.Id, OccurredUtc = now });
            TaskActivityLog.RecurringCreated(db, task, owner, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/recurring-tasks/{task.Id}", RecurringTaskScheduler.ToDto(task, now, pending: created, completed: 0));
        });

        secure.MapGet("/recurring-tasks/{id:int}", async (int id, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var task = await db.RecurringTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == owner, ct);
            if (task is null) return Results.NotFound();
            return Results.Ok(await ToDtoWithCounts(db, task, scheduler.UtcNow, ct));
        });

        secure.MapPost("/recurring-tasks/{id:int}/disable", async (int id, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var task = await db.RecurringTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == owner, ct);
            if (task is null) return Results.NotFound();
            if (task.IsActive)
            {
                task.IsActive = false;
                task.DisabledUtc = now;
                // Upcoming occurrences the user has not been told about yet are withdrawn (nothing new is due while
                // disabled). Overdue, completed and already-reminded occurrences stay: the user has seen them, and keeping
                // them means enabling the schedule again never repeats a reminder that was already shown or dismissed.
                var pending = await db.TaskOccurrences.Where(o => o.RecurringTaskId == task.Id && !o.Completed && o.DueUtc > now && o.ReminderSentUtc == null).ToListAsync(ct);
                var pendingIds = pending.Select(o => o.Id).ToList();
                var reminders = await db.TaskReminders.Where(r => pendingIds.Contains(r.OccurrenceId)).ToListAsync(ct);
                db.TaskReminderOccurrences.RemoveRange(await db.TaskReminderOccurrences.Where(l => pendingIds.Contains(l.OccurrenceId)).ToListAsync(ct));
                db.TaskReminders.RemoveRange(reminders);
                db.TaskOccurrences.RemoveRange(pending);
                db.AuditEvents.Add(new AuditEvent { UserId = owner, Action = "recurring.disabled", EntityId = task.Id, OccurredUtc = now });
                TaskActivityLog.RecurringActiveChanged(db, task, wasActive: true, owner, now); // history: edited, active true → false
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(await ToDtoWithCounts(db, task, now, ct));
        });

        secure.MapPost("/recurring-tasks/{id:int}/enable", async (int id, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var task = await db.RecurringTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == owner, ct);
            if (task is null) return Results.NotFound();
            if (!task.IsActive)
            {
                // Skip everything that would have been due while disabled and resume with the next future occurrence.
                // The stored rows (not NextSequence, which may have moved past occurrences that disable withdrew) decide
                // where to resume: a quick disable/enable keeps the same next date, and an occurrence that was kept
                // because its reminder was already shown is simply continued from, never re-created or re-reminded.
                var next = await scheduler.ResumeSequenceAsync(task, now, ct);
                // A schedule whose final occurrence is still upcoming (kept by disable because its reminder was shown)
                // has nothing new to generate but is not exhausted either.
                var hasPendingOccurrence = await db.TaskOccurrences.AnyAsync(o => o.RecurringTaskId == task.Id && !o.Completed && o.DueUtc > now, ct);
                if (next is null && !hasPendingOccurrence) throw new ValidationException("This schedule has no remaining occurrences and cannot be re-enabled.");
                if (next is int resume) task.NextSequence = resume;
                task.IsActive = true;
                task.DisabledUtc = null;
                await scheduler.MaterializeAsync(task, now, ct);
                db.AuditEvents.Add(new AuditEvent { UserId = owner, Action = "recurring.enabled", EntityId = task.Id, OccurredUtc = now });
                TaskActivityLog.RecurringActiveChanged(db, task, wasActive: false, owner, now); // history: edited, active false → true
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(await ToDtoWithCounts(db, task, now, ct));
        });

        secure.MapDelete("/recurring-tasks/{id:int}", async (int id, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var task = await db.RecurringTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == owner, ct);
            if (task is null) return Results.NotFound();
            await db.TaskReminderOccurrences.Where(l => l.RecurringTaskId == task.Id).ExecuteDeleteAsync(ct);
            await db.TaskReminders.Where(r => r.RecurringTaskId == task.Id).ExecuteDeleteAsync(ct);
            await db.TaskOccurrences.Where(o => o.RecurringTaskId == task.Id).ExecuteDeleteAsync(ct);
            db.RecurringTasks.Remove(task);
            var now = scheduler.UtcNow;
            db.AuditEvents.Add(new AuditEvent { UserId = owner, Action = "recurring.deleted", EntityId = task.Id, OccurredUtc = now });
            TaskActivityLog.RecurringDeleted(db, task, owner, now); // the occurrences go with the schedule; one history row
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        secure.MapGet("/task-occurrences", async (LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, bool? includeCompleted, int? recurringTaskId, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            var query = db.TaskOccurrences.Include(o => o.RecurringTask).Where(o => o.UserId == owner);
            if (includeCompleted != true) query = query.Where(o => !o.Completed);
            if (recurringTaskId is int taskId) query = query.Where(o => o.RecurringTaskId == taskId);
            var occurrences = await query.OrderBy(o => o.DueUtc).ThenBy(o => o.Id).Take(MaxOccurrencesListed).ToListAsync(ct);
            return Results.Ok(occurrences.Select(o => RecurringTaskScheduler.ToDto(o, o.RecurringTask!, now)).ToList());
        });

        secure.MapPut("/task-occurrences/{id:int}", async (int id, OccurrenceCompletionRequest input, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            if (input?.Completed is null) throw new ValidationException("Provide { \"completed\": true | false }.");
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            // Serializable so this cannot interleave with a reminder pass: either the pass sees the occurrence completed
            // and raises nothing, or it raised the reminder first and it is marked read below. Never both unread.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var occurrence = await db.TaskOccurrences.Include(o => o.RecurringTask).SingleOrDefaultAsync(o => o.Id == id && o.UserId == owner, ct);
            if (occurrence is null) return Results.NotFound();
            if (occurrence.Completed != input.Completed.Value)
            {
                occurrence.Completed = input.Completed.Value;
                occurrence.CompletedUtc = input.Completed.Value ? now : null;
                db.AuditEvents.Add(new AuditEvent { UserId = owner, Action = input.Completed.Value ? "occurrence.completed" : "occurrence.reopened", EntityId = occurrence.Id, OccurredUtc = now });
                TaskActivityLog.OccurrenceCompletionChanged(db, occurrence, occurrence.RecurringTask!, owner, now);
                await db.SaveChangesAsync(ct);
                // Reminders covering this occurrence are updated from the saved state: an ordinary reminder is settled, a
                // catch-up summary only once every missed occurrence it reports is completed (its text shows the rest).
                await scheduler.RefreshRemindersAsync(occurrence, now, ct);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(RecurringTaskScheduler.ToDto(occurrence, occurrence.RecurringTask!, now));
        });

        secure.MapGet("/task-reminders", async (LifeDb db, ClaimsPrincipal user, bool? unreadOnly, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var query = db.TaskReminders.Where(r => r.UserId == owner);
            if (unreadOnly == true) query = query.Where(r => r.ReadUtc == null);
            var reminders = await query.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id).Take(MaxRemindersListed).ToListAsync(ct);
            return Results.Ok(reminders.Select(RecurringTaskScheduler.ToDto).ToList());
        });

        secure.MapPost("/task-reminders/{id:int}/read", async (int id, LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var reminder = await db.TaskReminders.SingleOrDefaultAsync(r => r.Id == id && r.UserId == owner, ct);
            if (reminder is null) return Results.NotFound();
            if (reminder.ReadUtc is null) { reminder.ReadUtc = scheduler.UtcNow; await db.SaveChangesAsync(ct); }
            return Results.Ok(RecurringTaskScheduler.ToDto(reminder));
        });

        secure.MapPost("/task-reminders/read-all", async (LifeDb db, ClaimsPrincipal user, RecurringTaskScheduler scheduler, CancellationToken ct) =>
        {
            var owner = OwnerId(user);
            var now = scheduler.UtcNow;
            var unread = await db.TaskReminders.Where(r => r.UserId == owner && r.ReadUtc == null).ToListAsync(ct);
            foreach (var reminder in unread) reminder.ReadUtc = now;
            if (unread.Count > 0) await db.SaveChangesAsync(ct);
            return Results.Ok(new { updated = unread.Count });
        });

        return secure;
    }

    private static async Task<RecurringTaskDto> ToDtoWithCounts(LifeDb db, RecurringTask task, DateTime now, CancellationToken ct)
    {
        var pending = await db.TaskOccurrences.CountAsync(o => o.RecurringTaskId == task.Id && !o.Completed, ct);
        var completed = await db.TaskOccurrences.CountAsync(o => o.RecurringTaskId == task.Id && o.Completed, ct);
        return RecurringTaskScheduler.ToDto(task, now, pending, completed);
    }

    private static int OwnerId(ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
}

/// <summary>
/// Database initialisation that is safe to run from several API instances at once and that adds tables introduced by
/// newer versions to an existing database. <c>EnsureCreated()</c> alone is neither: two instances starting on a brand-new
/// file both see no tables and both run the CREATE script, so the second one crashes with "table already exists"; and it
/// does nothing at all when the file already has tables. Column changes to existing tables are still not handled (README).
/// </summary>
public static class SchemaBootstrap
{
    public static async Task InitializeAsync(LifeDb db, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await db.Database.EnsureCreatedAsync(ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Another instance created the schema between our "no tables yet" check and our CREATE; its transaction won.
            logger.LogInformation("Database schema was created concurrently by another instance; continuing");
        }
        // Idempotent on every start: adds any table or index that is missing (new version on an old database, or the
        // loser of the race above) and is a no-op otherwise. BEGIN IMMEDIATE serialises concurrent starters.
        var script = db.Database.GenerateCreateScript();
        var idempotent = System.Text.RegularExpressions.Regex.Replace(script, @"^\s*CREATE (TABLE|UNIQUE INDEX|INDEX) ", "CREATE $1 IF NOT EXISTS ", System.Text.RegularExpressions.RegexOptions.Multiline);
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await db.Database.ExecuteSqlRawAsync(idempotent, ct);
            await tx.CommitAsync(ct);
        }
        // Fail fast with a clear message instead of "no such table" errors at the first request.
        var expected = db.Model.GetEntityTypes().Select(t => t.GetTableName()).Where(n => n is not null).Select(n => n!).ToList();
        var present = (await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = expected.Where(t => !present.Contains(t)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Database schema is incomplete; missing table(s): {string.Join(", ", missing)}. See the database note in README.md.");
    }
}
