using Xunit;

/// <summary>Pure recurrence-rule tests: no database, no HTTP, no real clock.</summary>
public class RecurrenceTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTime Utc(string iso) => DateTime.SpecifyKind(DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
    private static string[] Dates(RecurrenceRule rule, int count) => rule.Enumerate().Take(count).Select(x => Recurrence.FormatDate(x.Date)).ToArray();

    [Fact]
    public void DailyRuleAdvancesByInterval()
    {
        var daily = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-30"), null);
        Assert.Equal(new[] { "2026-01-30", "2026-01-31", "2026-02-01", "2026-02-02" }, Dates(daily, 4));

        var everyThirdDay = RecurrenceRule.Create(RecurrenceFrequency.Daily, 3, D("2026-12-30"), null);
        Assert.Equal(new[] { "2026-12-30", "2027-01-02", "2027-01-05" }, Dates(everyThirdDay, 3));
    }

    [Fact]
    public void WeeklyRuleKeepsTheWeekday()
    {
        var weekly = RecurrenceRule.Create(RecurrenceFrequency.Weekly, 1, D("2026-01-19"), null); // a Monday
        var dates = weekly.Enumerate().Take(6).Select(x => x.Date).ToArray();
        Assert.All(dates, d => Assert.Equal(DayOfWeek.Monday, d.DayOfWeek));
        Assert.Equal(D("2026-02-23"), dates[5]);

        var fortnightly = RecurrenceRule.Create(RecurrenceFrequency.Weekly, 2, D("2026-01-19"), null);
        Assert.Equal(new[] { "2026-01-19", "2026-02-02", "2026-02-16" }, Dates(fortnightly, 3));
    }

    [Fact]
    public void MonthlyRulePreservesTheIntendedDayOfMonth()
    {
        // The headline requirement: Jan 31 → Feb 28 → Mar 31, and back to the 31st whenever the month has one.
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-31"), null);
        Assert.Equal(new[] { "2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30", "2026-05-31", "2026-06-30", "2026-07-31" }, Dates(rule, 7));
    }

    [Fact]
    public void MonthlyRuleUsesFebruary29InLeapYears()
    {
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2027-12-31"), null);
        Assert.Equal(new[] { "2027-12-31", "2028-01-31", "2028-02-29", "2028-03-31" }, Dates(rule, 4));
    }

    [Fact]
    public void MonthlyRuleAnchoredOn30thAnd29th()
    {
        Assert.Equal(new[] { "2026-01-30", "2026-02-28", "2026-03-30" }, Dates(RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-30"), null), 3));
        Assert.Equal(new[] { "2026-01-29", "2026-02-28", "2026-03-29" }, Dates(RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-29"), null), 3));
        // A day that every month has is never clamped.
        Assert.Equal(new[] { "2026-01-15", "2026-02-15", "2026-03-15" }, Dates(RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-15"), null), 3));
    }

    [Fact]
    public void MonthlyRuleWithIntervalAndYearRollover()
    {
        var quarterly = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 3, D("2026-11-30"), null);
        Assert.Equal(new[] { "2026-11-30", "2027-02-28", "2027-05-30", "2027-08-30", "2027-11-30" }, Dates(quarterly, 5));

        var yearly = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 12, D("2028-02-29"), null);
        Assert.Equal(new[] { "2028-02-29", "2029-02-28", "2030-02-28", "2031-02-28", "2032-02-29" }, Dates(yearly, 5));
    }

    [Fact]
    public void DateAtIsIndexedFromTheAnchorSoItNeverDrifts()
    {
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-31"), null);
        Assert.Equal(D("2026-01-31"), rule.DateAt(0));
        Assert.Equal(D("2026-02-28"), rule.DateAt(1));
        Assert.Equal(D("2026-03-31"), rule.DateAt(2));
        Assert.Equal(D("2027-01-31"), rule.DateAt(12));
        Assert.Equal(D("2036-01-31"), rule.DateAt(120));
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.DateAt(-1));
    }

    [Fact]
    public void EndDateTerminatesTheRule()
    {
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-01"), D("2026-01-03"));
        Assert.Equal(new[] { "2026-01-01", "2026-01-02", "2026-01-03" }, Dates(rule, 10));
        Assert.Null(rule.DateAt(3));
        // An end date equal to the start date yields exactly one occurrence.
        Assert.Single(RecurrenceRule.Create(RecurrenceFrequency.Weekly, 1, D("2026-01-01"), D("2026-01-01")).Enumerate());
    }

    [Fact]
    public void CreateRejectsInvalidIntervalsAndEndDates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceRule.Create(RecurrenceFrequency.Daily, 0, D("2026-01-01"), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceRule.Create(RecurrenceFrequency.Daily, 366, D("2026-01-01"), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceRule.Create(RecurrenceFrequency.Weekly, 53, D("2026-01-01"), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceRule.Create(RecurrenceFrequency.Monthly, 13, D("2026-01-01"), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-02"), D("2026-01-01")));
        Assert.NotNull(RecurrenceRule.Create(RecurrenceFrequency.Monthly, 12, D("2026-01-01"), null));
    }

    [Fact]
    public void RuleNearTheEndOfTheCalendarStopsInsteadOfOverflowing()
    {
        var daily = RecurrenceRule.Create(RecurrenceFrequency.Daily, 365, D("9999-01-01"), null);
        Assert.Equal(D("9999-01-01"), daily.DateAt(0));
        Assert.Null(daily.DateAt(1));
        var monthly = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 12, D("9999-06-30"), null);
        Assert.Null(monthly.DateAt(1));
    }

    [Theory]
    [InlineData("daily", true)]
    [InlineData("Weekly", true)]
    [InlineData(" MONTHLY ", true)]
    [InlineData("yearly", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FrequencyParsingIsCaseInsensitiveAndStrict(string? input, bool expected) =>
        Assert.Equal(expected, Recurrence.TryParseFrequency(input, out _));

    [Theory]
    [InlineData("2026-01-05", true)]
    [InlineData("2026-1-5", false)]
    [InlineData("05/01/2026", false)]
    [InlineData("2026-02-30", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DateParsingRequiresIsoFormat(string? input, bool expected) =>
        Assert.Equal(expected, Recurrence.TryParseDate(input, out _));

    [Theory]
    [InlineData("09:00", true)]
    [InlineData("9:00", true)]
    [InlineData("23:59:30", true)]
    [InlineData("24:00", false)]
    [InlineData("9am", false)]
    [InlineData("", false)]
    public void TimeParsingAcceptsHoursAndMinutes(string input, bool expected) =>
        Assert.Equal(expected, Recurrence.TryParseTime(input, out _));

    [Fact]
    public void TimeZoneResolutionTreatsBlankAsUtcAndRejectsUnknownIds()
    {
        Assert.True(Recurrence.TryResolveTimeZone(null, out var utc));
        Assert.Equal(TimeZoneInfo.Utc, utc);
        Assert.True(Recurrence.TryResolveTimeZone("utc", out _));
        Assert.False(Recurrence.TryResolveTimeZone("Mars/Olympus_Mons", out _));
        Assert.True(Recurrence.TryResolveTimeZone("Europe/Berlin", out var berlin));
        Assert.NotEqual(TimeZoneInfo.Utc, berlin);
    }

    [Fact]
    public void LocalTimesConvertToUtcAcrossDaylightSaving()
    {
        Assert.True(Recurrence.TryResolveTimeZone("Europe/Berlin", out var berlin));
        var nine = new TimeOnly(9, 0);
        Assert.Equal(Utc("2026-01-15T08:00:00Z"), Recurrence.ToUtc(D("2026-01-15"), nine, berlin)); // CET, UTC+1
        Assert.Equal(Utc("2026-07-15T07:00:00Z"), Recurrence.ToUtc(D("2026-07-15"), nine, berlin)); // CEST, UTC+2
        Assert.Equal(Utc("2026-07-15T09:00:00Z"), Recurrence.ToUtc(D("2026-07-15"), nine, TimeZoneInfo.Utc));
        Assert.Equal(DateTimeKind.Utc, Recurrence.ToUtc(D("2026-07-15"), nine, berlin).Kind);
    }

    [Fact]
    public void NonExistentLocalTimeMovesToTheFirstValidInstantAfterTheGap()
    {
        // US DST starts 2026-03-08 at 02:00 local: 02:00–02:59 do not exist. 02:30 becomes 03:00 EDT = 07:00Z.
        Assert.True(Recurrence.TryResolveTimeZone("America/New_York", out var newYork));
        Assert.Equal(Utc("2026-03-08T07:00:00Z"), Recurrence.ToUtc(D("2026-03-08"), new TimeOnly(2, 30), newYork));
        // The day before, 02:30 EST is simply 07:30Z; the day after, 02:30 EDT is 06:30Z.
        Assert.Equal(Utc("2026-03-07T07:30:00Z"), Recurrence.ToUtc(D("2026-03-07"), new TimeOnly(2, 30), newYork));
        Assert.Equal(Utc("2026-03-09T06:30:00Z"), Recurrence.ToUtc(D("2026-03-09"), new TimeOnly(2, 30), newYork));
    }

    [Fact]
    public void AmbiguousLocalTimeResolvesToStandardTime()
    {
        // US DST ends 2026-11-01 at 02:00 local: 01:30 happens twice. Standard time (EST, UTC-5) is chosen: 06:30Z.
        Assert.True(Recurrence.TryResolveTimeZone("America/New_York", out var newYork));
        Assert.Equal(Utc("2026-11-01T06:30:00Z"), Recurrence.ToUtc(D("2026-11-01"), new TimeOnly(1, 30), newYork));
    }

    [Fact]
    public void SouthernHemisphereDaylightSavingAndDateLineAreHandled()
    {
        // New Zealand leaves DST on 2026-04-05 (03:00 NZDT → 02:00 NZST) and re-enters it on 2026-09-27 (02:00 → 03:00).
        Assert.True(Recurrence.TryResolveTimeZone("Pacific/Auckland", out var auckland));
        var eight = new TimeOnly(8, 0);
        Assert.Equal(Utc("2026-04-03T19:00:00Z"), Recurrence.ToUtc(D("2026-04-04"), eight, auckland)); // NZDT, UTC+13: previous UTC day
        Assert.Equal(Utc("2026-04-04T20:00:00Z"), Recurrence.ToUtc(D("2026-04-05"), eight, auckland)); // NZST, UTC+12
        // 02:30 does not exist on the spring-forward day; it becomes 03:00 NZDT.
        Assert.Equal(Utc("2026-09-26T14:00:00Z"), Recurrence.ToUtc(D("2026-09-27"), new TimeOnly(2, 30), auckland));
        // A daily 08:00 rule keeps 08:00 wall-clock across the change, so consecutive UTC instants are 25 hours apart.
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-04-03"), null);
        var instants = rule.Enumerate().Take(4).Select(x => Recurrence.ToUtc(x.Date, eight, auckland)).ToArray();
        Assert.Equal(TimeSpan.FromHours(24), instants[1] - instants[0]);
        Assert.Equal(TimeSpan.FromHours(25), instants[2] - instants[1]);
        Assert.Equal(TimeSpan.FromHours(24), instants[3] - instants[2]);
    }

    [Fact]
    public void WholeDayReminderLeadsFollowTheWallClockAcrossDaylightSaving()
    {
        Assert.True(Recurrence.TryResolveTimeZone("America/New_York", out var newYork));
        var nine = new TimeOnly(9, 0);
        // Due 09:00 EDT on the spring-forward day (13:00Z); "1 day before" is 09:00 EST the day before (14:00Z), not 24 real hours.
        Assert.Equal(Utc("2026-03-07T14:00:00Z"), Recurrence.ReminderAt(D("2026-03-08"), nine, newYork, 1440));
        Assert.Equal(Utc("2026-03-06T14:00:00Z"), Recurrence.ReminderAt(D("2026-03-08"), nine, newYork, 2880));
        // Autumn: clocks fall back at 02:00 on Nov 1, so 09:00 on Nov 1 is EST (14:00Z) while 09:00 the day before is EDT (13:00Z).
        Assert.Equal(Utc("2026-11-01T14:00:00Z"), Recurrence.ToUtc(D("2026-11-01"), nine, newYork));
        Assert.Equal(Utc("2026-10-31T13:00:00Z"), Recurrence.ReminderAt(D("2026-11-01"), nine, newYork, 1440));
        // Without a transition in between, a whole-day lead equals the exact duration.
        Assert.Equal(Recurrence.ToUtc(D("2026-06-10"), nine, newYork).AddDays(-1), Recurrence.ReminderAt(D("2026-06-10"), nine, newYork, 1440));
        // A whole-day lead that lands on a non-existent local time is moved past the gap like any due time.
        Assert.Equal(Utc("2026-03-08T07:00:00Z"), Recurrence.ReminderAt(D("2026-03-09"), new TimeOnly(2, 30), newYork, 1440));
    }

    [Fact]
    public void ShorterReminderLeadsAreExactDurationsBeforeTheDueInstant()
    {
        Assert.True(Recurrence.TryResolveTimeZone("America/New_York", out var newYork));
        // 03:30 EDT on the switch-over day is 07:30Z; 60 minutes before is 06:30Z (01:30 EST), not a wall-clock 02:30 that never exists.
        Assert.Equal(Utc("2026-03-08T06:30:00Z"), Recurrence.ReminderAt(D("2026-03-08"), new TimeOnly(3, 30), newYork, 60));
        Assert.Equal(Utc("2026-03-08T07:00:00Z"), Recurrence.ReminderAt(D("2026-03-08"), new TimeOnly(3, 30), newYork, 30));
        // Zero lead is the due instant itself; a non-whole-day lead longer than a day is still exact.
        Assert.Equal(Utc("2026-03-08T07:30:00Z"), Recurrence.ReminderAt(D("2026-03-08"), new TimeOnly(3, 30), newYork, 0));
        Assert.Equal(Utc("2026-03-08T13:00:00Z").AddMinutes(-1500), Recurrence.ReminderAt(D("2026-03-08"), new TimeOnly(9, 0), newYork, 1500));
        Assert.Throws<ArgumentOutOfRangeException>(() => Recurrence.ReminderAt(D("2026-03-08"), new TimeOnly(9, 0), newYork, -1));
    }

    [Fact]
    public void LocalTodayFollowsTheZoneNotTheServer()
    {
        Assert.True(Recurrence.TryResolveTimeZone("Pacific/Auckland", out var auckland));
        Assert.Equal(D("2026-01-16"), Recurrence.LocalToday(Utc("2026-01-15T23:30:00Z"), auckland));
        Assert.Equal(D("2026-01-15"), Recurrence.LocalToday(Utc("2026-01-15T23:30:00Z"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void FirstSequenceAfterSkipsOccurrencesThatAreAlreadyInThePast()
    {
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-15"), null);
        var nine = new TimeOnly(9, 0);
        Assert.Equal(0, Recurrence.FirstSequenceAfter(rule, nine, TimeZoneInfo.Utc, Utc("2026-01-15T08:59:59Z")));
        Assert.Equal(1, Recurrence.FirstSequenceAfter(rule, nine, TimeZoneInfo.Utc, Utc("2026-01-15T09:00:00Z"))); // exactly due counts as past
        Assert.Equal(3, Recurrence.FirstSequenceAfter(rule, nine, TimeZoneInfo.Utc, Utc("2026-01-18T08:00:00Z")));
        Assert.Equal(5, Recurrence.FirstSequenceAfter(rule, nine, TimeZoneInfo.Utc, Utc("2026-01-18T08:00:00Z"), fromSequence: 5));

        var ended = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-15"), D("2026-01-16"));
        Assert.Null(Recurrence.FirstSequenceAfter(ended, nine, TimeZoneInfo.Utc, Utc("2026-01-16T09:00:00Z")));

        // A monthly anchor in the past still yields the correct *next* clamped date.
        var monthly = RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-31"), null);
        var next = Recurrence.FirstSequenceAfter(monthly, nine, TimeZoneInfo.Utc, Utc("2026-02-10T00:00:00Z"));
        Assert.Equal(1, next);
        Assert.Equal(D("2026-02-28"), monthly.DateAt(next!.Value));
    }

    [Fact]
    public void DescribeSummarisesTheRule()
    {
        Assert.Equal("Every day at 09:00", Recurrence.Describe(RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-15"), null), new TimeOnly(9, 0)));
        Assert.Equal("Every 2 weeks on Monday at 18:30", Recurrence.Describe(RecurrenceRule.Create(RecurrenceFrequency.Weekly, 2, D("2026-01-19"), null), new TimeOnly(18, 30)));
        Assert.Equal("Every month on day 31 at 07:05", Recurrence.Describe(RecurrenceRule.Create(RecurrenceFrequency.Monthly, 1, D("2026-01-31"), null), new TimeOnly(7, 5)));
    }
}

/// <summary>Request validation for recurring tasks (also pure: no database, fixed clock).</summary>
public class RecurringTaskValidationTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static RecurringTaskRequest Valid(string? title = "Water plants", string? frequency = "daily", int? interval = 1, string? startDate = "2026-01-15",
        string? dueTime = "09:00", string? timeZoneId = "UTC", int? lead = 30, string? endDate = null, string? category = "Home") =>
        new(title, category, frequency, interval, startDate, dueTime, timeZoneId, lead, endDate);

    [Fact]
    public void ValidRequestIsNormalised()
    {
        var v = RecurringTaskValidation.Validate(Valid(title: "  Water plants  ", frequency: "Monthly", interval: null, startDate: "2026-01-31", timeZoneId: " Europe/Berlin ", category: null), Now);
        Assert.Equal("Water plants", v.Title);
        Assert.Equal("", v.Category);
        Assert.Equal(RecurrenceFrequency.Monthly, v.Rule.Frequency);
        Assert.Equal(1, v.Rule.Interval);
        Assert.Equal(new DateOnly(2026, 1, 31), v.Rule.StartDate);
        Assert.Equal("Europe/Berlin", v.TimeZoneId);
        Assert.Equal(30, v.ReminderLeadMinutes);
    }

    [Fact]
    public void NullBodyIsRejected() => Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(null, Now));

    [Theory]
    [InlineData("", "Title")]
    [InlineData("   ", "Title")]
    public void BlankTitleIsRejected(string title, string expectedWord)
    {
        var error = Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(title: title), Now));
        Assert.Contains(expectedWord, error.Message);
    }

    [Fact]
    public void OverlongTitleAndCategoryAreRejected()
    {
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(title: new string('x', 201)), Now));
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(category: new string('x', 101)), Now));
        Assert.NotNull(RecurringTaskValidation.Validate(Valid(title: new string('x', 200), category: new string('y', 100)), Now));
    }

    [Theory]
    [InlineData("hourly")]
    [InlineData("")]
    [InlineData((string?)null)]
    public void UnknownFrequencyIsRejected(string? frequency) =>
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(frequency: frequency), Now));

    [Theory]
    [InlineData("daily", 0)]
    [InlineData("daily", 366)]
    [InlineData("weekly", 53)]
    [InlineData("monthly", 13)]
    [InlineData("monthly", -1)]
    public void IntervalOutOfRangeIsRejected(string frequency, int interval) =>
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(frequency: frequency, interval: interval), Now));

    [Theory]
    [InlineData("15/01/2026", "09:00")]
    [InlineData("2026-01-15T09:00", "09:00")]
    [InlineData(null, "09:00")]
    [InlineData("2026-01-15", "9 am")]
    [InlineData("2026-01-15", "25:00")]
    [InlineData("2026-01-15", null)]
    public void MalformedDateOrTimeIsRejected(string? startDate, string? dueTime) =>
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(startDate: startDate, dueTime: dueTime), Now));

    [Fact]
    public void UnknownTimeZoneIsRejectedAndBlankMeansUtc()
    {
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(timeZoneId: "Nowhere/Imaginary"), Now));
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(timeZoneId: new string('a', 101)), Now));
        Assert.Equal("UTC", RecurringTaskValidation.Validate(Valid(timeZoneId: null), Now).TimeZoneId);
        Assert.Equal("UTC", RecurringTaskValidation.Validate(Valid(timeZoneId: "  "), Now).TimeZoneId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(43201)]
    public void ReminderLeadOutOfRangeIsRejected(int lead) =>
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(lead: lead), Now));

    [Fact]
    public void ReminderLeadBoundariesAreAccepted()
    {
        Assert.Equal(0, RecurringTaskValidation.Validate(Valid(lead: 0), Now).ReminderLeadMinutes);
        Assert.Equal(43200, RecurringTaskValidation.Validate(Valid(lead: 43200), Now).ReminderLeadMinutes);
        Assert.Null(RecurringTaskValidation.Validate(Valid(lead: null), Now).ReminderLeadMinutes);
    }

    [Fact]
    public void EndDateMustNotPrecedeStartAndMustBeWellFormed()
    {
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(startDate: "2026-01-20", endDate: "2026-01-19"), Now));
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(endDate: "soon"), Now));
        Assert.Equal(new DateOnly(2026, 1, 20), RecurringTaskValidation.Validate(Valid(startDate: "2026-01-20", endDate: "2026-01-20"), Now).Rule.EndDate);
    }

    [Fact]
    public void StartDateMustBeWithinTenYears()
    {
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(startDate: "2015-12-31"), Now));
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(startDate: "2036-01-16"), Now));
        Assert.NotNull(RecurringTaskValidation.Validate(Valid(startDate: "2036-01-15"), Now));
    }

    [Fact]
    public void ScheduleWithoutAnyFutureOccurrenceIsRejected()
    {
        // Starts and ends before "now": nothing would ever be scheduled.
        var error = Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(startDate: "2026-01-10", endDate: "2026-01-14"), Now));
        Assert.Contains("no future occurrences", error.Message);
        // Today's occurrence already passed, but the end date allows tomorrow's: fine.
        Assert.NotNull(RecurringTaskValidation.Validate(Valid(startDate: "2026-01-15", dueTime: "08:00", endDate: "2026-01-16"), Now));
        // Today's occurrence has already passed and it is the only one: rejected.
        Assert.Throws<ValidationException>(() => RecurringTaskValidation.Validate(Valid(startDate: "2026-01-15", dueTime: "08:00", endDate: "2026-01-15"), Now));
    }

    [Fact]
    public void PastStartDatesAreAllowedSoMonthlyAnchorsCanBeInThePast()
    {
        // Anchor on Jan 31 while it is Feb 10: the next occurrence is Feb 28, then Mar 31.
        var v = RecurringTaskValidation.Validate(Valid(frequency: "monthly", startDate: "2026-01-31"), new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc));
        var first = Recurrence.FirstSequenceAfter(v.Rule, v.DueTime, v.Zone, new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, first);
        Assert.Equal(new DateOnly(2026, 2, 28), v.Rule.DateAt(1));
        Assert.Equal(new DateOnly(2026, 3, 31), v.Rule.DateAt(2));
    }
}

/// <summary>Arithmetic positioning used to avoid scanning years of past occurrences.</summary>
public class RecurrenceSeekTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("daily", 1, "2026-01-15", "2026-01-15", 0)]
    [InlineData("daily", 1, "2026-01-15", "2025-12-01", 0)]
    [InlineData("daily", 1, "2026-01-15", "2026-01-16", 1)]
    [InlineData("daily", 3, "2026-01-15", "2026-01-17", 1)]   // Jan 18 is the first on/after Jan 17
    [InlineData("daily", 3, "2026-01-15", "2026-01-18", 1)]
    [InlineData("daily", 3, "2026-01-15", "2026-01-19", 2)]
    [InlineData("weekly", 2, "2026-01-19", "2026-02-01", 1)]  // Feb 2
    [InlineData("weekly", 2, "2026-01-19", "2026-02-02", 1)]
    [InlineData("weekly", 2, "2026-01-19", "2026-02-03", 2)]  // Feb 16
    [InlineData("monthly", 1, "2026-01-31", "2026-02-01", 1)] // Feb 28
    [InlineData("monthly", 1, "2026-01-31", "2026-02-28", 1)]
    [InlineData("monthly", 1, "2026-01-31", "2026-03-01", 2)] // Mar 31
    [InlineData("monthly", 1, "2026-01-31", "2026-03-31", 2)]
    [InlineData("monthly", 3, "2026-11-30", "2027-03-01", 2)] // Feb 28 2027 passed → May 30 2027
    [InlineData("monthly", 12, "2028-02-29", "2029-02-28", 1)]
    [InlineData("monthly", 12, "2028-02-29", "2029-03-01", 2)]
    public void FirstSequenceOnOrAfterMatchesTheScan(string frequency, int interval, string start, string date, int expected)
    {
        Assert.True(Recurrence.TryParseFrequency(frequency, out var f));
        var rule = RecurrenceRule.Create(f, interval, D(start), null);
        Assert.Equal(expected, rule.FirstSequenceOnOrAfter(D(date)));
        // Cross-check against brute force: the chosen date is on/after the target and the previous one (if any) is before it.
        Assert.True(rule.DateAt(expected) >= D(date));
        if (expected > 0) Assert.True(rule.DateAt(expected - 1) < D(date));
    }

    [Fact]
    public void SeekingPastTheEndOfTheRuleIsReportedAsNull()
    {
        var ended = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("2026-01-01"), D("2026-01-03"));
        var sequence = ended.FirstSequenceOnOrAfter(D("2026-02-01"));
        Assert.Null(ended.DateAt(sequence));
        Assert.Null(Recurrence.FirstSequenceAfter(ended, new TimeOnly(9, 0), TimeZoneInfo.Utc, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void FirstSequenceAfterWorksForSchedulesOlderThanTheScanCap()
    {
        // A daily schedule anchored 60 years back (far beyond the 20-year scan cap) still resolves, and agrees with
        // the brute-force definition: the first sequence whose instant is after "now".
        var rule = RecurrenceRule.Create(RecurrenceFrequency.Daily, 1, D("1966-03-01"), null);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var nine = new TimeOnly(9, 0);
        var sequence = Recurrence.FirstSequenceAfter(rule, nine, TimeZoneInfo.Utc, now);
        Assert.NotNull(sequence);
        Assert.Equal(D("2026-01-16"), rule.DateAt(sequence!.Value));
        Assert.True(Recurrence.ToUtc(rule.DateAt(sequence.Value - 1)!.Value, nine, TimeZoneInfo.Utc) <= now);

        // Across the date line the local "today" is ahead of UTC; the two-day slack keeps the answer exact.
        Assert.True(Recurrence.TryResolveTimeZone("Pacific/Auckland", out var auckland));
        var late = new DateTime(2026, 1, 15, 20, 30, 0, DateTimeKind.Utc); // 09:30 Jan 16 in Auckland
        var next = Recurrence.FirstSequenceAfter(rule, nine, auckland, late);
        Assert.Equal(D("2026-01-17"), rule.DateAt(next!.Value));
        Assert.True(Recurrence.TryResolveTimeZone("Pacific/Honolulu", out var honolulu));
        var early = new DateTime(2026, 1, 16, 5, 0, 0, DateTimeKind.Utc); // 19:00 Jan 15 in Honolulu
        Assert.Equal(D("2026-01-16"), rule.DateAt(Recurrence.FirstSequenceAfter(rule, nine, honolulu, early)!.Value));
    }
}
