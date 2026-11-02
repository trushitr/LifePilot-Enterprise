using System.Text.Json.Serialization;

// Recurring tasks: entities, API contracts and validation. This file deliberately depends only on the BCL (no EF Core,
// no ASP.NET Core) so the rules can be unit-tested without a database. Persistence, scheduling and endpoints live in
// RecurringTasks.cs.

// ----- Entities -----

public class RecurringTask
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>"daily" | "weekly" | "monthly".</summary>
    public string Frequency { get; set; } = "daily";
    public int Interval { get; set; } = 1;
    /// <summary>Anchor date, yyyy-MM-dd in the schedule's time zone. Monthly schedules keep this day of month.</summary>
    public string StartDate { get; set; } = "";
    /// <summary>Wall-clock due time, HH:mm.</summary>
    public string DueTime { get; set; } = "09:00";
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>Minutes before the due time at which the reminder fires; null disables reminders.</summary>
    public int? ReminderLeadMinutes { get; set; }
    public string? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Sequence number of the next occurrence the scheduler will materialise.</summary>
    public int NextSequence { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DisabledUtc { get; set; }

    public RecurrenceRule ToRule()
    {
        if (!Recurrence.TryParseFrequency(Frequency, out var frequency)) frequency = RecurrenceFrequency.Daily;
        Recurrence.TryParseDate(StartDate, out var start);
        DateOnly? end = Recurrence.TryParseDate(EndDate, out var parsedEnd) ? parsedEnd : null;
        var interval = Math.Clamp(Interval, 1, RecurrenceRule.MaxInterval(frequency));
        return new RecurrenceRule(frequency, interval, start, end is not null && end < start ? null : end);
    }

    public TimeOnly ParseDueTime() => Recurrence.TryParseTime(DueTime, out var time) ? time : new TimeOnly(9, 0);

    public TimeZoneInfo ResolveTimeZone() => Recurrence.TryResolveTimeZone(TimeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;
}

public class TaskOccurrence
{
    public int Id { get; set; }
    public int RecurringTaskId { get; set; }
    [JsonIgnore] public RecurringTask? RecurringTask { get; set; }
    public int UserId { get; set; }
    public int SequenceNumber { get; set; }
    /// <summary>Local due date, yyyy-MM-dd.</summary>
    public string DueDate { get; set; } = "";
    public DateTime DueUtc { get; set; }
    public DateTime? ReminderAtUtc { get; set; }
    public DateTime? ReminderSentUtc { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public class TaskReminder
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int RecurringTaskId { get; set; }
    public int OccurrenceId { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime DueUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ReadUtc { get; set; }
}

/// <summary>
/// Which occurrences a reminder covers. An ordinary reminder covers the one occurrence it is linked to; a catch-up
/// summary covers every missed occurrence it reports, so it is only settled when all of them are completed.
/// </summary>
public class TaskReminderOccurrence
{
    public int Id { get; set; }
    public int ReminderId { get; set; }
    public int OccurrenceId { get; set; }
    public int RecurringTaskId { get; set; }
}

// ----- API contracts -----

public record RecurringTaskRequest(string? Title, string? Category, string? Frequency, int? Interval, string? StartDate,
    string? DueTime, string? TimeZoneId, int? ReminderLeadMinutes, string? EndDate);
public record OccurrenceCompletionRequest(bool? Completed);

public record UpcomingOccurrenceDto(int Sequence, string Date, DateTime DueUtc);
public record RecurringTaskDto(int Id, string Title, string Category, string Frequency, int Interval, string StartDate, string DueTime,
    string TimeZoneId, int? ReminderLeadMinutes, string? EndDate, bool IsActive, string Description, DateTime CreatedUtc,
    string? NextDueDate, DateTime? NextDueUtc, IReadOnlyList<UpcomingOccurrenceDto> Upcoming, int PendingCount, int CompletedCount);
public record RecurrencePreviewDto(string Description, string TimeZoneId, IReadOnlyList<UpcomingOccurrenceDto> Upcoming);
public record OccurrenceDto(int Id, int RecurringTaskId, string Title, string Category, int SequenceNumber, string DueDate, string DueTime,
    string TimeZoneId, DateTime DueUtc, bool Completed, DateTime? CompletedUtc, DateTime? ReminderAtUtc, DateTime? ReminderSentUtc, string Status,
    bool ScheduleActive);
public record TaskReminderDto(int Id, int OccurrenceId, int RecurringTaskId, string Title, string Message, DateTime DueUtc, DateTime CreatedUtc, DateTime? ReadUtc);
/// <summary><paramref name="OccurrencesReminded"/> counts occurrences whose reminder was handled; several missed occurrences of one
/// schedule share a single catch-up reminder, so it can exceed <paramref name="RemindersCreated"/>.</summary>
public record SchedulerPassResult(int OccurrencesCreated, int RemindersCreated, int OccurrencesReminded);

// ----- Validation -----

public sealed record ValidatedRecurringTask(string Title, string Category, RecurrenceRule Rule, TimeOnly DueTime, TimeZoneInfo Zone,
    string TimeZoneId, int? ReminderLeadMinutes);

public static class RecurringTaskValidation
{
    public const int MaxTitleLength = 200;
    public const int MaxCategoryLength = 100;
    public const int MaxReminderLeadMinutes = 30 * 24 * 60;
    public const int MaxYearsInPast = 10;
    public const int MaxYearsInFuture = 10;

    /// <summary>Checks every field and throws <see cref="ValidationException"/> (→ HTTP 400) on the first problem.</summary>
    public static ValidatedRecurringTask Validate(RecurringTaskRequest? input, DateTime utcNow)
    {
        if (input is null) throw new ValidationException("Provide a recurring task definition.");
        var title = input.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength) throw new ValidationException($"Title must be 1–{MaxTitleLength} characters.");
        var category = input.Category?.Trim() ?? "";
        if (category.Length > MaxCategoryLength) throw new ValidationException($"Category must be at most {MaxCategoryLength} characters.");
        if (!Recurrence.TryParseFrequency(input.Frequency, out var frequency)) throw new ValidationException("Frequency must be daily, weekly or monthly.");
        var interval = input.Interval ?? 1;
        if (interval < 1 || interval > RecurrenceRule.MaxInterval(frequency))
            throw new ValidationException($"Interval must be between 1 and {RecurrenceRule.MaxInterval(frequency)} for {Recurrence.FrequencyName(frequency)} recurrence.");
        if (!Recurrence.TryParseDate(input.StartDate, out var startDate)) throw new ValidationException("Start date must be in yyyy-MM-dd format.");
        if (!Recurrence.TryParseTime(input.DueTime, out var dueTime)) throw new ValidationException("Due time must be in HH:mm format.");
        var timeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? "UTC" : input.TimeZoneId.Trim();
        if (timeZoneId.Length > 100 || !Recurrence.TryResolveTimeZone(timeZoneId, out var zone)) throw new ValidationException("Unknown time zone.");
        if (input.ReminderLeadMinutes is < 0 or > MaxReminderLeadMinutes)
            throw new ValidationException($"Reminder lead time must be between 0 and {MaxReminderLeadMinutes} minutes.");
        DateOnly? endDate = null;
        if (!string.IsNullOrWhiteSpace(input.EndDate))
        {
            if (!Recurrence.TryParseDate(input.EndDate, out var parsedEnd)) throw new ValidationException("End date must be in yyyy-MM-dd format.");
            if (parsedEnd < startDate) throw new ValidationException("End date must not be before the start date.");
            endDate = parsedEnd;
        }
        var today = Recurrence.LocalToday(utcNow, zone);
        if (startDate < today.AddYears(-MaxYearsInPast) || startDate > today.AddYears(MaxYearsInFuture))
            throw new ValidationException($"Start date must be within {MaxYearsInPast} years in the past and {MaxYearsInFuture} years in the future.");
        var rule = RecurrenceRule.Create(frequency, interval, startDate, endDate);
        if (Recurrence.FirstSequenceAfter(rule, dueTime, zone, utcNow) is null)
            throw new ValidationException("This schedule has no future occurrences; choose a later start or end date.");
        return new ValidatedRecurringTask(title, category, rule, dueTime, zone, timeZoneId, input.ReminderLeadMinutes);
    }
}
