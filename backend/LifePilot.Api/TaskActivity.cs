using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

// Task activity history.
//
// Every user-initiated change to a task is recorded as a TaskActivity row: creation, edits (with the old and new value
// of every field that changed), completion, reopening and deletion. Rows carry who made the change, when, and a
// snapshot of the task's title, so the history stays readable after the task itself is gone. Three kinds of task are
// covered because they share the same life cycle in the UI: one-off items (kind task / grocery / event), recurring
// schedules and their individual occurrences. Changes the scheduler makes on its own (materialising occurrences,
// raising reminders) are deliberately not history: the history answers "what did the user do?".
//
// The table is append-only and is written in the same transaction / SaveChanges as the change it describes, so a
// change and its history row either both exist or neither does. The existing AuditEvents table (a minimal security
// log without values) is unchanged; TaskActivities is the user-facing, detailed history.

// ----- Entity -----

public class TaskActivity
{
    public int Id { get; set; }
    /// <summary>Owner of the task. History is listed per owner; with no sharing this is also the actor.</summary>
    public int UserId { get; set; }
    /// <summary>The user who made the change.</summary>
    public int ActorUserId { get; set; }
    /// <summary>task | grocery | event (one-off items, by kind) | recurring (a schedule) | occurrence.</summary>
    public string TaskType { get; set; } = "";
    /// <summary>Id of the item, schedule or occurrence. It may no longer exist.</summary>
    public int TaskId { get; set; }
    /// <summary>Title of the task when the change happened.</summary>
    public string Title { get; set; } = "";
    /// <summary>Short context, e.g. the due date of an occurrence or the description of a schedule. Optional.</summary>
    public string? Detail { get; set; }
    /// <summary>created | edited | completed | reopened | deleted.</summary>
    public string Action { get; set; } = "";
    /// <summary>JSON array of <see cref="FieldChange"/> (camelCase) for edits; null when nothing beyond the action applies.</summary>
    public string? ChangesJson { get; set; }
    public DateTime OccurredUtc { get; set; }
}

public static class TaskActivityActions
{
    public const string Created = "created";
    public const string Edited = "edited";
    public const string Completed = "completed";
    public const string Reopened = "reopened";
    public const string Deleted = "deleted";
    public static readonly IReadOnlyList<string> All = [Created, Edited, Completed, Reopened, Deleted];
    public static bool IsKnown(string value) => All.Contains(value);
}

public static class TaskActivityTypes
{
    public const string Recurring = "recurring";
    public const string Occurrence = "occurrence";
    /// <summary>One-off items are typed by their kind so the history can be filtered the same way the item lists are.</summary>
    public static readonly IReadOnlyList<string> All = ["task", "grocery", "event", Recurring, Occurrence];
    public static bool IsKnown(string value) => All.Contains(value);
}

// ----- API contracts -----

/// <summary>One field of an edit. Values are strings as shown to the user (amounts in invariant culture, booleans as
/// "true"/"false"); null means "not set", e.g. a cleared date.</summary>
public sealed record FieldChange(string Field, string? OldValue, string? NewValue);

public sealed record TaskActivityDto(int Id, string TaskType, int TaskId, string Title, string? Detail, string Action,
    int ActorUserId, string? ActorEmail, DateTime OccurredUtc, IReadOnlyList<FieldChange> Changes);

// ----- Pure helpers (no database; unit-testable) -----

/// <summary>The editable fields of a one-off item, captured before and after an update to compute the changes.</summary>
public sealed record ItemSnapshot(string Title, string Category, decimal Amount, string? Date, bool Done)
{
    public static ItemSnapshot Of(LifeItem item) => new(item.Title, item.Category, item.Amount, item.Date, item.Done);
}

public static class TaskActivityChanges
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Field-by-field differences between two snapshots, excluding <see cref="ItemSnapshot.Done"/>: completing or
    /// reopening is recorded as its own action, not as an edit. Returns an empty list when nothing changed, so an
    /// update that re-sends the current values leaves no history.
    /// </summary>
    public static IReadOnlyList<FieldChange> Diff(ItemSnapshot before, ItemSnapshot after)
    {
        var changes = new List<FieldChange>();
        if (before.Title != after.Title) changes.Add(new FieldChange("title", before.Title, after.Title));
        if (before.Category != after.Category) changes.Add(new FieldChange("category", before.Category, after.Category));
        if (before.Amount != after.Amount) changes.Add(new FieldChange("amount", FormatAmount(before.Amount), FormatAmount(after.Amount)));
        if (before.Date != after.Date) changes.Add(new FieldChange("date", before.Date, after.Date));
        return changes;
    }

    /// <summary>
    /// Amounts as shown in the history. The format is scale-independent: SQLite hands decimals back with a different
    /// scale than they were written with (EF Core stores them as text in the form <c>0.0###…</c>, so 0 is read back as
    /// 0.0, 3 as 3.0), while a request value keeps whatever scale the client sent (12.50). Plain <c>ToString</c> keeps
    /// the scale, so the same amount would read "3.0" as an old value (from the database) and "3" as a new value (from
    /// the request). Dropping trailing zeros makes equal numbers render identically wherever they came from, and the
    /// 28 optional decimals cover decimal's full precision without rounding or scientific notation.
    /// </summary>
    public static string FormatAmount(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    public static string FormatBool(bool value) => value ? "true" : "false";

    public static string? Serialize(IReadOnlyList<FieldChange>? changes) =>
        changes is null || changes.Count == 0 ? null : JsonSerializer.Serialize(changes, Json);

    /// <summary>Never throws: a row whose JSON cannot be read (hand-edited database) is shown without changes.</summary>
    public static IReadOnlyList<FieldChange> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<FieldChange>();
        try { return JsonSerializer.Deserialize<List<FieldChange>>(json, Json) ?? []; }
        catch (JsonException) { return Array.Empty<FieldChange>(); }
    }

    public static TaskActivityDto ToDto(TaskActivity a, string? actorEmail) =>
        new(a.Id, a.TaskType, a.TaskId, a.Title, a.Detail, a.Action, a.ActorUserId, actorEmail,
            DateTime.SpecifyKind(a.OccurredUtc, DateTimeKind.Utc), Deserialize(a.ChangesJson));
}

// ----- Recording -----

/// <summary>
/// Adds history rows to the <see cref="LifeDb"/> change tracker. Callers save (and commit) together with the change
/// itself; nothing here touches the database directly.
/// </summary>
public static class TaskActivityLog
{
    public static TaskActivity Record(LifeDb db, int ownerId, int actorId, string taskType, int taskId, string title, string action, DateTime now,
        IReadOnlyList<FieldChange>? changes = null, string? detail = null)
    {
        var activity = new TaskActivity
        {
            UserId = ownerId, ActorUserId = actorId, TaskType = taskType, TaskId = taskId, Title = title, Detail = detail,
            Action = action, ChangesJson = TaskActivityChanges.Serialize(changes), OccurredUtc = now
        };
        db.TaskActivities.Add(activity);
        return activity;
    }

    // One-off items. The item must already have its Id (i.e. be saved) when recording creation.
    public static TaskActivity ItemCreated(LifeDb db, LifeItem item, int actorId, DateTime now, string? detail = null) =>
        Record(db, item.UserId, actorId, item.Kind, item.Id, item.Title, TaskActivityActions.Created, now, detail: detail);

    /// <summary>
    /// Records what an update did: an "edited" row listing every changed field (old → new), and/or a "completed" or
    /// "reopened" row when the done flag flipped. An update that changed nothing records nothing.
    /// </summary>
    public static IReadOnlyList<TaskActivity> ItemUpdated(LifeDb db, ItemSnapshot before, LifeItem item, int actorId, DateTime now)
    {
        var recorded = new List<TaskActivity>();
        var changes = TaskActivityChanges.Diff(before, ItemSnapshot.Of(item));
        if (changes.Count > 0)
            recorded.Add(Record(db, item.UserId, actorId, item.Kind, item.Id, item.Title, TaskActivityActions.Edited, now, changes));
        if (before.Done != item.Done)
            recorded.Add(Record(db, item.UserId, actorId, item.Kind, item.Id, item.Title, item.Done ? TaskActivityActions.Completed : TaskActivityActions.Reopened, now));
        return recorded;
    }

    public static TaskActivity ItemDeleted(LifeDb db, LifeItem item, int actorId, DateTime now) =>
        Record(db, item.UserId, actorId, item.Kind, item.Id, item.Title, TaskActivityActions.Deleted, now);

    // Recurring schedules. Enabling and disabling are edits of the schedule's "active" flag.
    public static TaskActivity RecurringCreated(LifeDb db, RecurringTask task, int actorId, DateTime now) =>
        Record(db, task.UserId, actorId, TaskActivityTypes.Recurring, task.Id, task.Title, TaskActivityActions.Created, now, detail: Describe(task));

    public static TaskActivity RecurringActiveChanged(LifeDb db, RecurringTask task, bool wasActive, int actorId, DateTime now) =>
        Record(db, task.UserId, actorId, TaskActivityTypes.Recurring, task.Id, task.Title, TaskActivityActions.Edited, now,
            [new FieldChange("active", TaskActivityChanges.FormatBool(wasActive), TaskActivityChanges.FormatBool(task.IsActive))], Describe(task));

    public static TaskActivity RecurringDeleted(LifeDb db, RecurringTask task, int actorId, DateTime now) =>
        Record(db, task.UserId, actorId, TaskActivityTypes.Recurring, task.Id, task.Title, TaskActivityActions.Deleted, now, detail: Describe(task));

    // Occurrences carry their schedule's title and their own due date as context.
    public static TaskActivity OccurrenceCompletionChanged(LifeDb db, TaskOccurrence occurrence, RecurringTask task, int actorId, DateTime now) =>
        Record(db, occurrence.UserId, actorId, TaskActivityTypes.Occurrence, occurrence.Id, task.Title,
            occurrence.Completed ? TaskActivityActions.Completed : TaskActivityActions.Reopened, now, detail: $"Due {occurrence.DueDate}");

    private static string Describe(RecurringTask task) => Recurrence.Describe(task.ToRule(), task.ParseDueTime());
}

// ----- Endpoint -----

public static class TaskActivityEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>
    /// GET /api/task-activity?taskType=&amp;taskId=&amp;action=&amp;limit=&amp;beforeId= — the caller's history, newest
    /// first. <c>beforeId</c> is a cursor for paging: pass the smallest id of the previous page to get the next one.
    /// </summary>
    public static RouteGroupBuilder MapTaskActivity(this RouteGroupBuilder secure)
    {
        secure.MapGet("/task-activity", async (LifeDb db, ClaimsPrincipal user, string? taskType, int? taskId, string? action, int? limit, int? beforeId, CancellationToken ct) =>
        {
            var owner = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
            if (taskType is not null && !TaskActivityTypes.IsKnown(taskType))
                throw new ValidationException($"taskType must be one of: {string.Join(", ", TaskActivityTypes.All)}.");
            if (action is not null && !TaskActivityActions.IsKnown(action))
                throw new ValidationException($"action must be one of: {string.Join(", ", TaskActivityActions.All)}.");
            // Items, schedules and occurrences have separate id spaces, so an id alone does not identify a task.
            if (taskId is not null && taskType is null) throw new ValidationException("taskType is required when filtering by taskId.");
            var take = limit ?? DefaultLimit;
            if (take < 1 || take > MaxLimit) throw new ValidationException($"limit must be between 1 and {MaxLimit}.");

            var query = db.TaskActivities.Where(a => a.UserId == owner);
            if (taskType is not null) query = query.Where(a => a.TaskType == taskType);
            if (taskId is int id) query = query.Where(a => a.TaskId == id);
            if (action is not null) query = query.Where(a => a.Action == action);
            if (beforeId is int before) query = query.Where(a => a.Id < before);
            var rows = await query.OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);

            // Actor e-mail is resolved at read time rather than copied into every row.
            var actorIds = rows.Select(a => a.ActorUserId).Distinct().ToList();
            var actors = (await db.Users.Where(u => actorIds.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync(ct))
                .ToDictionary(u => u.Id, u => u.Email);
            return Results.Ok(rows.Select(a => TaskActivityChanges.ToDto(a, actors.GetValueOrDefault(a.ActorUserId))).ToList());
        });
        return secure;
    }
}
