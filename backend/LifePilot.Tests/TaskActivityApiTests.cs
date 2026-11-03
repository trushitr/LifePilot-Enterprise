using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// End-to-end tests for the task activity history: every user action on one-off items, recurring schedules and
/// occurrences is recorded with actor, time and (for edits) old and new values, and can be read back per user.
/// Each test gets its own SQLite database and a fake clock starting at 2026-01-15 10:00 UTC.
/// </summary>
public class TaskActivityApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public sealed record ChangeDto(string Field, string? OldValue, string? NewValue);
    public sealed record ActivityDto(int Id, string TaskType, int TaskId, string Title, string? Detail, string Action, int ActorUserId, string? ActorEmail,
        DateTime OccurredUtc, List<ChangeDto> Changes);
    public sealed record ItemDto(int Id, string Kind, string Title, string Category, decimal Amount, string? Date, bool Done);
    public sealed record ScheduleDto(int Id, string Title, bool IsActive, string Description, int PendingCount);
    public sealed record OccurrenceDto(int Id, int RecurringTaskId, string Title, string DueDate, bool Completed, string Status);
    public sealed record ReminderDto(int Id, int OccurrenceId, string Message, DateTime? ReadUtc);

    private static Task RunAsync(Func<WebApplicationFactory<Program>, HttpClient, FakeClock, Task> test)
    {
        var clock = new FakeClock(Start);
        return TestApp.RunIsolatedAsync((app, client) => test(app, client, clock), clock);
    }

    private static async Task<ItemDto> CreateItemAsync(HttpClient client, string title, string kind = "task", string? category = "Home", string? date = "2026-01-20", string? key = null, decimal? amount = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/items")
        {
            Content = JsonContent.Create(new { kind, title, category, amount, date })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await TestApp.ReadAsync<ItemDto>(await client.SendAsync(request), HttpStatusCode.Created);
    }

    private static async Task<ItemDto> UpdateItemAsync(HttpClient client, int id, object patch) =>
        await TestApp.ReadAsync<ItemDto>(await client.PutAsJsonAsync($"/api/items/{id}", patch));

    private static async Task<List<ActivityDto>> HistoryAsync(HttpClient client, string query = "") =>
        await TestApp.ReadAsync<List<ActivityDto>>(await client.GetAsync("/api/task-activity" + query));

    private static object Schedule(string title = "Water plants", string startDate = "2026-01-15", int? reminderLeadMinutes = 30, string? endDate = null) =>
        new { title, category = "Home", frequency = "daily", interval = 1, startDate, dueTime = "09:00", timeZoneId = "UTC", reminderLeadMinutes, endDate };

    private static void AssertAt(DateTimeOffset expected, ActivityDto activity)
    {
        Assert.Equal(expected.UtcDateTime, activity.OccurredUtc);
        Assert.Equal(DateTimeKind.Utc, activity.OccurredUtc.Kind);
    }

    [Fact]
    public async Task HistoryRequiresAuthentication()
    {
        await RunAsync(async (_, client, _) =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/task-activity")).StatusCode);
        });
    }

    [Fact]
    public async Task ItemLifecycleIsRecordedWithActorTimeAndValues()
    {
        await RunAsync(async (_, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "history@test.local"));
            var me = (await TestApp.ReadAsync<Dictionary<string, int>>(await client.GetAsync("/api/me")))["userId"];

            var created = await CreateItemAsync(client, "Buy milk");
            Assert.All(await HistoryAsync(client), a => Assert.Equal("created", a.Action));

            clock.Advance(TimeSpan.FromMinutes(5));
            var editedAt = clock.Now;
            await UpdateItemAsync(client, created.Id, new { title = "Buy oat milk", category = "Groceries", date = "2026-01-21" });

            clock.Advance(TimeSpan.FromMinutes(5));
            var completedAt = clock.Now;
            await UpdateItemAsync(client, created.Id, new { done = true });

            clock.Advance(TimeSpan.FromMinutes(5));
            var reopenedAt = clock.Now;
            await UpdateItemAsync(client, created.Id, new { done = false });

            clock.Advance(TimeSpan.FromMinutes(5));
            var deletedAt = clock.Now;
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/items/{created.Id}")).StatusCode);

            // Newest first; every row names the task (with the title it had at the time), the actor and the moment.
            var history = await HistoryAsync(client);
            Assert.Equal(new[] { "deleted", "reopened", "completed", "edited", "created" }, history.Select(a => a.Action).ToArray());
            Assert.All(history, a =>
            {
                Assert.Equal("task", a.TaskType);
                Assert.Equal(created.Id, a.TaskId);
                Assert.Equal(me, a.ActorUserId);
                Assert.Equal("history@test.local", a.ActorEmail);
            });
            var (deleted, reopened, completed, edited, creation) = (history[0], history[1], history[2], history[3], history[4]);

            AssertAt(Start, creation);
            Assert.Equal("Buy milk", creation.Title);
            Assert.Empty(creation.Changes);

            AssertAt(editedAt, edited);
            Assert.Equal("Buy oat milk", edited.Title);
            Assert.Equal(new[]
            {
                new ChangeDto("title", "Buy milk", "Buy oat milk"),
                new ChangeDto("category", "Home", "Groceries"),
                new ChangeDto("date", "2026-01-20", "2026-01-21"),
            }, edited.Changes);

            AssertAt(completedAt, completed);
            Assert.Empty(completed.Changes);
            AssertAt(reopenedAt, reopened);
            Assert.Empty(reopened.Changes);

            AssertAt(deletedAt, deleted);
            Assert.Equal("Buy oat milk", deleted.Title); // the history outlives the item
            Assert.Empty(deleted.Changes);

            // The item is gone, but its history can still be looked up by id.
            Assert.Equal(5, (await HistoryAsync(client, $"?taskType=task&taskId={created.Id}")).Count);
            Assert.Empty(await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items")));
        });
    }

    [Fact]
    public async Task UpdatesThatChangeNothingAndRejectedUpdatesLeaveNoHistory()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "noop@test.local"));
            var item = await CreateItemAsync(client, "Call mom", category: "Family");

            // Re-sending the current values (including an untouched done flag) is not a change.
            await UpdateItemAsync(client, item.Id, new { title = "Call mom", category = "Family", date = "2026-01-20", done = false });
            // Whitespace is trimmed before comparing, exactly as it is before storing.
            await UpdateItemAsync(client, item.Id, new { title = "  Call mom  " });
            // A rejected update writes nothing at all.
            using var rejected = await client.PutAsJsonAsync($"/api/items/{item.Id}", new { title = new string('x', 201) });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var missing = await client.PutAsJsonAsync("/api/items/999999", new { title = "Ghost" });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            var only = Assert.Single(await HistoryAsync(client));
            Assert.Equal("created", only.Action);
        });
    }

    [Fact]
    public async Task AnUpdateThatEditsAndCompletesAtOnceRecordsBothActions()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "both@test.local"));
            var item = await CreateItemAsync(client, "Draft report", date: null);
            await UpdateItemAsync(client, item.Id, new { title = "Draft and send report", date = "2026-01-16", done = true });

            // Both rows are written in one save and share the timestamp; their relative order is not asserted.
            var history = await HistoryAsync(client);
            Assert.Equal(3, history.Count);
            Assert.Equal("created", history[2].Action);
            Assert.Equal(new[] { "completed", "edited" }, history.Take(2).Select(a => a.Action).OrderBy(a => a).ToArray());
            var edited = history.Single(a => a.Action == "edited");
            var completed = history.Single(a => a.Action == "completed");
            Assert.Equal(edited.OccurredUtc, completed.OccurredUtc);
            Assert.Equal("Draft and send report", completed.Title);
            Assert.Equal(new[]
            {
                new ChangeDto("title", "Draft report", "Draft and send report"),
                new ChangeDto("date", null, "2026-01-16"),
            }, edited.Changes);
            Assert.Empty(completed.Changes);

            // Clearing the date again shows up as new value "not set".
            await UpdateItemAsync(client, item.Id, new { date = "" });
            Assert.Equal(new ChangeDto("date", "2026-01-16", null), Assert.Single((await HistoryAsync(client))[0].Changes));
        });
    }

    [Fact]
    public async Task IdempotentReplayOfACreateRecordsASingleCreation()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "replay@test.local"));
            var key = Guid.NewGuid().ToString();
            using var first = new HttpRequestMessage(HttpMethod.Post, "/api/items") { Content = JsonContent.Create(new { kind = "task", title = "Once", category = "", amount = 0, date = (string?)null }) };
            first.Headers.Add("Idempotency-Key", key);
            using var second = new HttpRequestMessage(HttpMethod.Post, "/api/items") { Content = JsonContent.Create(new { kind = "task", title = "Once", category = "", amount = 0, date = (string?)null }) };
            second.Headers.Add("Idempotency-Key", key);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(first)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(second)).StatusCode);
            Assert.Single(await HistoryAsync(client));
        });
    }

    [Fact]
    public async Task HistoryIsScopedToTheOwner()
    {
        await RunAsync(async (_, client, _) =>
        {
            var tokenA = await TestApp.RegisterAndLoginAsync(client, "owner-a@test.local");
            var tokenB = await TestApp.RegisterAndLoginAsync(client, "owner-b@test.local");
            TestApp.Authenticate(client, tokenA);
            var itemA = await CreateItemAsync(client, "A's task");
            await UpdateItemAsync(client, itemA.Id, new { done = true });
            var scheduleA = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule(title: "A's schedule")), HttpStatusCode.Created);
            var occurrenceA = Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences")));
            await client.PutAsJsonAsync($"/api/task-occurrences/{occurrenceA.Id}", new { completed = true });
            TestApp.Authenticate(client, tokenB);
            var itemB = await CreateItemAsync(client, "B's grocery", kind: "grocery");

            var seenByB = await HistoryAsync(client);
            var onlyB = Assert.Single(seenByB);
            Assert.Equal(itemB.Id, onlyB.TaskId);
            Assert.Equal("grocery", onlyB.TaskType);
            Assert.Equal("owner-b@test.local", onlyB.ActorEmail);
            // Asking for A's tasks by id yields nothing for B, and B cannot change them either (no history for A).
            Assert.Empty(await HistoryAsync(client, $"?taskType=task&taskId={itemA.Id}"));
            Assert.Empty(await HistoryAsync(client, $"?taskType=recurring&taskId={scheduleA.Id}"));
            Assert.Empty(await HistoryAsync(client, $"?taskType=occurrence&taskId={occurrenceA.Id}"));
            Assert.Empty(await HistoryAsync(client, "?taskType=recurring"));
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/items/{itemA.Id}", new { done = false })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/items/{itemA.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/task-occurrences/{occurrenceA.Id}", new { completed = false })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/recurring-tasks/{scheduleA.Id}/disable", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recurring-tasks/{scheduleA.Id}")).StatusCode);
            Assert.Single(await HistoryAsync(client));

            TestApp.Authenticate(client, tokenA);
            Assert.Equal(new[] { "completed", "created", "completed", "created" }, (await HistoryAsync(client)).Select(a => a.Action).ToArray());
            Assert.Equal(new[] { "occurrence", "recurring", "task", "task" }, (await HistoryAsync(client)).Select(a => a.TaskType).ToArray());
            Assert.All(await HistoryAsync(client), a => Assert.Equal("owner-a@test.local", a.ActorEmail));

            // A deletes both; the history stays A's and A's only. B gets nothing by id, by type or by action, and B's
            // own history is untouched.
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/items/{itemA.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recurring-tasks/{scheduleA.Id}")).StatusCode);
            var ownHistory = await HistoryAsync(client);
            Assert.Equal(new[] { "deleted", "deleted", "completed", "created", "completed", "created" }, ownHistory.Select(a => a.Action).ToArray());
            Assert.All(ownHistory, a => Assert.Equal("owner-a@test.local", a.ActorEmail));
            Assert.Equal(3, (await HistoryAsync(client, $"?taskType=task&taskId={itemA.Id}")).Count);

            TestApp.Authenticate(client, tokenB);
            foreach (var query in new[] { $"?taskType=task&taskId={itemA.Id}", $"?taskType=recurring&taskId={scheduleA.Id}", $"?taskType=occurrence&taskId={occurrenceA.Id}",
                                          "?taskType=task", "?taskType=recurring", "?taskType=occurrence", "?action=deleted", "?action=completed", "?limit=500" })
            {
                var seen = await HistoryAsync(client, query);
                Assert.All(seen, a => { Assert.Equal(itemB.Id, a.TaskId); Assert.Equal("grocery", a.TaskType); Assert.Equal("owner-b@test.local", a.ActorEmail); });
            }
            Assert.Single(await HistoryAsync(client));
            // Nor can B act on the deleted things (404s, no history for anyone).
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/items/{itemA.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recurring-tasks/{scheduleA.Id}")).StatusCode);
            Assert.Single(await HistoryAsync(client));
            TestApp.Authenticate(client, tokenA);
            Assert.Equal(6, (await HistoryAsync(client)).Count);
        });
    }

    [Fact]
    public async Task HistoryCanBeFilteredAndPaged()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "filter@test.local"));
            var task = await CreateItemAsync(client, "Task");
            var grocery = await CreateItemAsync(client, "Milk", kind: "grocery");
            var ev = await CreateItemAsync(client, "Dentist", kind: "event");
            await UpdateItemAsync(client, task.Id, new { done = true });
            await UpdateItemAsync(client, grocery.Id, new { done = true });
            await UpdateItemAsync(client, task.Id, new { done = false });
            await UpdateItemAsync(client, ev.Id, new { title = "Dentist appointment" });
            // 7 rows: 3 created, 2 completed, 1 reopened, 1 edited.

            Assert.Equal(7, (await HistoryAsync(client)).Count);
            Assert.Equal(new[] { "reopened", "completed", "created" }, (await HistoryAsync(client, $"?taskType=task&taskId={task.Id}")).Select(a => a.Action).ToArray());
            Assert.Equal(new[] { "completed", "created" }, (await HistoryAsync(client, "?taskType=grocery")).Select(a => a.Action).ToArray());
            Assert.Equal(2, (await HistoryAsync(client, "?action=completed")).Count);
            Assert.Equal(ev.Id, Assert.Single(await HistoryAsync(client, "?action=edited")).TaskId);
            Assert.Equal(task.Id, Assert.Single(await HistoryAsync(client, "?action=created&taskType=task&limit=5")).TaskId);
            // Ids of another kind are a different id space: the task's id does not find a grocery.
            Assert.Empty(await HistoryAsync(client, $"?taskType=grocery&taskId={task.Id}"));

            // Cursor paging: three pages of three, newest first, no gaps, no overlaps.
            var page1 = await HistoryAsync(client, "?limit=3");
            var page2 = await HistoryAsync(client, $"?limit=3&beforeId={page1[^1].Id}");
            var page3 = await HistoryAsync(client, $"?limit=3&beforeId={page2[^1].Id}");
            Assert.Equal(3, page1.Count);
            Assert.Equal(3, page2.Count);
            Assert.Single(page3);
            var ids = page1.Concat(page2).Concat(page3).Select(a => a.Id).ToList();
            Assert.Equal(ids.OrderByDescending(i => i), ids);
            Assert.Equal(7, ids.Distinct().Count());
            Assert.Empty(await HistoryAsync(client, $"?limit=3&beforeId={page3[^1].Id}"));

            // Invalid filters are 400 Problem Details, like the rest of the API.
            foreach (var bad in new[] { "?taskType=item", "?action=archived", "?taskId=1", "?limit=0", "?limit=501", "?limit=abc" })
            {
                using var response = await client.GetAsync("/api/task-activity" + bad);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for {bad} but got {(int)response.StatusCode}");
            }
        });
    }

    [Fact]
    public async Task RecurringScheduleLifecycleIsRecordedAndSchedulingStillWorks()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "recurring@test.local"));
            var schedule = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule()), HttpStatusCode.Created);
            Assert.Equal(1, schedule.PendingCount); // creation still materialises the first occurrence

            var created = Assert.Single(await HistoryAsync(client));
            Assert.Equal("recurring", created.TaskType);
            Assert.Equal(schedule.Id, created.TaskId);
            Assert.Equal("created", created.Action);
            Assert.Equal("Water plants", created.Title);
            Assert.Equal("Every day at 09:00", created.Detail);
            AssertAt(Start, created);

            clock.Advance(TimeSpan.FromHours(1));
            await client.PostAsync($"/api/recurring-tasks/{schedule.Id}/disable", null);
            var disabled = (await HistoryAsync(client))[0];
            Assert.Equal("edited", disabled.Action);
            Assert.Equal(new ChangeDto("active", "true", "false"), Assert.Single(disabled.Changes));
            AssertAt(clock.Now, disabled);
            // Disabling twice is a no-op and records nothing.
            await client.PostAsync($"/api/recurring-tasks/{schedule.Id}/disable", null);
            Assert.Equal(2, (await HistoryAsync(client)).Count);

            clock.Advance(TimeSpan.FromHours(1));
            var enabled = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsync($"/api/recurring-tasks/{schedule.Id}/enable", null));
            Assert.True(enabled.IsActive);
            Assert.Equal(1, enabled.PendingCount); // the next occurrence is scheduled again
            Assert.Equal(new ChangeDto("active", "false", "true"), Assert.Single((await HistoryAsync(client))[0].Changes));

            // Scheduler passes (the background worker's unit of work) are not user actions and write no history.
            clock.Set("2026-01-20T10:00:00Z");
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.True(pass.OccurrencesCreated > 0);
            Assert.True(pass.RemindersCreated > 0);
            Assert.Equal(3, (await HistoryAsync(client)).Count);

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recurring-tasks/{schedule.Id}")).StatusCode);
            var deleted = (await HistoryAsync(client))[0];
            Assert.Equal("deleted", deleted.Action);
            Assert.Equal("recurring", deleted.TaskType);
            Assert.Equal("Water plants", deleted.Title);
            Assert.Equal("Every day at 09:00", deleted.Detail);
            Assert.Equal(new[] { "deleted", "edited", "edited", "created" }, (await HistoryAsync(client, "?taskType=recurring")).Select(a => a.Action).ToArray());
            Assert.Empty(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences?includeCompleted=true")));
        });
    }

    [Fact]
    public async Task CompletingAndReopeningAnOccurrenceIsRecordedAndItsReminderStillSettles()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "occurrence@test.local"));
            var schedule = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule()), HttpStatusCode.Created);
            // The first occurrence is Jan 16 09:00 with a 30-minute reminder: at 08:45 the reminder is raised.
            clock.Set("2026-01-16T08:45:00Z");
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            var occurrence = Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences")));
            Assert.Null(Assert.Single(await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders"))).ReadUtc);

            clock.Set("2026-01-16T08:50:00Z");
            var completed = await TestApp.ReadAsync<OccurrenceDto>(await client.PutAsJsonAsync($"/api/task-occurrences/{occurrence.Id}", new { completed = true }));
            Assert.True(completed.Completed);
            // Existing behaviour is intact: completing settles the reminder.
            Assert.NotNull(Assert.Single(await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders"))).ReadUtc);

            var row = (await HistoryAsync(client))[0];
            Assert.Equal("occurrence", row.TaskType);
            Assert.Equal(occurrence.Id, row.TaskId);
            Assert.Equal("completed", row.Action);
            Assert.Equal("Water plants", row.Title);
            Assert.Equal("Due 2026-01-16", row.Detail);
            Assert.Equal("occurrence@test.local", row.ActorEmail);
            AssertAt(clock.Now, row);

            // Completing an already completed occurrence changes nothing and records nothing.
            await client.PutAsJsonAsync($"/api/task-occurrences/{occurrence.Id}", new { completed = true });
            Assert.Equal(2, (await HistoryAsync(client)).Count);

            clock.Advance(TimeSpan.FromMinutes(1));
            await client.PutAsJsonAsync($"/api/task-occurrences/{occurrence.Id}", new { completed = false });
            var reopened = (await HistoryAsync(client))[0];
            Assert.Equal("reopened", reopened.Action);
            Assert.Equal("Due 2026-01-16", reopened.Detail);
            AssertAt(clock.Now, reopened);
            Assert.Equal(new[] { "reopened", "completed" }, (await HistoryAsync(client, $"?taskType=occurrence&taskId={occurrence.Id}")).Select(a => a.Action).ToArray());
            Assert.Equal(schedule.Id, occurrence.RecurringTaskId);
        });
    }

    [Fact]
    public async Task RestoredItemsAreRecordedAsCreatedAndSkippedDuplicatesAreNot()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "restore@test.local"));
            var backup = new[]
            {
                new { kind = "task", title = "Renew passport", category = "Admin", amount = 0m, date = (string?)"2026-03-01" },
                new { kind = "grocery", title = "Eggs", category = "", amount = 0m, date = (string?)null },
            };
            using var first = await client.PostAsJsonAsync("/api/restore", backup);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var second = await client.PostAsJsonAsync("/api/restore", backup); // everything already present
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var items = await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"));
            Assert.Equal(2, items.Count);
            var history = await HistoryAsync(client);
            Assert.Equal(2, history.Count);
            Assert.All(history, a => { Assert.Equal("created", a.Action); Assert.Equal("Restored from backup", a.Detail); });
            Assert.Equal(items.Select(i => (i.Kind, i.Id, i.Title)).OrderBy(x => x.Id), history.Select(a => (a.TaskType, a.TaskId, a.Title)).OrderBy(x => x.TaskId));

            // The restored item now has a normal history.
            var passport = items.Single(i => i.Title == "Renew passport");
            await UpdateItemAsync(client, passport.Id, new { done = true });
            Assert.Equal(new[] { "completed", "created" }, (await HistoryAsync(client, $"?taskType=task&taskId={passport.Id}")).Select(a => a.Action).ToArray());
        });
    }

    [Fact]
    public async Task UpdatingEveryFieldAtOnceRecordsEachChangeAndRepeatingItRecordsNothing()
    {
        await RunAsync(async (_, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "fields@test.local"));
            var item = await CreateItemAsync(client, "Pay invoice", category: "", date: null);
            clock.Advance(TimeSpan.FromMinutes(1));
            var patch = new { title = "Pay invoice #42", category = "Finance", amount = 12.5m, date = "2026-02-01", done = true };
            await UpdateItemAsync(client, item.Id, patch);

            var edited = Assert.Single(await HistoryAsync(client, "?action=edited"));
            Assert.Equal(new[]
            {
                new ChangeDto("title", "Pay invoice", "Pay invoice #42"),
                new ChangeDto("category", "", "Finance"),
                new ChangeDto("amount", "0", "12.5"),
                new ChangeDto("date", null, "2026-02-01"),
            }, edited.Changes);
            Assert.Single(await HistoryAsync(client, "?action=completed"));

            // The same request again (a client retry after a lost response) finds every value already in place: the
            // item is returned unchanged and no history is written. Numerically equal amounts are not a change either.
            var again = await UpdateItemAsync(client, item.Id, patch);
            Assert.Equal("Pay invoice #42", again.Title);
            Assert.True(again.Done);
            await UpdateItemAsync(client, item.Id, new { amount = 12.50m, category = "  Finance  " });
            Assert.Equal(3, (await HistoryAsync(client)).Count);

            // The stored 12.5 is the old value of the next edit and must read exactly as it did as the new value above.
            await UpdateItemAsync(client, item.Id, new { amount = 20 });
            Assert.Equal(new ChangeDto("amount", "12.5", "20"), Assert.Single((await HistoryAsync(client))[0].Changes));
        });
    }

    [Fact]
    public async Task AmountsReadTheSameWhetherTheyComeFromTheRequestOrTheDatabase()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "amounts@test.local"));
            // Created with 3 (stored by SQLite as "3.0"), then edited through a sequence of scales and magnitudes.
            var item = await CreateItemAsync(client, "Groceries budget", kind: "grocery", amount: 3m);
            await UpdateItemAsync(client, item.Id, new { amount = 4.25m });
            await UpdateItemAsync(client, item.Id, new { amount = 4.250m });  // same number, different scale: not a change
            await UpdateItemAsync(client, item.Id, new { amount = 5 });
            await UpdateItemAsync(client, item.Id, new { amount = 0 });
            await UpdateItemAsync(client, item.Id, new { amount = 0.0m });    // still not a change
            await UpdateItemAsync(client, item.Id, new { amount = 99999999.99m });
            await UpdateItemAsync(client, item.Id, new { amount = 0.01m });

            var edits = (await HistoryAsync(client, "?action=edited")).OrderBy(a => a.Id).Select(a => Assert.Single(a.Changes)).ToList();
            Assert.All(edits, c => Assert.Equal("amount", c.Field));
            Assert.Equal(new[]
            {
                new ChangeDto("amount", "3", "4.25"),
                new ChangeDto("amount", "4.25", "5"),
                new ChangeDto("amount", "5", "0"),
                new ChangeDto("amount", "0", "99999999.99"),
                new ChangeDto("amount", "99999999.99", "0.01"),
            }, edits);
            // Every old value is exactly the previous new value: the chain never shows the same number two ways.
            for (var i = 1; i < edits.Count; i++) Assert.Equal(edits[i - 1].NewValue, edits[i].OldValue);
            Assert.Equal(0.01m, (await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"))).Single().Amount);
        });
    }

    [Fact]
    public async Task OverlappingDuplicateRequestsRecordEachChangeExactlyOnce()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "race@test.local"));
            var item = await CreateItemAsync(client, "Race");

            // Eight copies of the same completion at once (a double-click, or a retry overtaking the original): the
            // item ends up completed once and the history says so once. Without a serializable transaction every copy
            // would read "not done", write "done" and record its own "completed" row.
            var completions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PutAsJsonAsync($"/api/items/{item.Id}", new { done = true })));
            Assert.All(completions, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Single(await HistoryAsync(client, "?action=completed"));

            var edits = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PutAsJsonAsync($"/api/items/{item.Id}", new { title = "Race (renamed)" })));
            Assert.All(edits, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var edited = Assert.Single(await HistoryAsync(client, "?action=edited"));
            Assert.Equal(new ChangeDto("title", "Race", "Race (renamed)"), Assert.Single(edited.Changes));

            // Overlapping deletes: one succeeds, the others find nothing (404, not a server error) and record nothing.
            var deletes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.DeleteAsync($"/api/items/{item.Id}")));
            Assert.Equal(1, deletes.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(7, deletes.Count(r => r.StatusCode == HttpStatusCode.NotFound));
            Assert.Single(await HistoryAsync(client, "?action=deleted"));
            Assert.Equal(new[] { "deleted", "edited", "completed", "created" }, (await HistoryAsync(client)).Select(a => a.Action).ToArray());

            // The same holds for the idempotent create: one key, many concurrent sends, one item, one "created".
            var key = Guid.NewGuid().ToString();
            var creates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/items") { Content = JsonContent.Create(new { kind = "task", title = "Once", category = "", amount = 0, date = (string?)null }) };
                request.Headers.Add("Idempotency-Key", key);
                return client.SendAsync(request);
            }));
            Assert.Equal(1, creates.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(7, creates.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Single(await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items")));
            Assert.Single(await HistoryAsync(client, "?action=created&taskType=task"), a => a.Title == "Once");
        });
    }

    [Fact]
    public async Task ConcurrentOppositeTogglesKeepTheHistoryConsistentWithTheItem()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "toggle-race@test.local"));
            var item = await CreateItemAsync(client, "Flip");
            // Completions and reopenings racing each other. Which order they land in is up to the database lock, but
            // because each request is serialized and compares against the state it actually read, the history must be
            // a strict alternation that starts with "completed" (the item began open) and ends in the item's final state.
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.PutAsJsonAsync($"/api/items/{item.Id}", new { done = i % 2 == 0 })));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            var final = (await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"))).Single();
            var transitions = (await HistoryAsync(client, $"?taskType=task&taskId={item.Id}")).Where(a => a.Action != "created").OrderBy(a => a.Id).Select(a => a.Action).ToList();
            for (var i = 0; i < transitions.Count; i++) Assert.Equal(i % 2 == 0 ? "completed" : "reopened", transitions[i]);
            Assert.Equal(final.Done, transitions.Count > 0 && transitions[^1] == "completed");
            Assert.True(transitions.Count <= 8);
        });
    }

    [Fact]
    public async Task OverlappingDuplicateRecurringRequestsRecordEachChangeExactlyOnce()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "recurring-race@test.local"));
            var schedule = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule()), HttpStatusCode.Created);
            clock.Set("2026-01-16T08:45:00Z");
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            var occurrence = Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences")));

            // Eight copies of the same completion: one "completed" row, and the reminder is settled exactly as before.
            var completions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PutAsJsonAsync($"/api/task-occurrences/{occurrence.Id}", new { completed = true })));
            Assert.All(completions, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Single(await HistoryAsync(client, "?taskType=occurrence&action=completed"));
            Assert.NotNull(Assert.Single(await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders"))).ReadUtc);

            // Past the due time, so that enabling has a next occurrence (Jan 17) to materialise.
            clock.Set("2026-01-16T09:30:00Z");
            var disables = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsync($"/api/recurring-tasks/{schedule.Id}/disable", null)));
            Assert.All(disables, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var enables = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsync($"/api/recurring-tasks/{schedule.Id}/enable", null)));
            Assert.All(enables, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var edits = (await HistoryAsync(client, "?taskType=recurring&action=edited")).OrderBy(a => a.Id).ToList();
            Assert.Equal(2, edits.Count);
            Assert.Equal(new ChangeDto("active", "true", "false"), Assert.Single(edits[0].Changes));
            Assert.Equal(new ChangeDto("active", "false", "true"), Assert.Single(edits[1].Changes));
            // Enabling materialised the next occurrence once, not eight times.
            Assert.Equal("2026-01-17", Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences"))).DueDate);

            var deletes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.DeleteAsync($"/api/recurring-tasks/{schedule.Id}")));
            Assert.Equal(1, deletes.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(7, deletes.Count(r => r.StatusCode == HttpStatusCode.NotFound));
            Assert.Single(await HistoryAsync(client, "?taskType=recurring&action=deleted"));
            Assert.Equal(new[] { "deleted", "edited", "edited", "completed", "created" }, (await HistoryAsync(client)).Select(a => a.Action).ToArray());
            Assert.Empty(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences?includeCompleted=true")));
        });
    }

    [Fact]
    public async Task RejectedRequestsRecordNothing()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "rejected@test.local"));
            var item = await CreateItemAsync(client, "Keep me");

            // Idempotency key reused with a different payload: 409, no second item, no history.
            var key = Guid.NewGuid().ToString();
            await CreateItemAsync(client, "First", key: key);
            using var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/items") { Content = JsonContent.Create(new { kind = "task", title = "Different", category = (string?)null, amount = (decimal?)null, date = (string?)null }) };
            conflict.Headers.Add("Idempotency-Key", key);
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(conflict)).StatusCode);
            // Missing key, invalid item, invalid update, invalid occurrence body, invalid schedule: all 4xx, nothing recorded.
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/items", new { kind = "task", title = "No key" })).StatusCode);
            using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/items") { Content = JsonContent.Create(new { kind = "chore", title = "Bad kind" }) };
            invalid.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(invalid)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/items/{item.Id}", new { title = "Valid title", amount = -1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/items/{item.Id}", new { date = "not a date", done = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/recurring-tasks", Schedule(title: ""))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/task-occurrences/1", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/task-occurrences/999999", new { completed = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/recurring-tasks/999999/enable", null)).StatusCode);

            var items = await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"));
            Assert.Equal(2, items.Count);
            Assert.Equal("Keep me", items.Single(i => i.Id == item.Id).Title); // the rejected update changed nothing
            Assert.False(items.Single(i => i.Id == item.Id).Done);
            Assert.Equal(new[] { "created", "created" }, (await HistoryAsync(client)).Select(a => a.Action).ToArray());

            // A schedule whose only occurrence is completed cannot be re-enabled: the 400 leaves the history untouched.
            var schedule = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule(title: "Once only", startDate: "2026-01-16", endDate: "2026-01-16")), HttpStatusCode.Created);
            var occurrence = Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences")));
            await client.PutAsJsonAsync($"/api/task-occurrences/{occurrence.Id}", new { completed = true });
            await client.PostAsync($"/api/recurring-tasks/{schedule.Id}/disable", null);
            var before = await HistoryAsync(client);
            Assert.Equal(new[] { "edited", "completed", "created" }, before.Take(3).Select(a => a.Action).ToArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/recurring-tasks/{schedule.Id}/enable", null)).StatusCode);
            Assert.Equal(before.Select(a => a.Id), (await HistoryAsync(client)).Select(a => a.Id));
            Assert.False(Assert.Single(await TestApp.ReadAsync<List<ScheduleDto>>(await client.GetAsync("/api/recurring-tasks"))).IsActive);
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).OccurrencesCreated);
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(before.Count, (await HistoryAsync(client)).Count);
        });
    }

    [Fact]
    public async Task OccurrenceHistorySurvivesDeletingItsScheduleAndDeletedSchedulesStayReadable()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "survive@test.local"));
            var schedule = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule(title: "Take out bins")), HttpStatusCode.Created);
            clock.Set("2026-01-18T10:00:00Z");
            await TestApp.RunSchedulerAsync(app); // Jan 16, 17, 18 overdue + Jan 19 upcoming
            var occurrences = await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences"));
            Assert.Equal(4, occurrences.Count);
            var first = occurrences.Single(o => o.DueDate == "2026-01-16");
            var second = occurrences.Single(o => o.DueDate == "2026-01-17");
            await client.PutAsJsonAsync($"/api/task-occurrences/{first.Id}", new { completed = true });
            await client.PutAsJsonAsync($"/api/task-occurrences/{second.Id}", new { completed = true });
            await client.PutAsJsonAsync($"/api/task-occurrences/{second.Id}", new { completed = false });

            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recurring-tasks/{schedule.Id}")).StatusCode);
            Assert.Empty(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences?includeCompleted=true")));
            Assert.Empty(await TestApp.ReadAsync<List<ScheduleDto>>(await client.GetAsync("/api/recurring-tasks")));

            // Everything that was done is still there, in order, with the titles and due dates it had.
            var history = await HistoryAsync(client);
            Assert.Equal(new[] { "deleted", "reopened", "completed", "completed", "created" }, history.Select(a => a.Action).ToArray());
            Assert.All(history, a => Assert.Equal("Take out bins", a.Title));
            Assert.Equal(new[] { "Due 2026-01-17", "Due 2026-01-17", "Due 2026-01-16" }, history.Where(a => a.TaskType == "occurrence").Select(a => a.Detail).ToArray());
            Assert.Equal(new[] { "reopened", "completed" }, (await HistoryAsync(client, $"?taskType=occurrence&taskId={second.Id}")).Select(a => a.Action).ToArray());
            Assert.Equal(new[] { "deleted", "created" }, (await HistoryAsync(client, $"?taskType=recurring&taskId={schedule.Id}")).Select(a => a.Action).ToArray());
            AssertAt(clock.Now, history[0]);

            // A later schedule reuses nothing: its rows are its own even though ids of the deleted one are gone.
            var next = await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule(title: "Recycling", startDate: "2026-01-19")), HttpStatusCode.Created);
            Assert.NotEqual(schedule.Id, next.Id);
            Assert.Equal("Recycling", Assert.Single(await HistoryAsync(client, $"?taskType=recurring&taskId={next.Id}")).Title);
        });
    }

    [Fact]
    public async Task ConcurrentIdenticalRestoresRecordEachImportedItemOnce()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "restore-race@test.local"));
            var backup = Enumerable.Range(1, 20).Select(i => new { kind = "task", title = $"Task {i}", category = "Import", amount = 0m, date = (string?)null }).ToArray();
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/restore", backup)));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal(20, (await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"))).Count);
            var history = await HistoryAsync(client);
            Assert.Equal(20, history.Count);
            Assert.Equal(20, history.Select(a => a.TaskId).Distinct().Count());
            Assert.All(history, a => { Assert.Equal("created", a.Action); Assert.Equal("Restored from backup", a.Detail); });
        });
    }

    [Fact]
    public async Task PagingIsStableWhenNewActivityArrivesBetweenPages()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "paging@test.local"));
            var me = (await TestApp.ReadAsync<Dictionary<string, int>>(await client.GetAsync("/api/me")))["userId"];
            // A long-lived account: 1,200 rows written directly (600 created, 600 completed), then live activity via the API.
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LifeDb>();
                db.TaskActivities.AddRange(Enumerable.Range(1, 1200).Select(i => new TaskActivity
                {
                    UserId = me, ActorUserId = me, TaskType = "task", TaskId = (i + 1) / 2, Title = $"Seed {(i + 1) / 2}",
                    Action = i % 2 == 1 ? "created" : "completed", OccurredUtc = Start.UtcDateTime.AddMinutes(i)
                }));
                await db.SaveChangesAsync();
            }
            clock.Set("2026-01-16T10:00:00Z");
            var live = await CreateItemAsync(client, "Live"); // row 1,201

            var page1 = await HistoryAsync(client, "?limit=500");
            Assert.Equal(500, page1.Count);
            Assert.Equal("Live", page1[0].Title);

            // New activity lands while the user is paging: it must neither shift, duplicate nor hide the older pages.
            await UpdateItemAsync(client, live.Id, new { done = true });
            var newer = await CreateItemAsync(client, "Newer");
            var page2 = await HistoryAsync(client, $"?limit=500&beforeId={page1[^1].Id}");
            await UpdateItemAsync(client, newer.Id, new { title = "Newer still" });
            var page3 = await HistoryAsync(client, $"?limit=500&beforeId={page2[^1].Id}");
            var page4 = await HistoryAsync(client, $"?limit=500&beforeId={page3[^1].Id}");
            Assert.Equal(500, page2.Count);
            Assert.Equal(201, page3.Count);
            Assert.Empty(page4);

            var ids = page1.Concat(page2).Concat(page3).Select(a => a.Id).ToList();
            Assert.Equal(1201, ids.Count);
            Assert.Equal(1201, ids.Distinct().Count());
            Assert.Equal(ids.OrderByDescending(i => i), ids);
            Assert.DoesNotContain(ids, id => id > page1[0].Id); // nothing added mid-way leaked into later pages
            Assert.Equal("Seed 1", page3[^1].Title);
            Assert.Equal("created", page3[^1].Action);

            // The rows added while paging are at the top of a fresh first page, newest first.
            var top = await HistoryAsync(client, "?limit=3");
            Assert.Equal(new[] { "edited", "created", "completed" }, top.Select(a => a.Action).ToArray());
            Assert.Equal(new[] { "Newer still", "Newer", "Live" }, top.Select(a => a.Title).ToArray());
            Assert.True(top[^1].Id > page1[0].Id);

            // Filters page with the same cursor: 600 seeded completions plus the live one.
            var completed1 = await HistoryAsync(client, "?action=completed&limit=400");
            var completed2 = await HistoryAsync(client, $"?action=completed&limit=400&beforeId={completed1[^1].Id}");
            Assert.Equal(400, completed1.Count);
            Assert.Equal(201, completed2.Count);
            Assert.Equal(601, completed1.Concat(completed2).Select(a => a.Id).Distinct().Count());
            Assert.All(completed1.Concat(completed2), a => Assert.Equal("completed", a.Action));
            Assert.Empty(await HistoryAsync(client, $"?action=completed&limit=400&beforeId={completed2[^1].Id}"));
            // Per-task paging: every seeded task has exactly its two rows.
            Assert.Equal(new[] { "completed", "created" }, (await HistoryAsync(client, "?taskType=task&taskId=300&limit=500")).Select(a => a.Action).ToArray());

            // What the UI does: ask for one row more than the page (51), show 50, and continue from the id of the 50th.
            // The hidden 51st row must be the first row of the next page, so nothing is ever skipped, and the extra row
            // is only present while there really is more.
            var shown = new List<ActivityDto>();
            for (var more = true; more;)
            {
                var query = "?limit=51" + (shown.Count > 0 ? $"&beforeId={shown[^1].Id}" : "");
                var page = await HistoryAsync(client, query);
                more = page.Count > 50;
                var visible = page.Take(50).ToList();
                if (more) Assert.Equal(page[50].Id, (await HistoryAsync(client, $"?limit=1&beforeId={visible[^1].Id}")).Single().Id);
                shown.AddRange(visible);
            }
            var all = page1.Concat(page2).Concat(page3).Select(a => a.Id).ToList(); // taken before the live rows
            Assert.Equal(1204, shown.Count);                                          // 1,201 + the 3 rows added mid-way
            Assert.Equal(shown.Count, shown.Select(a => a.Id).Distinct().Count());
            Assert.Equal(all, shown.Select(a => a.Id).Where(id => all.Contains(id)));
            Assert.Equal(shown.Select(a => a.Id).OrderByDescending(i => i), shown.Select(a => a.Id));

            // Default page size and the cap.
            Assert.Equal(100, (await HistoryAsync(client)).Count);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/task-activity?limit=501")).StatusCode);
            // A cursor below every id, or a cursor that is not a number, is handled (empty / 400), never an error page.
            Assert.Empty(await HistoryAsync(client, "?beforeId=1"));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/task-activity?beforeId=last")).StatusCode);
        });
    }

    [Fact]
    public async Task DatabaseFromAnOlderVersionWithExistingTasksAndSchedulesGainsHistoryOnStartup()
    {
        var clock = new FakeClock(Start);
        var path = TestApp.NewDatabasePath();
        try
        {
            int itemId, scheduleId, occurrenceId;
            using (var app = TestApp.Create(path, clock))
            using (var client = app.CreateClient())
            {
                TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "upgrade@test.local"));
                itemId = (await CreateItemAsync(client, "Pre-existing task")).Id;
                await CreateItemAsync(client, "Pre-existing grocery", kind: "grocery");
                scheduleId = (await TestApp.ReadAsync<ScheduleDto>(await client.PostAsJsonAsync("/api/recurring-tasks", Schedule(title: "Pre-existing schedule")), HttpStatusCode.Created)).Id;
                clock.Set("2026-01-16T08:45:00Z");
                Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated); // Jan 16's reminder is up
                occurrenceId = Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences"))).Id;
            }
            // Simulate a database written by a version without the history table: items, schedule, occurrence and
            // reminder are there, TaskActivities (and its indexes) are not.
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE TaskActivities";
                await drop.ExecuteNonQueryAsync();
            }
            // Two instances starting on that database at the same moment both come up; the table is added once.
            using (var appA = TestApp.Create(path, clock))
            using (var appB = TestApp.Create(path, clock))
            {
                var clients = await Task.WhenAll(Task.Run(() => appA.CreateClient()), Task.Run(() => appB.CreateClient()));
                using var client = clients[0];
                using var clientB = clients[1];
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/api/health")).StatusCode);
                await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    await connection.OpenAsync();
                    var check = connection.CreateCommand();
                    check.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND tbl_name = 'TaskActivities' ORDER BY name";
                    var names = new List<string>();
                    await using (var reader = await check.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
                    Assert.Contains("TaskActivities", names);
                    Assert.Contains("IX_TaskActivities_UserId_Id", names);
                    Assert.Contains("IX_TaskActivities_UserId_TaskType_TaskId", names);
                }
                var token = await LoginAsync(client, "upgrade@test.local");
                TestApp.Authenticate(client, token);
                TestApp.Authenticate(clientB, token);

                // Everything from before is intact, and nothing was recorded retroactively.
                Assert.Equal(2, (await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"))).Count);
                Assert.Equal(scheduleId, Assert.Single(await TestApp.ReadAsync<List<ScheduleDto>>(await client.GetAsync("/api/recurring-tasks"))).Id);
                Assert.Equal(occurrenceId, Assert.Single(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences"))).Id);
                Assert.Null(Assert.Single(await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders"))).ReadUtc);
                Assert.Empty(await HistoryAsync(client));
                Assert.Empty(await HistoryAsync(clientB, $"?taskType=task&taskId={itemId}"));

                // From now on changes are recorded, through either instance, and reminders still settle.
                clock.Set("2026-01-16T08:50:00Z");
                await clientB.PutAsJsonAsync($"/api/task-occurrences/{occurrenceId}", new { completed = true });
                Assert.NotNull(Assert.Single(await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders"))).ReadUtc);
                await UpdateItemAsync(client, itemId, new { title = "Pre-existing task (renamed)" });
                var history = await HistoryAsync(client);
                Assert.Equal(new[] { "edited", "completed" }, history.Select(a => a.Action).ToArray());
                Assert.Equal(new ChangeDto("title", "Pre-existing task", "Pre-existing task (renamed)"), Assert.Single(history[0].Changes));
                Assert.Equal("Pre-existing schedule", history[1].Title);
                Assert.Equal("Due 2026-01-16", history[1].Detail);

                // The scheduler keeps materialising and reminding on the upgraded database without writing history.
                clock.Set("2026-01-18T10:00:00Z");
                var pass = await TestApp.RunSchedulerAsync(appA);
                Assert.Equal(3, pass.OccurrencesCreated); // Jan 17, 18 (missed) and Jan 19 (upcoming)
                Assert.True(pass.RemindersCreated > 0);
                Assert.Equal(2, (await HistoryAsync(client)).Count);

                // Deleting the schedule removes its occurrences and reminders but none of the history.
                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recurring-tasks/{scheduleId}")).StatusCode);
                Assert.Empty(await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences?includeCompleted=true")));
                Assert.Empty(await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders")));
                Assert.Equal(new[] { "deleted", "edited", "completed" }, (await HistoryAsync(clientB)).Select(a => a.Action).ToArray());
            }
            // A further restart finds the table already there and everything still readable.
            using (var app = TestApp.Create(path, clock))
            using (var client = app.CreateClient())
            {
                TestApp.Authenticate(client, await LoginAsync(client, "upgrade@test.local"));
                var history = await HistoryAsync(client);
                Assert.Equal(new[] { "deleted", "edited", "completed" }, history.Select(a => a.Action).ToArray());
                Assert.Equal(new[] { "recurring", "task", "occurrence" }, history.Select(a => a.TaskType).ToArray());
                Assert.Equal("completed", Assert.Single(await HistoryAsync(client, $"?taskType=occurrence&taskId={occurrenceId}")).Action);
                Assert.Equal(new[] { "deleted" }, (await HistoryAsync(client, $"?taskType=recurring&taskId={scheduleId}")).Select(a => a.Action).ToArray());
                Assert.Empty(await TestApp.ReadAsync<List<ScheduleDto>>(await client.GetAsync("/api/recurring-tasks")));
                Assert.Equal("Pre-existing task (renamed)", (await TestApp.ReadAsync<List<ItemDto>>(await client.GetAsync("/api/items"))).Single(i => i.Id == itemId).Title);
                Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).OccurrencesCreated);
            }
        }
        finally { await TestApp.DeleteDatabaseAsync(path); }
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
