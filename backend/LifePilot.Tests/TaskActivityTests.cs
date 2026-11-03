using Xunit;

/// <summary>Unit tests for the database-free parts of the task activity history: change detection and serialisation.</summary>
public class TaskActivityChangesTests
{
    private static ItemSnapshot Snapshot(string title = "Buy milk", string category = "Home", decimal amount = 0, string? date = "2026-01-20", bool done = false) =>
        new(title, category, amount, date, done);

    [Fact]
    public void IdenticalSnapshotsProduceNoChanges()
    {
        Assert.Empty(TaskActivityChanges.Diff(Snapshot(), Snapshot()));
    }

    [Fact]
    public void EveryChangedFieldIsReportedWithItsOldAndNewValue()
    {
        var before = Snapshot(title: "Buy milk", category: "Home", amount: 2.5m, date: "2026-01-20");
        var after = Snapshot(title: "Buy oat milk", category: "Groceries", amount: 3m, date: "2026-01-21");
        var changes = TaskActivityChanges.Diff(before, after);
        Assert.Equal(new[] { "title", "category", "amount", "date" }, changes.Select(c => c.Field).ToArray());
        Assert.Equal(new FieldChange("title", "Buy milk", "Buy oat milk"), changes[0]);
        Assert.Equal(new FieldChange("category", "Home", "Groceries"), changes[1]);
        Assert.Equal(new FieldChange("amount", "2.5", "3"), changes[2]);
        Assert.Equal(new FieldChange("date", "2026-01-20", "2026-01-21"), changes[3]);
    }

    [Fact]
    public void OnlyTheFieldsThatChangedAreReported()
    {
        var changes = TaskActivityChanges.Diff(Snapshot(), Snapshot(category: "Errands"));
        var only = Assert.Single(changes);
        Assert.Equal("category", only.Field);
        Assert.Equal("Home", only.OldValue);
        Assert.Equal("Errands", only.NewValue);
    }

    [Fact]
    public void ClearingAndSettingTheDateUseNullForNotSet()
    {
        var cleared = Assert.Single(TaskActivityChanges.Diff(Snapshot(date: "2026-01-20"), Snapshot(date: null)));
        Assert.Equal(new FieldChange("date", "2026-01-20", null), cleared);
        var set = Assert.Single(TaskActivityChanges.Diff(Snapshot(date: null), Snapshot(date: "2026-02-01")));
        Assert.Equal(new FieldChange("date", null, "2026-02-01"), set);
    }

    [Fact]
    public void TheDoneFlagIsNotAnEdit()
    {
        // Completing and reopening are recorded as their own actions by TaskActivityLog.ItemUpdated, never as an edit.
        Assert.Empty(TaskActivityChanges.Diff(Snapshot(done: false), Snapshot(done: true)));
    }

    [Fact]
    public void AmountsAreFormattedCultureIndependently()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); // would print 1234,5
            Assert.Equal("1234.5", TaskActivityChanges.FormatAmount(1234.5m));
            var change = Assert.Single(TaskActivityChanges.Diff(Snapshot(amount: 0), Snapshot(amount: 1234.5m)));
            Assert.Equal("0", change.OldValue);
            Assert.Equal("1234.5", change.NewValue);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0.0", "0")]          // how SQLite hands back a stored 0 (EF Core stores decimals as text "0.0###…")
    [InlineData("3.0", "3")]
    [InlineData("3", "3")]
    [InlineData("12.50", "12.5")]     // scale as sent by a client
    [InlineData("12.5", "12.5")]
    [InlineData("1234.5000", "1234.5")]
    [InlineData("0.00001", "0.00001")] // no scientific notation
    [InlineData("100000000", "100000000")]
    [InlineData("0.1234567890123456789012345678", "0.1234567890123456789012345678")] // full decimal precision kept
    public void AmountFormattingIgnoresScaleSoStoredAndRequestedValuesLookTheSame(string value, string expected)
    {
        Assert.Equal(expected, TaskActivityChanges.FormatAmount(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ScaleAloneIsNotAChange()
    {
        // 3 written to the database comes back as 3.0; a client may resend 3.00. None of these is an edit.
        Assert.Empty(TaskActivityChanges.Diff(Snapshot(amount: 3.0m), Snapshot(amount: 3m)));
        Assert.Empty(TaskActivityChanges.Diff(Snapshot(amount: 0.0m), Snapshot(amount: 0m)));
        Assert.Empty(TaskActivityChanges.Diff(Snapshot(amount: 12.5m), Snapshot(amount: 12.500m)));
        var change = Assert.Single(TaskActivityChanges.Diff(Snapshot(amount: 0.0m), Snapshot(amount: 12.50m)));
        Assert.Equal(new FieldChange("amount", "0", "12.5"), change);
    }

    [Fact]
    public void ChangesRoundTripThroughJsonInTheApiShape()
    {
        var changes = new[] { new FieldChange("title", "Old", "New"), new FieldChange("date", "2026-01-20", null) };
        var json = TaskActivityChanges.Serialize(changes);
        Assert.NotNull(json);
        // Stored exactly as the API exposes it (camelCase, explicit nulls), so rows can be read without translation.
        Assert.Contains("\"field\":\"title\"", json);
        Assert.Contains("\"oldValue\":\"Old\"", json);
        Assert.Contains("\"newValue\":null", json);
        Assert.Equal(changes, TaskActivityChanges.Deserialize(json));
    }

    [Fact]
    public void ValuesWithQuotesBackslashesAndNonAsciiSurviveStorage()
    {
        var changes = new[] { new FieldChange("title", "Say \"hi\" \\ <b>", "Café → naïve 日本語"), new FieldChange("category", null, "") };
        Assert.Equal(changes, TaskActivityChanges.Deserialize(TaskActivityChanges.Serialize(changes)));
        var change = Assert.Single(TaskActivityChanges.Diff(Snapshot(title: "Say \"hi\""), Snapshot(title: "Café → naïve")));
        Assert.Equal(new FieldChange("title", "Say \"hi\"", "Café → naïve"), change);
    }

    [Fact]
    public void NothingToSerialiseIsStoredAsNull()
    {
        Assert.Null(TaskActivityChanges.Serialize(null));
        Assert.Null(TaskActivityChanges.Serialize(Array.Empty<FieldChange>()));
        Assert.Empty(TaskActivityChanges.Deserialize(null));
        Assert.Empty(TaskActivityChanges.Deserialize(""));
    }

    [Fact]
    public void UnreadableStoredJsonYieldsNoChangesInsteadOfFailing()
    {
        Assert.Empty(TaskActivityChanges.Deserialize("{not json"));
        Assert.Empty(TaskActivityChanges.Deserialize("null"));
    }

    [Fact]
    public void DtoCarriesUtcTimestampsAndDeserialisedChanges()
    {
        var row = new TaskActivity
        {
            Id = 7, UserId = 1, ActorUserId = 1, TaskType = "task", TaskId = 3, Title = "Buy milk", Action = "edited",
            ChangesJson = TaskActivityChanges.Serialize([new FieldChange("title", "Milk", "Buy milk")]),
            OccurredUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Unspecified) // as SQLite hands it back
        };
        var dto = TaskActivityChanges.ToDto(row, "me@test.local");
        Assert.Equal(DateTimeKind.Utc, dto.OccurredUtc.Kind);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc), dto.OccurredUtc);
        Assert.Equal("me@test.local", dto.ActorEmail);
        Assert.Equal(new FieldChange("title", "Milk", "Buy milk"), Assert.Single(dto.Changes));
        Assert.Empty(TaskActivityChanges.ToDto(new TaskActivity(), null).Changes);
    }

    [Theory]
    [InlineData("created", true)]
    [InlineData("edited", true)]
    [InlineData("completed", true)]
    [InlineData("reopened", true)]
    [InlineData("deleted", true)]
    [InlineData("Created", false)]
    [InlineData("archived", false)]
    public void OnlyTheFiveActionsAreKnown(string action, bool known)
    {
        Assert.Equal(known, TaskActivityActions.IsKnown(action));
    }

    [Theory]
    [InlineData("task", true)]
    [InlineData("grocery", true)]
    [InlineData("event", true)]
    [InlineData("recurring", true)]
    [InlineData("occurrence", true)]
    [InlineData("item", false)]
    [InlineData("", false)]
    public void TaskTypesMatchItemKindsPlusRecurringAndOccurrence(string type, bool known)
    {
        Assert.Equal(known, TaskActivityTypes.IsKnown(type));
    }
}
