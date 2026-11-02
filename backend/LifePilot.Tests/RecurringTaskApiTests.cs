using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// End-to-end tests for recurring tasks, occurrences and in-app reminders. Each test gets its own SQLite database and a
/// fake clock starting at 2026-01-15 10:00 UTC; the scheduler is run explicitly instead of by the background worker.
/// </summary>
public class RecurringTaskApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public sealed record TaskDto(int Id, string Title, string Category, string Frequency, int Interval, string StartDate, string DueTime, string TimeZoneId,
        int? ReminderLeadMinutes, string? EndDate, bool IsActive, string Description, string? NextDueDate, DateTime? NextDueUtc, List<UpcomingDto> Upcoming, int PendingCount, int CompletedCount);
    public sealed record UpcomingDto(int Sequence, string Date, DateTime DueUtc);
    public sealed record OccurrenceDto(int Id, int RecurringTaskId, string Title, int SequenceNumber, string DueDate, DateTime DueUtc, bool Completed, DateTime? CompletedUtc, DateTime? ReminderAtUtc, DateTime? ReminderSentUtc, string Status, bool ScheduleActive);
    public sealed record ReminderDto(int Id, int OccurrenceId, int RecurringTaskId, string Title, string Message, DateTime DueUtc, DateTime CreatedUtc, DateTime? ReadUtc);

    private static object Request(string title = "Water plants", string frequency = "daily", int? interval = 1, string startDate = "2026-01-15", string dueTime = "09:00",
        string? timeZoneId = "UTC", int? reminderLeadMinutes = 30, string? endDate = null, string? category = "Home") =>
        new { title, category, frequency, interval, startDate, dueTime, timeZoneId, reminderLeadMinutes, endDate };

    private static async Task<TaskDto> CreateAsync(HttpClient client, object request) =>
        await TestApp.ReadAsync<TaskDto>(await client.PostAsJsonAsync("/api/recurring-tasks", request), HttpStatusCode.Created);
    private static async Task<List<TaskDto>> TasksAsync(HttpClient client) => await TestApp.ReadAsync<List<TaskDto>>(await client.GetAsync("/api/recurring-tasks"));
    private static async Task<List<OccurrenceDto>> OccurrencesAsync(HttpClient client, bool includeCompleted = false) =>
        await TestApp.ReadAsync<List<OccurrenceDto>>(await client.GetAsync("/api/task-occurrences" + (includeCompleted ? "?includeCompleted=true" : "")));
    private static async Task<List<ReminderDto>> RemindersAsync(HttpClient client, bool unreadOnly = false) =>
        await TestApp.ReadAsync<List<ReminderDto>>(await client.GetAsync("/api/task-reminders" + (unreadOnly ? "?unreadOnly=true" : "")));
    private static async Task<OccurrenceDto> SetCompletedAsync(HttpClient client, int occurrenceId, bool completed) =>
        await TestApp.ReadAsync<OccurrenceDto>(await client.PutAsJsonAsync($"/api/task-occurrences/{occurrenceId}", new { completed }));

    private static Task RunAsync(Func<WebApplicationFactory<Program>, HttpClient, FakeClock, Task> test)
    {
        var clock = new FakeClock(Start);
        return TestApp.RunIsolatedAsync((app, client) => test(app, client, clock), clock);
    }

    [Fact]
    public async Task RecurringEndpointsRequireAuthentication()
    {
        await RunAsync(async (_, client, _) =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/recurring-tasks")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/recurring-tasks", Request())).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/task-occurrences")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/task-reminders")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/recurring-tasks/preview", Request())).StatusCode);
        });
    }

    [Fact]
    public async Task CreatingADailyTaskSkipsTodayIfItsTimeHasPassedAndMaterialisesTheNextOccurrence()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "daily@test.local"));
            // It is 10:00 UTC on 2026-01-15; a 09:00 task starting today first falls due tomorrow.
            var task = await CreateAsync(client, Request());
            Assert.True(task.IsActive);
            Assert.Equal("Every day at 09:00", task.Description);
            Assert.Equal("2026-01-16", task.NextDueDate);
            Assert.Equal(new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc), task.NextDueUtc);
            Assert.Equal(new[] { "2026-01-16", "2026-01-17", "2026-01-18" }, task.Upcoming.Select(u => u.Date).ToArray());
            Assert.Equal(1, task.PendingCount);

            var occurrences = await OccurrencesAsync(client);
            var only = Assert.Single(occurrences);
            Assert.Equal(task.Id, only.RecurringTaskId);
            Assert.Equal("2026-01-16", only.DueDate);
            Assert.Equal("upcoming", only.Status);
            Assert.False(only.Completed);
            Assert.Equal(new DateTime(2026, 1, 16, 8, 30, 0, DateTimeKind.Utc), only.ReminderAtUtc); // 30 minutes before
            Assert.Null(only.ReminderSentUtc);

            // Listing the schedules reflects the same state.
            var listed = Assert.Single(await TasksAsync(client));
            Assert.Equal(task.Id, listed.Id);
            Assert.Equal(1, listed.PendingCount);
            Assert.Equal(0, listed.CompletedCount);
        });
    }

    [Fact]
    public async Task CreatingATaskWhoseTimeIsStillAheadTodayIsDueToday()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "today@test.local"));
            var task = await CreateAsync(client, Request(dueTime: "18:00", timeZoneId: "Europe/Berlin"));
            Assert.Equal("2026-01-15", task.NextDueDate);
            Assert.Equal(new DateTime(2026, 1, 15, 17, 0, 0, DateTimeKind.Utc), task.NextDueUtc); // 18:00 CET = 17:00 UTC
            Assert.Equal("Europe/Berlin", task.TimeZoneId);
        });
    }

    [Fact]
    public async Task InvalidDefinitionsReturnBadRequestAndCreateNothing()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "invalid@test.local"));
            var invalid = new object[]
            {
                Request(title: ""),
                Request(title: new string('x', 201)),
                Request(frequency: "hourly"),
                Request(interval: 0),
                Request(frequency: "monthly", interval: 13),
                Request(startDate: "15/01/2026"),
                Request(dueTime: "9 o'clock"),
                Request(timeZoneId: "Atlantis/Lost_City"),
                Request(reminderLeadMinutes: -5),
                Request(reminderLeadMinutes: 50000),
                Request(startDate: "2026-01-20", endDate: "2026-01-19"),
                Request(startDate: "2026-01-01", endDate: "2026-01-14"), // entirely in the past
                Request(startDate: "2000-01-01"),
            };
            foreach (var request in invalid)
            {
                using var response = await client.PostAsJsonAsync("/api/recurring-tasks", request);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for {JsonSerializer.Serialize(request)} but got {(int)response.StatusCode}: {body}");
                Assert.Contains("\"status\":400", body); // Problem Details, like the rest of the API
            }
            // Malformed JSON and the wrong shape are rejected by the binder with the same Problem Details contract.
            using var malformed = await client.PostAsync("/api/recurring-tasks", new StringContent("{not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            using var wrongType = await client.PostAsync("/api/recurring-tasks", new StringContent("{\"title\":\"x\",\"interval\":\"two\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
            using var completion = await client.PutAsJsonAsync("/api/task-occurrences/1", new { });
            Assert.Equal(HttpStatusCode.BadRequest, completion.StatusCode);

            Assert.Empty(await TasksAsync(client));
            Assert.Empty(await OccurrencesAsync(client));
        });
    }

    [Fact]
    public async Task MonthlyTaskPreservesTheDayOfMonthAndEveryOccurrenceHasItsOwnCompletion()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "monthly@test.local"));
            var task = await CreateAsync(client, Request(title: "Pay rent", frequency: "monthly", startDate: "2026-01-31", reminderLeadMinutes: 1440));
            Assert.Equal("Every month on day 31 at 09:00", task.Description);
            Assert.Equal(new[] { "2026-01-31", "2026-02-28", "2026-03-31" }, task.Upcoming.Select(u => u.Date).ToArray());

            // Nobody runs the API for two and a half months. On restart the scheduler catches up: Jan 31, Feb 28 and
            // Mar 31 are materialised as separate overdue occurrences and Apr 30 as the upcoming one.
            clock.Set("2026-04-01T12:00:00Z");
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(3, pass.OccurrencesCreated);
            Assert.Equal(3, pass.OccurrencesReminded); // Jan, Feb and Mar; the reminder for Apr 30 is not due until Apr 29
            Assert.Equal(1, pass.RemindersCreated);    // ...but they share one catch-up reminder instead of three
            var digest = Assert.Single(await RemindersAsync(client));
            Assert.Equal("\"Pay rent\" has 3 missed occurrences (2026-01-31 – 2026-03-31) that are still open.", digest.Message);

            var occurrences = await OccurrencesAsync(client);
            Assert.Equal(new[] { "2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30" }, occurrences.Select(o => o.DueDate).ToArray());
            Assert.Equal(new[] { "overdue", "overdue", "overdue", "upcoming" }, occurrences.Select(o => o.Status).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 3 }, occurrences.Select(o => o.SequenceNumber).ToArray());

            // Completing February leaves January and March open.
            var february = occurrences.Single(o => o.DueDate == "2026-02-28");
            var completed = await SetCompletedAsync(client, february.Id, true);
            Assert.True(completed.Completed);
            Assert.Equal("completed", completed.Status);
            Assert.Equal(new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc), completed.CompletedUtc);

            var open = await OccurrencesAsync(client);
            Assert.Equal(new[] { "2026-01-31", "2026-03-31", "2026-04-30" }, open.Select(o => o.DueDate).ToArray());
            var all = await OccurrencesAsync(client, includeCompleted: true);
            Assert.Equal(4, all.Count);
            Assert.Single(all, o => o.Completed);

            var listed = Assert.Single(await TasksAsync(client));
            Assert.Equal(3, listed.PendingCount);
            Assert.Equal(1, listed.CompletedCount);
            Assert.Equal("2026-04-30", listed.NextDueDate);
            Assert.Equal(new[] { "2026-04-30", "2026-05-31", "2026-06-30" }, listed.Upcoming.Select(u => u.Date).ToArray());

            // Re-opening works too and is reflected in the counts.
            var reopened = await SetCompletedAsync(client, february.Id, false);
            Assert.False(reopened.Completed);
            Assert.Null(reopened.CompletedUtc);
            Assert.Equal(4, (await OccurrencesAsync(client)).Count);
        });
    }

    [Fact]
    public async Task SchedulerRecoversMissedWorkAfterARestartWithoutDuplicates()
    {
        var clock = new FakeClock(Start);
        var path = TestApp.NewDatabasePath();
        try
        {
            int taskId;
            using (var app = TestApp.Create(path, clock))
            using (var client = app.CreateClient())
            {
                TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "restart@test.local"));
                taskId = (await CreateAsync(client, Request(reminderLeadMinutes: 30))).Id;
                Assert.Single(await OccurrencesAsync(client));
            }
            // The API is stopped for three days...
            clock.Set("2026-01-18T10:00:00Z");
            // ...and started again against the same database.
            using (var app = TestApp.Create(path, clock))
            using (var client = app.CreateClient())
            {
                var first = await TestApp.RunSchedulerAsync(app);
                Assert.Equal(3, first.OccurrencesCreated); // Jan 17, Jan 18 (missed) and Jan 19 (upcoming); Jan 16 already existed
                Assert.Equal(3, first.OccurrencesReminded); // Jan 16, 17 and 18 reminders were all due while stopped...
                Assert.Equal(1, first.RemindersCreated);    // ...and are reported as one catch-up reminder

                var second = await TestApp.RunSchedulerAsync(app);
                Assert.Equal(0, second.OccurrencesCreated);
                Assert.Equal(0, second.RemindersCreated);

                // Hammering the scheduler concurrently (as if several workers overlapped) still creates nothing new.
                var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => TestApp.RunSchedulerAsync(app)));
                Assert.All(concurrent, r => { Assert.Equal(0, r.OccurrencesCreated); Assert.Equal(0, r.RemindersCreated); });

                TestApp.Authenticate(client, await LoginAsync(client, "restart@test.local"));
                var occurrences = await OccurrencesAsync(client);
                Assert.Equal(new[] { "2026-01-16", "2026-01-17", "2026-01-18", "2026-01-19" }, occurrences.Select(o => o.DueDate).ToArray());
                Assert.Equal(new[] { "overdue", "overdue", "overdue", "upcoming" }, occurrences.Select(o => o.Status).ToArray());
                Assert.All(occurrences, o => Assert.Equal(taskId, o.RecurringTaskId));
                Assert.Equal(3, occurrences.Count(o => o.ReminderSentUtc != null));

                var digest = Assert.Single(await RemindersAsync(client));
                Assert.Null(digest.ReadUtc);
                Assert.Equal("\"Water plants\" has 3 missed occurrences (2026-01-16 – 2026-01-18) that are still open.", digest.Message);
                Assert.Equal(occurrences.Single(o => o.DueDate == "2026-01-18").Id, digest.OccurrenceId); // linked to the most recent missed one

                // Time moves on to just past the Jan 19 reminder time: exactly one more reminder, no new occurrence yet.
                clock.Set("2026-01-19T08:30:00Z");
                var third = await TestApp.RunSchedulerAsync(app);
                Assert.Equal(0, third.OccurrencesCreated);
                Assert.Equal(1, third.RemindersCreated);
                var latest = (await RemindersAsync(client)).First();
                Assert.Contains("is due on 2026-01-19 at 09:00", latest.Message);
                Assert.Equal(2, (await RemindersAsync(client, unreadOnly: true)).Count);
            }
        }
        finally { await TestApp.DeleteDatabaseAsync(path); }
    }

    [Fact]
    public async Task CompletedOccurrencesNeverRaiseRemindersAndCompletingSettlesAnExistingOne()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "settle@test.local"));
            await CreateAsync(client, Request(reminderLeadMinutes: 0)); // reminder exactly at the due time
            var upcoming = Assert.Single(await OccurrencesAsync(client));
            Assert.Equal(upcoming.DueUtc, upcoming.ReminderAtUtc);

            // Completed ahead of time: when the due time passes no reminder is raised for it.
            await SetCompletedAsync(client, upcoming.Id, true);
            clock.Set("2026-01-16T09:00:00Z");
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, pass.RemindersCreated);
            Assert.Equal(1, pass.OccurrencesCreated); // Jan 17 becomes the upcoming occurrence
            Assert.Empty(await RemindersAsync(client));

            // The next one is left open until after it falls due: its reminder is raised once and then settled by completing it.
            clock.Set("2026-01-17T09:00:00Z");
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            var reminder = Assert.Single(await RemindersAsync(client, unreadOnly: true));
            var open = (await OccurrencesAsync(client)).Single(o => o.DueDate == "2026-01-17");
            Assert.Equal(open.Id, reminder.OccurrenceId);
            await SetCompletedAsync(client, open.Id, true);
            Assert.Empty(await RemindersAsync(client, unreadOnly: true));
            Assert.NotNull((await RemindersAsync(client)).Single().ReadUtc);

            // Explicit acknowledgement endpoints.
            clock.Set("2026-01-18T09:00:00Z");
            await TestApp.RunSchedulerAsync(app);
            var unread = Assert.Single(await RemindersAsync(client, unreadOnly: true));
            var read = await TestApp.ReadAsync<ReminderDto>(await client.PostAsync($"/api/task-reminders/{unread.Id}/read", null));
            Assert.NotNull(read.ReadUtc);
            Assert.Empty(await RemindersAsync(client, unreadOnly: true));
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/task-reminders/999999/read", null)).StatusCode);
            using var readAll = await client.PostAsync("/api/task-reminders/read-all", null);
            Assert.Equal(HttpStatusCode.OK, readAll.StatusCode);
        });
    }

    [Fact]
    public async Task DisablingStopsSchedulingAndEnablingResumesWithoutBackfillingTheGap()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "disable@test.local"));
            var task = await CreateAsync(client, Request());
            Assert.Single(await OccurrencesAsync(client));

            var disabled = await TestApp.ReadAsync<TaskDto>(await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null));
            Assert.False(disabled.IsActive);
            Assert.Null(disabled.NextDueDate);
            Assert.Empty(disabled.Upcoming);
            Assert.Empty(await OccurrencesAsync(client)); // the not-yet-due occurrence is withdrawn

            // Disabling twice is harmless. While disabled, time passing creates nothing.
            Assert.False((await TestApp.ReadAsync<TaskDto>(await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null))).IsActive);
            clock.Set("2026-01-20T10:00:00Z");
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, pass.OccurrencesCreated);
            Assert.Equal(0, pass.RemindersCreated);
            Assert.Empty(await OccurrencesAsync(client));
            Assert.Empty(await RemindersAsync(client));

            // Enabling resumes with the next future occurrence; the five missed days are not backfilled as overdue.
            var enabled = await TestApp.ReadAsync<TaskDto>(await client.PostAsync($"/api/recurring-tasks/{task.Id}/enable", null));
            Assert.True(enabled.IsActive);
            Assert.Equal("2026-01-21", enabled.NextDueDate);
            var resumed = Assert.Single(await OccurrencesAsync(client));
            Assert.Equal("2026-01-21", resumed.DueDate);
            Assert.Equal("upcoming", resumed.Status);
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).OccurrencesCreated);

            // Overdue occurrences survive disabling (they are history, not future work).
            clock.Set("2026-01-22T10:00:00Z");
            await TestApp.RunSchedulerAsync(app);
            Assert.Equal(new[] { "2026-01-21", "2026-01-22", "2026-01-23" }, (await OccurrencesAsync(client)).Select(o => o.DueDate).ToArray());
            await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null);
            Assert.Equal(new[] { "2026-01-21", "2026-01-22" }, (await OccurrencesAsync(client)).Select(o => o.DueDate).ToArray());
            Assert.Single(await RemindersAsync(client)); // one catch-up reminder for Jan 21 + 22; the withdrawn Jan 23 had none yet

            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/recurring-tasks/999999/enable", null)).StatusCode);
        });
    }

    [Fact]
    public async Task EndDateExhaustsTheScheduleAndAnExhaustedScheduleCannotBeReEnabled()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "enddate@test.local"));
            var task = await CreateAsync(client, Request(startDate: "2026-01-16", endDate: "2026-01-18", reminderLeadMinutes: null));
            Assert.Equal(new[] { "2026-01-16", "2026-01-17", "2026-01-18" }, task.Upcoming.Select(u => u.Date).ToArray());

            clock.Set("2026-02-01T00:00:00Z");
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(2, pass.OccurrencesCreated);
            Assert.Equal(0, pass.RemindersCreated); // reminders disabled for this schedule
            Assert.Equal(3, (await OccurrencesAsync(client)).Count);
            Assert.All(await OccurrencesAsync(client), o => Assert.Null(o.ReminderAtUtc));
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).OccurrencesCreated);

            var listed = Assert.Single(await TasksAsync(client));
            Assert.True(listed.IsActive);
            Assert.Null(listed.NextDueDate);
            Assert.Empty(listed.Upcoming);

            await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null);
            using var enable = await client.PostAsync($"/api/recurring-tasks/{task.Id}/enable", null);
            Assert.Equal(HttpStatusCode.BadRequest, enable.StatusCode);
            Assert.Contains("no remaining occurrences", await enable.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task PreviewComputesDatesWithoutPersistingAnything()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "preview@test.local"));
            var weekly = await TestApp.ReadAsync<JsonElement>(await client.PostAsJsonAsync("/api/recurring-tasks/preview", Request(frequency: "weekly", interval: 2, startDate: "2026-01-19", dueTime: "18:30")));
            Assert.Equal("Every 2 weeks on Monday at 18:30", weekly.GetProperty("description").GetString());
            Assert.Equal(new[] { "2026-01-19", "2026-02-02", "2026-02-16", "2026-03-02", "2026-03-16", "2026-03-30" },
                weekly.GetProperty("upcoming").EnumerateArray().Select(u => u.GetProperty("date").GetString()).ToArray());

            // DST gap: 02:30 does not exist in New York on 2026-03-08, so that occurrence falls at 03:00 EDT (07:00Z).
            var dst = await TestApp.ReadAsync<JsonElement>(await client.PostAsJsonAsync("/api/recurring-tasks/preview", Request(startDate: "2026-03-07", dueTime: "02:30", timeZoneId: "America/New_York")));
            var instants = dst.GetProperty("upcoming").EnumerateArray().Select(u => u.GetProperty("dueUtc").GetDateTime().ToUniversalTime()).ToArray();
            Assert.Equal(new DateTime(2026, 3, 7, 7, 30, 0, DateTimeKind.Utc), instants[0]);
            Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), instants[1]);
            Assert.Equal(new DateTime(2026, 3, 9, 6, 30, 0, DateTimeKind.Utc), instants[2]);

            using var invalid = await client.PostAsJsonAsync("/api/recurring-tasks/preview", Request(frequency: "fortnightly"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            Assert.Empty(await TasksAsync(client));
            Assert.Empty(await OccurrencesAsync(client));
        });
    }

    [Fact]
    public async Task UsersOnlySeeAndChangeTheirOwnRecurringData()
    {
        await RunAsync(async (app, client, clock) =>
        {
            var tokenA = await TestApp.RegisterAndLoginAsync(client, "owner-a@test.local");
            var tokenB = await TestApp.RegisterAndLoginAsync(client, "owner-b@test.local");
            TestApp.Authenticate(client, tokenA);
            var task = await CreateAsync(client, Request(title: "A's task"));
            var occurrence = Assert.Single(await OccurrencesAsync(client));
            clock.Set("2026-01-16T09:00:00Z");
            await TestApp.RunSchedulerAsync(app);
            var reminder = Assert.Single(await RemindersAsync(client));

            TestApp.Authenticate(client, tokenB);
            Assert.Empty(await TasksAsync(client));
            Assert.Empty(await OccurrencesAsync(client));
            Assert.Empty(await RemindersAsync(client));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/recurring-tasks/{task.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/recurring-tasks/{task.Id}/enable", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recurring-tasks/{task.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/task-occurrences/{occurrence.Id}", new { completed = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/task-reminders/{reminder.Id}/read", null)).StatusCode);

            TestApp.Authenticate(client, tokenA);
            Assert.True((await TestApp.ReadAsync<TaskDto>(await client.GetAsync($"/api/recurring-tasks/{task.Id}"))).IsActive);
            Assert.False((await OccurrencesAsync(client)).Single(o => o.Id == occurrence.Id).Completed);
            Assert.Null((await RemindersAsync(client)).Single().ReadUtc);
        });
    }

    [Fact]
    public async Task DeletingAScheduleRemovesItsOccurrencesAndReminders()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "delete@test.local"));
            var doomed = await CreateAsync(client, Request(title: "Doomed"));
            var keeper = await CreateAsync(client, Request(title: "Keeper", dueTime: "08:00"));
            clock.Set("2026-01-17T12:00:00Z");
            await TestApp.RunSchedulerAsync(app);
            Assert.Equal(6, (await OccurrencesAsync(client)).Count); // 3 each: Jan 16 & 17 overdue, Jan 18 upcoming
            Assert.Equal(2, (await RemindersAsync(client)).Count);   // one catch-up reminder per schedule covering Jan 16 + 17

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recurring-tasks/{doomed.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recurring-tasks/{doomed.Id}")).StatusCode);

            Assert.Equal(keeper.Id, Assert.Single(await TasksAsync(client)).Id);
            Assert.All(await OccurrencesAsync(client, includeCompleted: true), o => Assert.Equal(keeper.Id, o.RecurringTaskId));
            Assert.All(await RemindersAsync(client), r => Assert.Equal(keeper.Id, r.RecurringTaskId));
            Assert.Equal(3, (await OccurrencesAsync(client)).Count);
            Assert.Single(await RemindersAsync(client));
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).OccurrencesCreated);
        });
    }

    [Fact]
    public async Task ExistingItemBackupIsUnaffectedByRecurringTasks()
    {
        await RunAsync(async (_, client, _) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "backup-recurring@test.local"));
            await CreateAsync(client, Request());
            using var backup = await client.GetAsync("/api/backup");
            Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
            Assert.Equal("[]", (await backup.Content.ReadAsStringAsync()).Trim());
            using var restore = await client.PostAsJsonAsync("/api/restore", Array.Empty<object>());
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            // Plain items keep working alongside recurring tasks.
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/items");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            request.Content = JsonContent.Create(new { kind = "task", title = "One-off", category = "", amount = 0, date = (string?)null });
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
            Assert.Single(await TestApp.ReadAsync<List<JsonElement>>(await client.GetAsync("/api/items")));
        });
    }

    [Fact]
    public async Task LongReminderLeadMaterialisesOccurrencesEarlyEnoughForRemindersToFireOnTime()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "lead@test.local"));
            // Two-day lead on a daily task: the Jan 17 occurrence's reminder time (Jan 15 09:00) has already passed when
            // the schedule is created at 10:00, so it must exist now, not only once Jan 16 falls due.
            var task = await CreateAsync(client, Request(reminderLeadMinutes: 2880));
            Assert.Equal(2, task.PendingCount);
            Assert.Equal("2026-01-16", task.NextDueDate);
            Assert.Equal(new[] { "2026-01-16", "2026-01-17" }, (await OccurrencesAsync(client)).Select(o => o.DueDate).ToArray());

            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, pass.OccurrencesCreated);
            Assert.Equal(2, pass.RemindersCreated); // both reminder times are in the past; both are "is due on" (not overdue)
            Assert.All(await RemindersAsync(client), r => Assert.Contains("is due on", r.Message));

            // Jan 16 falls due: Jan 18's reminder time (Jan 16 09:00) arrives at the same instant, so Jan 18 is created and
            // its reminder raised exactly two days ahead. Jan 19 (reminder Jan 17 09:00) is not created yet.
            clock.Set("2026-01-16T09:00:00Z");
            pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(1, pass.OccurrencesCreated);
            Assert.Equal(1, pass.RemindersCreated);
            var jan18 = (await OccurrencesAsync(client)).Single(o => o.DueDate == "2026-01-18");
            Assert.Equal(jan18.DueUtc.AddDays(-2), jan18.ReminderAtUtc);
            var reminder = (await RemindersAsync(client)).Single(r => r.OccurrenceId == jan18.Id);
            Assert.Equal(new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc), reminder.CreatedUtc);
            Assert.Equal(new[] { "2026-01-16", "2026-01-17", "2026-01-18" }, (await OccurrencesAsync(client)).Select(o => o.DueDate).ToArray());

            clock.Set("2026-01-16T09:01:00Z");
            pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, pass.OccurrencesCreated);
            Assert.Equal(0, pass.RemindersCreated);
            Assert.Equal(3, (await RemindersAsync(client)).Select(r => r.OccurrenceId).Distinct().Count());
        });
    }

    [Fact]
    public async Task DisableAndEnableNeverRepeatsAReminderTheUserAlreadySawAndKeepsTheNextDate()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "quick@test.local"));
            // "Announced": 1-day lead, so the reminder for Jan 16 (Jan 15 09:00) has already been shown.
            // "Quiet": 30-minute lead, so nothing has been shown yet for Jan 16.
            var announced = await CreateAsync(client, Request(title: "Announced", reminderLeadMinutes: 1440));
            var quiet = await CreateAsync(client, Request(title: "Quiet", reminderLeadMinutes: 30));
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(1, pass.RemindersCreated);
            var shown = Assert.Single(await RemindersAsync(client));
            Assert.Equal(announced.Id, shown.RecurringTaskId);
            var announcedOccurrence = (await OccurrencesAsync(client)).Single(o => o.RecurringTaskId == announced.Id);
            // The user dismisses it.
            await client.PostAsync($"/api/task-reminders/{shown.Id}/read", null);

            await client.PostAsync($"/api/recurring-tasks/{announced.Id}/disable", null);
            await client.PostAsync($"/api/recurring-tasks/{quiet.Id}/disable", null);
            // Quiet's unannounced occurrence is withdrawn; Announced's stays because the user has already seen its reminder.
            var kept = Assert.Single(await OccurrencesAsync(client));
            Assert.Equal(announcedOccurrence.Id, kept.Id);
            Assert.False(kept.ScheduleActive);
            Assert.NotNull(Assert.Single(await RemindersAsync(client)).ReadUtc); // the dismissed reminder is untouched

            clock.Advance(TimeSpan.FromMinutes(1));
            var announcedEnabled = await TestApp.ReadAsync<TaskDto>(await client.PostAsync($"/api/recurring-tasks/{announced.Id}/enable", null));
            var quietEnabled = await TestApp.ReadAsync<TaskDto>(await client.PostAsync($"/api/recurring-tasks/{quiet.Id}/enable", null));
            Assert.Equal("2026-01-16", announcedEnabled.NextDueDate); // same date, same stored occurrence
            Assert.Equal("2026-01-16", quietEnabled.NextDueDate);     // same date, re-created (not skipped to Jan 17)
            var occurrences = await OccurrencesAsync(client);
            Assert.Equal(2, occurrences.Count);
            Assert.Equal(announcedOccurrence.Id, occurrences.Single(o => o.RecurringTaskId == announced.Id).Id);
            Assert.Equal(1, occurrences.Single(o => o.RecurringTaskId == quiet.Id).SequenceNumber);
            Assert.All(occurrences, o => Assert.True(o.ScheduleActive));

            // No reminder is repeated for Announced; Quiet's fires at its normal time.
            pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, pass.RemindersCreated);
            Assert.Equal(0, pass.OccurrencesCreated);
            Assert.Single(await RemindersAsync(client));
            clock.Set("2026-01-16T08:30:00Z");
            pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(1, pass.RemindersCreated);
            var reminders = await RemindersAsync(client);
            Assert.Equal(2, reminders.Count);
            Assert.Single(reminders, r => r.RecurringTaskId == announced.Id);
            Assert.Single(reminders, r => r.RecurringTaskId == quiet.Id);
        });
    }

    [Fact]
    public async Task CompletingWhileSchedulerPassesRunConcurrentlyKeepsInvariants()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "race@test.local"));
            await CreateAsync(client, Request(reminderLeadMinutes: 0));
            var jan16 = Assert.Single(await OccurrencesAsync(client));
            clock.Set("2026-01-16T09:00:00Z"); // Jan 16 is due and its reminder is due right now

            // Several overlapping scheduler passes while the user completes the occurrence at the same time.
            var passes = Enumerable.Range(0, 4).Select(_ => TestApp.RunSchedulerAsync(app)).ToArray();
            var completion = SetCompletedAsync(client, jan16.Id, true);
            await Task.WhenAll(passes.Concat<Task>(new[] { completion }));
            Assert.True((await completion).Completed);

            // Invariants regardless of interleaving: the occurrence stays completed, no duplicate occurrences, at most one
            // reminder for it, and any reminder raised for the completed occurrence is already marked read.
            var all = await OccurrencesAsync(client, includeCompleted: true);
            Assert.Equal(new[] { "2026-01-16", "2026-01-17" }, all.Select(o => o.DueDate).ToArray());
            Assert.True(all.Single(o => o.Id == jan16.Id).Completed);
            var reminders = await RemindersAsync(client);
            Assert.True(reminders.Count(r => r.OccurrenceId == jan16.Id) <= 1);
            Assert.All(reminders.Where(r => r.OccurrenceId == jan16.Id), r => Assert.NotNull(r.ReadUtc));
            Assert.Empty(await RemindersAsync(client, unreadOnly: true));
            Assert.Equal(1, passes.Sum(p => p.Result.OccurrencesCreated)); // Jan 17 was created by exactly one pass
        });
    }

    [Fact]
    public async Task DeletingOrDisablingWhileSchedulerPassesRunConcurrentlyLeavesNoOrphans()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "orphans@test.local"));
            var doomed = await CreateAsync(client, Request(title: "Doomed"));
            var paused = await CreateAsync(client, Request(title: "Paused", dueTime: "08:00"));
            clock.Set("2026-01-25T10:00:00Z"); // nine days of catch-up work for each schedule

            var passes = Enumerable.Range(0, 3).Select(_ => TestApp.RunSchedulerAsync(app)).ToArray();
            var delete = client.DeleteAsync($"/api/recurring-tasks/{doomed.Id}");
            var disable = client.PostAsync($"/api/recurring-tasks/{paused.Id}/disable", null);
            await Task.WhenAll(passes.Concat<Task>(new Task[] { delete, disable }));
            Assert.Equal(HttpStatusCode.NoContent, (await delete).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await disable).StatusCode);

            // Deleted: nothing left anywhere. Disabled: inactive, no upcoming occurrence, history (if any) intact.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/recurring-tasks/{doomed.Id}")).StatusCode);
            var occurrences = await OccurrencesAsync(client, includeCompleted: true);
            Assert.All(occurrences, o => Assert.Equal(paused.Id, o.RecurringTaskId));
            Assert.All(occurrences, o => Assert.True(o.DueUtc <= new DateTime(2026, 1, 25, 10, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(occurrences.Select(o => o.SequenceNumber).Distinct().Count(), occurrences.Count);
            Assert.All(await RemindersAsync(client), r => Assert.Equal(paused.Id, r.RecurringTaskId));
            var listed = Assert.Single(await TasksAsync(client));
            Assert.False(listed.IsActive);

            // Once settled, further passes have nothing to do.
            var after = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, after.OccurrencesCreated);
            Assert.Equal(0, after.RemindersCreated);
        });
    }

    [Fact]
    public async Task VeryLongOutageIsRecoveredAcrossPassesWithoutDuplicates()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "outage@test.local"));
            var task = await CreateAsync(client, Request(reminderLeadMinutes: 30)); // Jan 16 2026 exists
            // 600 more daily occurrences (Jan 17 2026 … Sep 8 2027) are due when the API comes back.
            clock.Set("2027-09-08T10:00:00Z");

            var first = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(RecurringTaskScheduler.MaxOccurrencesPerSchedulePerPass, first.OccurrencesCreated); // capped per pass
            Assert.Equal(RecurringTaskScheduler.MaxRemindersPerPass, first.OccurrencesReminded);
            Assert.Equal(1, first.RemindersCreated); // one catch-up reminder, not 500 notifications
            var second = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(101, second.OccurrencesCreated); // the remaining 100 overdue ones plus Sep 9 as the upcoming one
            Assert.Equal(101, second.OccurrencesReminded); // remaining 101 overdue reminders; Sep 9's is not due until 08:30
            Assert.Equal(1, second.RemindersCreated);
            var third = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, third.OccurrencesCreated);
            Assert.Equal(0, third.RemindersCreated);

            var listed = Assert.Single(await TasksAsync(client));
            Assert.Equal(602, listed.PendingCount); // every missed occurrence is still listed and completable
            Assert.Equal("2027-09-09", listed.NextDueDate);
            var reminders = await RemindersAsync(client);
            Assert.Equal(2, reminders.Count);
            Assert.Contains("has 500 missed occurrences (2026-01-16 – 2027-05-30)", reminders.Last().Message);
            Assert.Contains("has 101 missed occurrences (2027-05-31 – 2027-09-08)", reminders.First().Message);
            var readAll = await TestApp.ReadAsync<JsonElement>(await client.PostAsync("/api/task-reminders/read-all", null));
            Assert.Equal(2, readAll.GetProperty("updated").GetInt32());
        });
    }

    [Fact]
    public async Task DaylightSavingKeepsWallClockDueTimesAndWholeDayRemindersWhileShortLeadsStayExact()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero));
        await TestApp.RunIsolatedAsync(async (app, client) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "dst@test.local"));
            Assert.True(Recurrence.TryResolveTimeZone("America/New_York", out var newYork));
            TimeOnly Local(DateTime utc) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, newYork));

            // US clocks spring forward on 2026-03-08. 09:00 New York is 14:00Z on Mar 7 (EST) and 13:00Z from Mar 8 (EDT).
            var daily = await CreateAsync(client, Request(title: "Daily 09:00", startDate: "2026-03-07", dueTime: "09:00", timeZoneId: "America/New_York", reminderLeadMinutes: 1440));
            // A 60-minute lead on a 03:30 task on the switch-over day: 03:30 EDT is 07:30Z and the reminder is exactly 60 minutes earlier.
            var early = await CreateAsync(client, Request(title: "Early", startDate: "2026-03-08", dueTime: "03:30", timeZoneId: "America/New_York", reminderLeadMinutes: 60));
            var mar7 = (await OccurrencesAsync(client)).Single(o => o.RecurringTaskId == daily.Id);
            Assert.Equal(new DateTime(2026, 3, 7, 14, 0, 0, DateTimeKind.Utc), mar7.DueUtc);
            Assert.Equal(new DateTime(2026, 3, 6, 14, 0, 0, DateTimeKind.Utc), mar7.ReminderAtUtc); // 09:00 EST the day before
            Assert.Equal(new[] { "2026-03-07", "2026-03-08", "2026-03-09" }, daily.Upcoming.Select(u => u.Date).ToArray());
            Assert.Equal(new DateTime(2026, 3, 8, 13, 0, 0, DateTimeKind.Utc), daily.Upcoming[1].DueUtc);
            var earlyMar8 = (await OccurrencesAsync(client)).Single(o => o.RecurringTaskId == early.Id);
            Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), earlyMar8.DueUtc);
            Assert.Equal(new DateTime(2026, 3, 8, 6, 30, 0, DateTimeKind.Utc), earlyMar8.ReminderAtUtc); // 01:30 EST: 60 real minutes before

            clock.Set("2026-03-06T13:59:00Z");
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            clock.Set("2026-03-06T14:00:00Z");
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            Assert.Contains("2026-03-07 at 09:00 (America/New_York)", (await RemindersAsync(client)).Single().Message);

            // The whole-day lead follows the wall clock: Mar 8's reminder is at 09:00 local on Mar 7 (14:00Z), not 24 real
            // hours before 13:00Z. At 13:00Z nothing happens yet; at 14:00Z the occurrence is created and reminded.
            clock.Set("2026-03-07T13:00:00Z");
            var pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(0, pass.OccurrencesCreated);
            Assert.Equal(0, pass.RemindersCreated);
            clock.Set("2026-03-07T14:00:00Z");
            pass = await TestApp.RunSchedulerAsync(app);
            Assert.Equal(1, pass.OccurrencesCreated);
            Assert.Equal(1, pass.RemindersCreated);
            var mar8 = (await OccurrencesAsync(client)).Single(o => o.DueDate == "2026-03-08" && o.RecurringTaskId == daily.Id);
            Assert.Equal(new DateTime(2026, 3, 8, 13, 0, 0, DateTimeKind.Utc), mar8.DueUtc);
            Assert.Equal(new DateTime(2026, 3, 7, 14, 0, 0, DateTimeKind.Utc), mar8.ReminderAtUtc);
            Assert.Equal(new TimeOnly(9, 0), Local(mar8.ReminderAtUtc!.Value));
            Assert.Contains("2026-03-08 at 09:00 (America/New_York)", (await RemindersAsync(client)).First().Message);

            // The short lead is exact: the 03:30 reminder fires at 06:30Z on the switch-over day.
            clock.Set("2026-03-08T06:29:00Z");
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            clock.Set("2026-03-08T06:30:00Z");
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            Assert.Contains("\"Early\" is due on 2026-03-08 at 03:30 (America/New_York)", (await RemindersAsync(client)).First().Message);

            // Across the whole week every daily occurrence is at 09:00 wall-clock: 14:00Z before the change, 13:00Z after.
            // At 13:00Z on Mar 12 that day's occurrence is due, Mar 13 becomes the upcoming one, and its reminder (09:00 local
            // on Mar 12 = 13:00Z) is due as well. Mar 9–12 were missed and share one catch-up reminder.
            clock.Set("2026-03-12T13:00:00Z");
            pass = await TestApp.RunSchedulerAsync(app);
            var week = (await OccurrencesAsync(client)).Where(o => o.RecurringTaskId == daily.Id).ToList();
            Assert.Equal(new[] { "2026-03-07", "2026-03-08", "2026-03-09", "2026-03-10", "2026-03-11", "2026-03-12", "2026-03-13" }, week.Select(o => o.DueDate).ToArray());
            Assert.Equal(new[] { 14, 13, 13, 13, 13, 13, 13 }, week.Select(o => o.DueUtc.Hour).ToArray());
            Assert.All(week, o => Assert.Equal(new TimeOnly(9, 0), Local(o.ReminderAtUtc!.Value)));
            Assert.All(week, o => Assert.NotNull(o.ReminderSentUtc));
            var reminders = (await RemindersAsync(client)).Where(r => r.RecurringTaskId == daily.Id).ToList();
            Assert.Equal(4, reminders.Count); // Mar 7, Mar 8, catch-up for Mar 9–12, Mar 13
            Assert.Contains(reminders, r => r.Message.Contains("has 4 missed occurrences (2026-03-09 – 2026-03-12)"));
            Assert.Contains(reminders, r => r.Message.Contains("is due on 2026-03-13 at 09:00"));
        }, clock);
    }

    [Fact]
    public async Task TwoApiInstancesSharingOneDatabaseCooperateWithoutDuplicatesOrLockFailures()
    {
        var clock = new FakeClock(Start);
        var path = TestApp.NewDatabasePath();
        try
        {
            // Two independent hosts (separate DI containers, DbContexts and connections) on the same SQLite file, like two
            // API processes. The second host finds the schema present and runs the additive bootstrap path.
            using var appA = TestApp.Create(path, clock);
            using var appB = TestApp.Create(path, clock);
            using var clientA = appA.CreateClient();
            using var clientB = appB.CreateClient();
            var token = await TestApp.RegisterAndLoginAsync(clientA, "shared@test.local");
            TestApp.Authenticate(clientA, token);
            TestApp.Authenticate(clientB, token); // same signing key, so the token is valid on both hosts
            var task = await CreateAsync(clientA, Request(reminderLeadMinutes: 30));
            Assert.Equal(task.Id, Assert.Single(await TasksAsync(clientB)).Id);

            // Nine days pass; both instances' workers wake up at the same time and also serve API traffic meanwhile.
            clock.Set("2026-01-25T10:00:00Z");
            var passes = Enumerable.Range(0, 3).SelectMany(_ => new[] { TestApp.RunSchedulerAsync(appA), TestApp.RunSchedulerAsync(appB) }).ToArray();
            var reads = new[] { OccurrencesAsync(clientA), OccurrencesAsync(clientB) };
            await Task.WhenAll(passes.Concat<Task>(reads));

            var results = passes.Select(p => p.Result).ToList();
            Assert.Equal(10, results.Sum(r => r.OccurrencesCreated));   // Jan 17–25 missed plus Jan 26 upcoming, created once in total
            Assert.Equal(1, results.Sum(r => r.RemindersCreated));      // one catch-up reminder covering Jan 16–25
            Assert.Equal(10, results.Sum(r => r.OccurrencesReminded));
            var fromA = await OccurrencesAsync(clientA);
            var fromB = await OccurrencesAsync(clientB);
            Assert.Equal(fromA.Select(o => o.Id), fromB.Select(o => o.Id));
            Assert.Equal(Enumerable.Range(1, 11), fromA.Select(o => o.SequenceNumber)); // Jan 16 … Jan 26, no gaps, no duplicates
            var digest = Assert.Single(await RemindersAsync(clientB));
            Assert.Contains("has 10 missed occurrences (2026-01-16 – 2026-01-25)", digest.Message);

            // Writes through one instance are immediately consistent for the other, and the other's pass respects them.
            await SetCompletedAsync(clientB, fromA.Single(o => o.DueDate == "2026-01-26").Id, true);
            await clientB.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null);
            clock.Set("2026-01-28T10:00:00Z");
            var after = await Task.WhenAll(TestApp.RunSchedulerAsync(appA), TestApp.RunSchedulerAsync(appB));
            Assert.All(after, r => { Assert.Equal(0, r.OccurrencesCreated); Assert.Equal(0, r.RemindersCreated); });
            Assert.False(Assert.Single(await TasksAsync(clientA)).IsActive);
            Assert.True((await OccurrencesAsync(clientA, includeCompleted: true)).Single(o => o.DueDate == "2026-01-26").Completed);
        }
        finally { await TestApp.DeleteDatabaseAsync(path); }
    }

    [Fact]
    public async Task ConcurrentInitialisationOfANewDatabaseSucceedsForEveryInstance()
    {
        // Several instances initialising the same brand-new file at once (the startup race). Repeated on fresh files to
        // hit the "both saw no tables" window with high probability; every attempt must succeed and leave a usable schema.
        for (var round = 0; round < 5; round++)
        {
            var path = TestApp.NewDatabasePath();
            try
            {
                var contexts = Enumerable.Range(0, 6).Select(_ => new LifeDb(new DbContextOptionsBuilder<LifeDb>().UseSqlite($"Data Source={path};Pooling=False").Options)).ToList();
                try
                {
                    await Task.WhenAll(contexts.Select(db => Task.Run(() => SchemaBootstrap.InitializeAsync(db, NullLogger.Instance))));
                }
                finally { foreach (var db in contexts) await db.DisposeAsync(); }

                await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
                var tables = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
                foreach (var expected in new[] { "Users", "Items", "Reminders", "AuditEvents", "IdempotencyRecords", "RecurringTasks", "TaskOccurrences", "TaskReminders", "TaskReminderOccurrences" })
                    Assert.Contains(expected, tables);
                Assert.Equal(tables.Count, tables.Distinct().Count());
            }
            finally { await TestApp.DeleteDatabaseAsync(path); }
        }
    }

    [Fact]
    public async Task TwoHostsStartingSimultaneouslyOnANewDatabaseBothComeUp()
    {
        var clock = new FakeClock(Start);
        var path = TestApp.NewDatabasePath();
        try
        {
            using var appA = TestApp.Create(path, clock);
            using var appB = TestApp.Create(path, clock);
            // The host (and Program's schema initialisation) is built on first use; start both at the same moment.
            var clients = await Task.WhenAll(Task.Run(() => appA.CreateClient()), Task.Run(() => appB.CreateClient()));
            using var clientA = clients[0];
            using var clientB = clients[1];
            Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync("/api/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/api/health")).StatusCode);

            // Both instances work against the one schema: register on A, sign in on B, write on B, read on A.
            var token = await TestApp.RegisterAndLoginAsync(clientA, "boot@test.local");
            TestApp.Authenticate(clientA, token);
            TestApp.Authenticate(clientB, token);
            var task = await CreateAsync(clientB, Request());
            Assert.Equal(task.Id, Assert.Single(await TasksAsync(clientA)).Id);
            Assert.Single(await OccurrencesAsync(clientA));
            using var backup = await clientA.GetAsync("/api/backup");
            Assert.Equal("[]", (await backup.Content.ReadAsStringAsync()).Trim());
        }
        finally { await TestApp.DeleteDatabaseAsync(path); }
    }

    [Fact]
    public async Task CatchUpSummaryStaysUntilEveryMissedOccurrenceIsCompletedAndReportsTheRest()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "summary@test.local"));
            await CreateAsync(client, Request(title: "Pay rent", frequency: "monthly", startDate: "2026-01-31", reminderLeadMinutes: 1440));
            clock.Set("2026-04-01T12:00:00Z");
            await TestApp.RunSchedulerAsync(app);
            var occurrences = await OccurrencesAsync(client);
            var summary = Assert.Single(await RemindersAsync(client));
            Assert.Equal("\"Pay rent\" has 3 missed occurrences (2026-01-31 – 2026-03-31) that are still open.", summary.Message);
            Assert.Equal(occurrences.Single(o => o.DueDate == "2026-03-31").Id, summary.OccurrenceId);

            // Completing the occurrence the summary is linked to (the most recent one) must not dismiss it.
            await SetCompletedAsync(client, summary.OccurrenceId, true);
            var afterMarch = Assert.Single(await RemindersAsync(client));
            Assert.Null(afterMarch.ReadUtc);
            Assert.Equal("\"Pay rent\" has 3 missed occurrences (2026-01-31 – 2026-03-31); 2 of them are still open.", afterMarch.Message);
            Assert.Single(await RemindersAsync(client, unreadOnly: true));

            await SetCompletedAsync(client, occurrences.Single(o => o.DueDate == "2026-01-31").Id, true);
            Assert.Equal("\"Pay rent\" has 3 missed occurrences (2026-01-31 – 2026-03-31); 1 of them is still open.", Assert.Single(await RemindersAsync(client)).Message);
            Assert.Null(Assert.Single(await RemindersAsync(client)).ReadUtc);

            // The last one settles the summary.
            clock.Set("2026-04-01T12:30:00Z");
            await SetCompletedAsync(client, occurrences.Single(o => o.DueDate == "2026-02-28").Id, true);
            var settled = Assert.Single(await RemindersAsync(client));
            Assert.Equal(new DateTime(2026, 4, 1, 12, 30, 0, DateTimeKind.Utc), settled.ReadUtc);
            Assert.Equal("\"Pay rent\" has 3 missed occurrences (2026-01-31 – 2026-03-31); all of them are completed.", settled.Message);
            Assert.Empty(await RemindersAsync(client, unreadOnly: true));

            // Reopening refreshes the text but does not un-dismiss a settled reminder.
            await SetCompletedAsync(client, occurrences.Single(o => o.DueDate == "2026-02-28").Id, false);
            var reopened = Assert.Single(await RemindersAsync(client));
            Assert.Contains("1 of them is still open", reopened.Message);
            Assert.NotNull(reopened.ReadUtc);

            // Ordinary (single-occurrence) reminders behave as before: settled by completing their occurrence.
            clock.Set("2026-04-29T09:00:00Z");
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            var april = (await RemindersAsync(client, unreadOnly: true)).Single();
            Assert.Contains("is due on 2026-04-30", april.Message);
            await SetCompletedAsync(client, april.OccurrenceId, true);
            Assert.Empty(await RemindersAsync(client, unreadOnly: true));
            Assert.Equal(2, (await RemindersAsync(client)).Count);
        });
    }

    [Fact]
    public async Task DatabaseFromAnOlderVersionGainsTheNewTableOnStartupAndLegacyRemindersStillSettle()
    {
        var clock = new FakeClock(Start);
        var path = TestApp.NewDatabasePath();
        try
        {
            int taskId;
            using (var app = TestApp.Create(path, clock))
            using (var client = app.CreateClient())
            {
                TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "upgrade@test.local"));
                taskId = (await CreateAsync(client, Request(reminderLeadMinutes: 1440))).Id; // Jan 16; reminder time already passed
                Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);
            }
            // Simulate a database written by the previous version: the link table does not exist and the existing reminder
            // therefore has no link rows.
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE TaskReminderOccurrences";
                await drop.ExecuteNonQueryAsync();
            }
            using (var app = TestApp.Create(path, clock))
            using (var client = app.CreateClient())
            {
                // Startup re-created the missing table; everything written before is intact.
                await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    await connection.OpenAsync();
                    var check = connection.CreateCommand();
                    check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'TaskReminderOccurrences'";
                    Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
                }
                TestApp.Authenticate(client, await LoginAsync(client, "upgrade@test.local"));
                Assert.Equal(taskId, Assert.Single(await TasksAsync(client)).Id);
                var legacy = Assert.Single(await RemindersAsync(client));
                var occurrence = Assert.Single(await OccurrencesAsync(client));

                // A reminder without link rows falls back to the occurrence it points at and is settled by completing it.
                await SetCompletedAsync(client, occurrence.Id, true);
                Assert.NotNull(Assert.Single(await RemindersAsync(client)).ReadUtc);
                Assert.Equal(legacy.Id, Assert.Single(await RemindersAsync(client)).Id);

                // New reminders on the upgraded database use the link table normally.
                clock.Set("2026-01-20T10:00:00Z");
                var pass = await TestApp.RunSchedulerAsync(app);
                Assert.Equal(2, pass.RemindersCreated); // catch-up summary for Jan 17–20 plus Jan 21's own 1-day reminder
                var summary = (await RemindersAsync(client, unreadOnly: true)).Single(r => r.Message.Contains("missed"));
                Assert.Contains("has 4 missed occurrences (2026-01-17 – 2026-01-20)", summary.Message);
                await SetCompletedAsync(client, summary.OccurrenceId, true);
                Assert.Contains("3 of them are still open", (await RemindersAsync(client, unreadOnly: true)).Single(r => r.Message.Contains("missed")).Message);
            }
        }
        finally { await TestApp.DeleteDatabaseAsync(path); }
    }

    [Fact]
    public async Task ScheduleWhoseFinalOccurrenceIsStillUpcomingCanBeReEnabled()
    {
        await RunAsync(async (app, client, clock) =>
        {
            TestApp.Authenticate(client, await TestApp.RegisterAndLoginAsync(client, "final@test.local"));
            // One-off in recurring clothing: a single occurrence on Jan 16 whose 1-day reminder has already been shown.
            var task = await CreateAsync(client, Request(startDate: "2026-01-16", endDate: "2026-01-16", reminderLeadMinutes: 1440));
            Assert.Equal(1, (await TestApp.RunSchedulerAsync(app)).RemindersCreated);

            await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null);
            var kept = Assert.Single(await OccurrencesAsync(client)); // announced, so kept
            Assert.False(kept.ScheduleActive);

            var enabled = await TestApp.ReadAsync<TaskDto>(await client.PostAsync($"/api/recurring-tasks/{task.Id}/enable", null));
            Assert.True(enabled.IsActive);
            Assert.Equal("2026-01-16", enabled.NextDueDate);
            Assert.Equal(kept.Id, Assert.Single(await OccurrencesAsync(client)).Id);
            Assert.Equal(0, (await TestApp.RunSchedulerAsync(app)).RemindersCreated); // not reminded again
            Assert.Single(await RemindersAsync(client));

            // Once that occurrence is completed the schedule really is exhausted and can no longer be re-enabled.
            await SetCompletedAsync(client, kept.Id, true);
            await client.PostAsync($"/api/recurring-tasks/{task.Id}/disable", null);
            using var exhausted = await client.PostAsync($"/api/recurring-tasks/{task.Id}/enable", null);
            Assert.Equal(HttpStatusCode.BadRequest, exhausted.StatusCode);
        });
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
