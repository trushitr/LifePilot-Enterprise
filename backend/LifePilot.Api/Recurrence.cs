using System.Globalization;

// Pure recurrence calculations. Nothing in this file touches the database, the clock or ASP.NET Core, so the rules
// can be unit-tested exhaustively and reasoned about in isolation.

public enum RecurrenceFrequency { Daily, Weekly, Monthly }

/// <summary>
/// Describes when a recurring task is due. Occurrences are addressed by a zero-based <c>sequence</c> number and every
/// date is derived from the anchor (<see cref="StartDate"/>) rather than from the previous occurrence, so the schedule
/// never drifts. In particular a monthly rule anchored on the 31st keeps asking for "the 31st, or the last day of
/// shorter months": Jan 31 → Feb 28/29 → Mar 31 → Apr 30 → May 31.
/// </summary>
public sealed record RecurrenceRule(RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate)
{
    public const int MaxDailyInterval = 365;
    public const int MaxWeeklyInterval = 52;
    public const int MaxMonthlyInterval = 12;

    public static int MaxInterval(RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Daily => MaxDailyInterval,
        RecurrenceFrequency.Weekly => MaxWeeklyInterval,
        _ => MaxMonthlyInterval
    };

    public static RecurrenceRule Create(RecurrenceFrequency frequency, int interval, DateOnly startDate, DateOnly? endDate)
    {
        if (interval < 1 || interval > MaxInterval(frequency))
            throw new ArgumentOutOfRangeException(nameof(interval), $"Interval must be between 1 and {MaxInterval(frequency)} for {frequency} recurrence.");
        if (endDate is not null && endDate < startDate)
            throw new ArgumentOutOfRangeException(nameof(endDate), "End date must not be before the start date.");
        return new RecurrenceRule(frequency, interval, startDate, endDate);
    }

    /// <summary>
    /// The local due date of occurrence number <paramref name="sequence"/> (0 = the start date), or null when the
    /// occurrence lies beyond <see cref="EndDate"/> or the supported calendar range.
    /// </summary>
    public DateOnly? DateAt(int sequence)
    {
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        DateOnly date;
        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
            case RecurrenceFrequency.Weekly:
            {
                var days = (long)sequence * Interval * (Frequency == RecurrenceFrequency.Weekly ? 7 : 1);
                if (StartDate.DayNumber + days > DateOnly.MaxValue.DayNumber) return null;
                date = StartDate.AddDays((int)days);
                break;
            }
            default:
            {
                // Month arithmetic on a zero-based month index; the day is clamped to the target month's length but the
                // *anchor* day is always StartDate.Day, so a schedule for the 31st returns to the 31st after February.
                var monthIndex = (long)StartDate.Year * 12 + (StartDate.Month - 1) + (long)sequence * Interval;
                var year = (int)(monthIndex / 12);
                var month = (int)(monthIndex % 12) + 1;
                if (year > DateOnly.MaxValue.Year) return null;
                var day = Math.Min(StartDate.Day, DateTime.DaysInMonth(year, month));
                date = new DateOnly(year, month, day);
                break;
            }
        }
        if (EndDate is not null && date > EndDate) return null;
        return date;
    }

    /// <summary>
    /// The smallest sequence number whose date is on or after <paramref name="date"/> (0 when the date is not after the
    /// anchor). Computed arithmetically so callers never have to walk years of past occurrences; the result may be one
    /// past the end of the rule, in which case <see cref="DateAt"/> returns null for it.
    /// </summary>
    public int FirstSequenceOnOrAfter(DateOnly date)
    {
        if (date <= StartDate) return 0;
        long lowerBound;
        if (Frequency == RecurrenceFrequency.Monthly)
        {
            var months = (long)(date.Year * 12 + date.Month) - (StartDate.Year * 12 + StartDate.Month);
            lowerBound = Math.Max(0, (months - 1) / Interval); // at most one interval too early because of day clamping
        }
        else
        {
            var step = (long)Interval * (Frequency == RecurrenceFrequency.Weekly ? 7 : 1);
            lowerBound = (date.DayNumber - StartDate.DayNumber + step - 1) / step; // exact: ceil(diff / step)
        }
        var sequence = (int)Math.Min(lowerBound, int.MaxValue - 2);
        while (DateAt(sequence) is DateOnly d && d < date) sequence++;
        return sequence;
    }

    /// <summary>Enumerates occurrence dates starting at <paramref name="fromSequence"/> until the rule ends.</summary>
    public IEnumerable<(int Sequence, DateOnly Date)> Enumerate(int fromSequence = 0)
    {
        for (var sequence = Math.Max(0, fromSequence); ; sequence++)
        {
            var date = DateAt(sequence);
            if (date is null) yield break;
            yield return (sequence, date.Value);
        }
    }
}

public static class Recurrence
{
    public const string DateFormat = "yyyy-MM-dd";
    private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss", "H:mm"];

    /// <summary>Safety cap for the "first future occurrence" scan; it normally finishes within a handful of steps.</summary>
    public const int MaxSkipScan = 366 * 20;

    public static bool TryParseFrequency(string? value, out RecurrenceFrequency frequency)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "daily": frequency = RecurrenceFrequency.Daily; return true;
            case "weekly": frequency = RecurrenceFrequency.Weekly; return true;
            case "monthly": frequency = RecurrenceFrequency.Monthly; return true;
            default: frequency = default; return false;
        }
    }

    public static string FrequencyName(RecurrenceFrequency frequency) => frequency.ToString().ToLowerInvariant();

    public static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);
    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves an IANA ("Europe/Berlin") or Windows ("W. Europe Standard Time") time zone id. Null/blank means UTC.
    /// </summary>
    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var found) && found is not null)
        {
            zone = found;
            return true;
        }
        zone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>
    /// Converts a local wall-clock due time to a UTC instant. A time that does not exist because clocks sprang forward
    /// (e.g. 02:30 on the DST-start day) is moved to the first valid instant after the gap; an ambiguous time (clocks
    /// fell back) resolves to standard time, which is what <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> does.
    /// </summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        for (var attempts = 0; attempts < 12 && zone.IsInvalidTime(local); attempts++)
            local = local.AddMinutes(15);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    /// <summary>
    /// When the reminder for an occurrence fires. Leads that are whole days (1440, 2880, …) mean "at the task's due time
    /// of day, N days earlier, in the schedule's time zone", so a "1 day before" reminder for a 09:00 task is at 09:00
    /// local the day before even when clocks change overnight. Shorter leads are exact durations before the due instant
    /// (a 60-minute reminder is always 60 real minutes before), because wall-clock arithmetic inside a DST transition
    /// hour would be meaningless.
    /// </summary>
    public static DateTime ReminderAt(DateOnly dueDate, TimeOnly dueTime, TimeZoneInfo zone, int leadMinutes)
    {
        if (leadMinutes < 0) throw new ArgumentOutOfRangeException(nameof(leadMinutes));
        if (leadMinutes > 0 && leadMinutes % MinutesPerDay == 0)
            return ToUtc(dueDate.AddDays(-leadMinutes / MinutesPerDay), dueTime, zone);
        return ToUtc(dueDate, dueTime, zone).AddMinutes(-leadMinutes);
    }

    public const int MinutesPerDay = 24 * 60;

    public static DateOnly LocalToday(DateTime utcNow, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone));

    /// <summary>
    /// The first sequence number whose due instant is after <paramref name="utcNow"/>, or null when every remaining
    /// occurrence is already in the past (the rule has effectively ended). Used when a schedule is created or
    /// re-enabled so that occurrences which would have been due while the schedule did not exist / was disabled are
    /// skipped instead of appearing as overdue.
    /// </summary>
    public static int? FirstSequenceAfter(RecurrenceRule rule, TimeOnly time, TimeZoneInfo zone, DateTime utcNow, int fromSequence = 0)
    {
        // Jump straight to the occurrences around "now" (a day of slack covers every time zone offset) instead of
        // converting years of past dates; the remaining scan is only a few steps for any age of schedule.
        var start = Math.Max(fromSequence, rule.FirstSequenceOnOrAfter(LocalToday(utcNow, zone).AddDays(-2)));
        var scanned = 0;
        foreach (var (sequence, date) in rule.Enumerate(start))
        {
            if (ToUtc(date, time, zone) > utcNow) return sequence;
            if (++scanned >= MaxSkipScan) return null;
        }
        return null;
    }

    /// <summary>Human-readable summary, e.g. "Every 2 weeks at 09:00".</summary>
    public static string Describe(RecurrenceRule rule, TimeOnly time)
    {
        var unit = rule.Frequency switch { RecurrenceFrequency.Daily => "day", RecurrenceFrequency.Weekly => "week", _ => "month" };
        var every = rule.Interval == 1 ? $"Every {unit}" : $"Every {rule.Interval} {unit}s";
        var anchor = rule.Frequency switch
        {
            RecurrenceFrequency.Weekly => $" on {rule.StartDate.DayOfWeek}",
            RecurrenceFrequency.Monthly => $" on day {rule.StartDate.Day}",
            _ => ""
        };
        return $"{every}{anchor} at {FormatTime(time)}";
    }
}
