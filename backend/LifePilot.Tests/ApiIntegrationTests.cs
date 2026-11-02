using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public class ApiIntegrationTests
{
    private const string Password = "TestPassword123!";

    private static WebApplicationFactory<Program> CreateApp(string databasePath)
    {
        // This is a test-only key, never a production credential.
        Environment.SetEnvironmentVariable(
            "LIFEPILOT_JWT_KEY",
            "test-only-32-byte-secret-0123456789abcdef");

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Remove the application's original database configuration.
                    var descriptors = services
                        .Where(x =>
                            x.ServiceType == typeof(DbContextOptions<LifeDb>) ||
                            x.ServiceType == typeof(DbContextOptions))
                        .ToList();

                    foreach (var descriptor in descriptors)
                        services.Remove(descriptor);

                    services.AddDbContext<LifeDb>(options =>
                        options.UseSqlite($"Data Source={databasePath};Pooling=False"));

                    // Avoid running the background workers (reminders, recurring tasks) in these tests.
                    var workers = services
                        .Where(x =>
                            x.ServiceType == typeof(IHostedService) &&
                            (x.ImplementationType == typeof(ReminderWorker) ||
                             x.ImplementationType == typeof(RecurringTaskWorker)))
                        .ToList();

                    foreach (var worker in workers)
                        services.Remove(worker);
                });
            });
    }

    private static async Task RunIsolatedAsync(
    Func<HttpClient, Task> test)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"lifepilot-test-{Guid.NewGuid():N}.db");

        try
        {
            using (var app = CreateApp(path))
            using (var client = app.CreateClient())
            {
                await test(client);
            }
        }
        finally
        {
            // Release pooled SQLite connections before deleting the files.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = path + suffix;

                if (!File.Exists(file))
                    continue;

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.Delete(file);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await Task.Delay(200);
                    }
                }
            }
        }
    }

    private static async Task<string> RegisterAndLoginAsync(
        HttpClient client,
        string email)
    {
        var credentials = new
        {
            email,
            password = Password
        };

        var registration = await client.PostAsJsonAsync(
            "/api/auth/register", credentials);

        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

        var login = await client.PostAsJsonAsync(
            "/api/auth/login", credentials);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var json = JsonDocument.Parse(
            await login.Content.ReadAsStringAsync());

        return json.RootElement
            .GetProperty("accessToken")
            .GetString()!;
    }

    private static void Authenticate(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<HttpResponseMessage> CreateItemAsync(
        HttpClient client,
        string title,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/items");

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        request.Content = JsonContent.Create(new
        {
            kind = "task",
            title,
            category = "Testing",
            amount = 0,
            date = (string?)null
        });

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task HealthIsAvailableAndItemsRequireAuthentication()
    {
        await RunIsolatedAsync(async client =>
        {
            var health = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var items = await client.GetAsync("/api/items");
            Assert.Equal(HttpStatusCode.Unauthorized, items.StatusCode);
        });
    }

    [Fact]
    public async Task RegistrationAndLoginReturnValidToken()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "register@test.local");

            Assert.False(string.IsNullOrWhiteSpace(token));

            Authenticate(client, token);

            var response = await client.GetAsync("/api/me");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
    }

    [Fact]
    public async Task LoginRejectsIncorrectPassword()
    {
        await RunIsolatedAsync(async client =>
        {
            await RegisterAndLoginAsync(
                client, "wrongpassword@test.local");

            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new
                {
                    email = "wrongpassword@test.local",
                    password = "IncorrectPassword123!"
                });

            Assert.Equal(
                HttpStatusCode.Unauthorized,
                response.StatusCode);
        });
    }

    [Fact]
    public async Task MemberCannotAccessAdminEndpoint()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "member@test.local");

            Authenticate(client, token);

            var response = await client.GetAsync("/api/admin/stats");

            Assert.Equal(
                HttpStatusCode.Forbidden,
                response.StatusCode);
        });
    }

    [Fact]
    public async Task UsersCannotAccessEachOthersItems()
    {
        await RunIsolatedAsync(async client =>
        {
            var userAToken = await RegisterAndLoginAsync(
                client, "usera@test.local");

            var userBToken = await RegisterAndLoginAsync(
                client, "userb@test.local");

            Authenticate(client, userAToken);

            using var created = await CreateItemAsync(
                client,
                "Private task belonging to User A",
                Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            Authenticate(client, userBToken);

            var response = await client.GetAsync("/api/items");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            Assert.Equal(0, json.RootElement.GetArrayLength());
        });
    }

    [Fact]
    public async Task RepeatedIdempotencyKeyDoesNotCreateDuplicate()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "idempotency@test.local");

            Authenticate(client, token);

            var key = Guid.NewGuid().ToString();

            using var first = await CreateItemAsync(
                client, "Buy groceries", key);

            using var second = await CreateItemAsync(
                client, "Buy groceries", key);

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            using var firstJson = JsonDocument.Parse(
                await first.Content.ReadAsStringAsync());

            using var secondJson = JsonDocument.Parse(
                await second.Content.ReadAsStringAsync());

            var firstId = firstJson.RootElement
                .GetProperty("id").GetInt32();

            var secondId = secondJson.RootElement
                .GetProperty("id").GetInt32();

            Assert.Equal(firstId, secondId);

            var items = await client.GetAsync("/api/items");

            using var itemsJson = JsonDocument.Parse(
                await items.Content.ReadAsStringAsync());

            Assert.Equal(1, itemsJson.RootElement.GetArrayLength());
        });
    }

    [Fact]
    public async Task InvalidItemReturnsBadRequest()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "validation@test.local");

            Authenticate(client, token);

            using var response = await CreateItemAsync(
                client,
                "",
                Guid.NewGuid().ToString());

            Assert.Equal(
                HttpStatusCode.BadRequest,
                response.StatusCode);
        });
    }

    // ----- Backup / restore helpers -----

    public sealed record RestoreResult(int Imported, int Skipped, int Total);

    private static object BackupItem(
        string kind,
        string title,
        string? category = "Testing",
        decimal amount = 0m,
        string? date = null)
    {
        return new { kind, title, category, amount, date };
    }

    private static async Task<RestoreResult> RestoreAsync(
        HttpClient client,
        object payload)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/restore", payload);

        var body = await response.Content.ReadAsStringAsync();

        // Include the server's message so a failing restore is diagnosable from the test output alone.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 OK from /api/restore but got {(int)response.StatusCode}: {body}");

        var result = JsonSerializer.Deserialize<RestoreResult>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);

        return result;
    }

    private static async Task<HttpResponseMessage> RestoreRawAsync(
        HttpClient client,
        string json)
    {
        return await client.PostAsync(
            "/api/restore",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task<JsonDocument> GetItemsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> CountItemsAsync(HttpClient client)
    {
        using var items = await GetItemsAsync(client);

        return items.RootElement.GetArrayLength();
    }

    private static async Task<int> GetUserIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return json.RootElement.GetProperty("userId").GetInt32();
    }

    private static object[] SampleBackup()
    {
        return new[]
        {
            BackupItem("task", "Call the dentist"),
            BackupItem("grocery", "Milk", "Dairy", 2.49m),
            BackupItem("event", "Team standup", "Work", 0, "2030-01-15")
        };
    }

    // ----- Backup / restore tests -----

    [Fact]
    public async Task BackupAndRestoreRequireAuthentication()
    {
        await RunIsolatedAsync(async client =>
        {
            // Anonymous requests are rejected before any validation or database work.
            using var anonymousBackup = await client.GetAsync("/api/backup");

            Assert.Equal(HttpStatusCode.Unauthorized, anonymousBackup.StatusCode);

            using var anonymousRestore = await client.PostAsJsonAsync(
                "/api/restore", SampleBackup());

            Assert.Equal(HttpStatusCode.Unauthorized, anonymousRestore.StatusCode);

            // A syntactically plausible but unsigned/forged token is rejected the same way.
            Authenticate(client, "eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0.");

            using var forgedRestore = await client.PostAsJsonAsync(
                "/api/restore", SampleBackup());

            Assert.Equal(HttpStatusCode.Unauthorized, forgedRestore.StatusCode);

            // Nothing was written for anyone: a real user still sees an empty account.
            var token = await RegisterAndLoginAsync(
                client, "restore-auth@test.local");

            Authenticate(client, token);

            Assert.Equal(0, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task RestoreFirstImportCreatesEveryItem()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-first@test.local");

            Authenticate(client, token);

            var result = await RestoreAsync(client, SampleBackup());

            Assert.Equal(3, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(3, result.Total);

            using var items = await GetItemsAsync(client);

            Assert.Equal(3, items.RootElement.GetArrayLength());

            var titles = items.RootElement.EnumerateArray()
                .Select(x => x.GetProperty("title").GetString())
                .OrderBy(x => x)
                .ToArray();

            Assert.Equal(
                new[] { "Call the dentist", "Milk", "Team standup" },
                titles);

            var milk = items.RootElement.EnumerateArray()
                .Single(x => x.GetProperty("title").GetString() == "Milk");

            Assert.Equal("grocery", milk.GetProperty("kind").GetString());
            Assert.Equal("Dairy", milk.GetProperty("category").GetString());
            Assert.Equal(2.49m, milk.GetProperty("amount").GetDecimal());
        });
    }

    [Fact]
    public async Task RestoreRepeatedImportSkipsEverything()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-repeat@test.local");

            Authenticate(client, token);

            var first = await RestoreAsync(client, SampleBackup());

            Assert.Equal(3, first.Imported);

            var second = await RestoreAsync(client, SampleBackup());

            Assert.Equal(0, second.Imported);
            Assert.Equal(3, second.Skipped);
            Assert.Equal(3, second.Total);

            Assert.Equal(3, await CountItemsAsync(client));

            // A backup exported by the API must round-trip as a no-op.
            using var backup = await client.GetAsync("/api/backup");

            Assert.Equal(HttpStatusCode.OK, backup.StatusCode);

            using var roundTrip = await RestoreRawAsync(
                client, await backup.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, roundTrip.StatusCode);

            var roundTripResult = await roundTrip.Content
                .ReadFromJsonAsync<RestoreResult>();

            Assert.NotNull(roundTripResult);
            Assert.Equal(0, roundTripResult.Imported);
            Assert.Equal(3, roundTripResult.Skipped);

            Assert.Equal(3, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task RestoreImportsNewItemsAndSkipsExistingOnes()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-mixed@test.local");

            Authenticate(client, token);

            // Created through the normal API: kind task, category Testing, amount 0, no date.
            using var created = await CreateItemAsync(
                client, "Buy milk", Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var result = await RestoreAsync(client, new[]
            {
                // Same kind/title/category/amount/date as the existing record: skipped.
                BackupItem("task", "Buy milk"),
                // Same title but a different category: a genuinely different record.
                BackupItem("task", "Buy milk", "Errands"),
                BackupItem("task", "Call the dentist"),
                BackupItem("event", "Team standup", "Work", 0, "2030-01-15")
            });

            Assert.Equal(3, result.Imported);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(4, result.Total);

            using var items = await GetItemsAsync(client);

            Assert.Equal(4, items.RootElement.GetArrayLength());

            Assert.Equal(
                1,
                items.RootElement.EnumerateArray().Count(x =>
                    x.GetProperty("title").GetString() == "Buy milk" &&
                    x.GetProperty("category").GetString() == "Testing"));
        });
    }

    [Fact]
    public async Task RestorePreservesIdenticalRowsWithinOneBackup()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-multiplicity@test.local");

            Authenticate(client, token);

            var twoIdenticalRows = new[]
            {
                BackupItem("grocery", "Eggs", "Dairy"),
                BackupItem("grocery", "Eggs", "Dairy")
            };

            // Two identical rows in the backup mean the account held two such records.
            var first = await RestoreAsync(client, twoIdenticalRows);

            Assert.Equal(2, first.Imported);
            Assert.Equal(0, first.Skipped);

            // Importing the same backup again adds nothing.
            var second = await RestoreAsync(client, twoIdenticalRows);

            Assert.Equal(0, second.Imported);
            Assert.Equal(2, second.Skipped);

            Assert.Equal(2, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task RestoreRejectsInvalidBackupWithoutImportingAnything()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-invalid@test.local");

            Authenticate(client, token);

            // One valid item followed by one with an unsupported kind.
            using var invalidKind = await client.PostAsJsonAsync(
                "/api/restore",
                new[]
                {
                    BackupItem("task", "Valid task"),
                    BackupItem("note", "Unsupported kind")
                });

            Assert.Equal(HttpStatusCode.BadRequest, invalidKind.StatusCode);

            Assert.Contains(
                "index 1",
                await invalidKind.Content.ReadAsStringAsync());

            // A null entry inside the array.
            using var nullEntry = await RestoreRawAsync(
                client,
                "[{\"kind\":\"task\",\"title\":\"Valid task\"}, null]");

            Assert.Equal(HttpStatusCode.BadRequest, nullEntry.StatusCode);

            // Title too long and an unparseable date.
            using var badFields = await client.PostAsJsonAsync(
                "/api/restore",
                new[]
                {
                    BackupItem("task", new string('x', 201)),
                    BackupItem("event", "Bad date", "Work", 0, "not-a-date")
                });

            Assert.Equal(HttpStatusCode.BadRequest, badFields.StatusCode);

            // Not an array at all.
            using var notArray = await RestoreRawAsync(client, "{}");

            Assert.Equal(HttpStatusCode.BadRequest, notArray.StatusCode);

            // JSON null body.
            using var nullBody = await RestoreRawAsync(client, "null");

            Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);

            // Over the size limit.
            using var tooLarge = await client.PostAsJsonAsync(
                "/api/restore",
                Enumerable.Range(0, 5001)
                    .Select(i => BackupItem("task", $"Task {i}"))
                    .ToArray());

            Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);

            // None of the rejected payloads may have written anything,
            // including the valid items that preceded the invalid ones.
            Assert.Equal(0, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task RestoreKeepsRecordsScopedToAuthenticatedUser()
    {
        await RunIsolatedAsync(async client =>
        {
            var userAToken = await RegisterAndLoginAsync(
                client, "restore-usera@test.local");

            var userBToken = await RegisterAndLoginAsync(
                client, "restore-userb@test.local");

            Authenticate(client, userAToken);

            var userAId = await GetUserIdAsync(client);

            var userAResult = await RestoreAsync(client, SampleBackup());

            Assert.Equal(3, userAResult.Imported);

            Authenticate(client, userBToken);

            var userBId = await GetUserIdAsync(client);

            Assert.NotEqual(userAId, userBId);

            // User B restores an identical backup. User A's records must not count as
            // "existing" for B, and ownership fields in the payload must be ignored.
            var userBResult = await RestoreAsync(client, new[]
            {
                new { kind = "task", title = "Call the dentist", category = "Testing", amount = 0m, date = (string?)null, id = 1, userId = userAId },
                new { kind = "grocery", title = "Milk", category = "Dairy", amount = 2.49m, date = (string?)null, id = 2, userId = userAId },
                new { kind = "event", title = "Team standup", category = "Work", amount = 0m, date = (string?)"2030-01-15", id = 3, userId = userAId }
            });

            Assert.Equal(3, userBResult.Imported);
            Assert.Equal(0, userBResult.Skipped);

            using var userBItems = await GetItemsAsync(client);

            Assert.Equal(3, userBItems.RootElement.GetArrayLength());

            Assert.All(
                userBItems.RootElement.EnumerateArray(),
                x => Assert.Equal(userBId, x.GetProperty("userId").GetInt32()));

            Authenticate(client, userAToken);

            using var userAItems = await GetItemsAsync(client);

            Assert.Equal(3, userAItems.RootElement.GetArrayLength());

            Assert.All(
                userAItems.RootElement.EnumerateArray(),
                x => Assert.Equal(userAId, x.GetProperty("userId").GetInt32()));
        });
    }

    [Fact]
    public async Task RestoreRejectsMalformedJsonWithProblemDetails()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-malformed@test.local");

            Authenticate(client, token);

            // Truncated document.
            using var truncated = await RestoreRawAsync(
                client, "[{\"kind\":\"task\",\"title\":\"Unfinished");

            Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);

            // The binder failure must surface as a Problem Details body in every environment,
            // not as an empty response.
            var truncatedBody = await truncated.Content.ReadAsStringAsync();

            Assert.Contains("\"title\"", truncatedBody);
            Assert.Contains("\"status\":400", truncatedBody);

            // Not JSON at all.
            using var text = await RestoreRawAsync(client, "this is not json");

            Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

            // Whitespace only.
            using var whitespace = await RestoreRawAsync(client, "   ");

            Assert.Equal(HttpStatusCode.BadRequest, whitespace.StatusCode);

            // Valid JSON but elements of the wrong type.
            using var wrongElementType = await RestoreRawAsync(client, "[1, 2, 3]");

            Assert.Equal(HttpStatusCode.BadRequest, wrongElementType.StatusCode);

            // Valid JSON but a field of the wrong type.
            using var wrongFieldType = await RestoreRawAsync(
                client, "[{\"kind\":\"task\",\"title\":\"Task\",\"amount\":\"lots\"}]");

            Assert.Equal(HttpStatusCode.BadRequest, wrongFieldType.StatusCode);

            // Wrong content type.
            using var notJsonContentType = await client.PostAsync(
                "/api/restore",
                new StringContent("[]", Encoding.UTF8, "text/plain"));

            Assert.Equal(
                HttpStatusCode.UnsupportedMediaType,
                notJsonContentType.StatusCode);

            Assert.Equal(0, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task RestoreOfEmptyBackupIsNoOp()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-empty@test.local");

            Authenticate(client, token);

            // A brand-new account exports an empty array.
            using var emptyBackup = await client.GetAsync("/api/backup");

            Assert.Equal(HttpStatusCode.OK, emptyBackup.StatusCode);
            Assert.Equal("[]", (await emptyBackup.Content.ReadAsStringAsync()).Trim());

            using var created = await CreateItemAsync(
                client, "Keep me", Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            // Restoring an empty array succeeds, reports zeros and leaves existing data alone.
            var result = await RestoreAsync(client, Array.Empty<object>());

            Assert.Equal(0, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(0, result.Total);

            Assert.Equal(1, await CountItemsAsync(client));

            // An empty request body is not an empty backup; it is rejected.
            using var emptyBody = await RestoreRawAsync(client, "");

            Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);

            Assert.Equal(1, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task RestoreAcceptsBackupAtMaximumSizeAndRejectsOneMore()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-max@test.local");

            Authenticate(client, token);

            const int maximum = 5000;

            object[] Build(int count) => Enumerable.Range(0, count)
                .Select(i => BackupItem("task", $"Bulk task {i}"))
                .ToArray();

            // Exactly the limit is accepted and fully imported.
            var atLimit = await RestoreAsync(client, Build(maximum));

            Assert.Equal(maximum, atLimit.Imported);
            Assert.Equal(0, atLimit.Skipped);
            Assert.Equal(maximum, atLimit.Total);

            Assert.Equal(maximum, await CountItemsAsync(client));

            // One more is rejected before anything is written, even though the first 5000 entries already exist.
            using var overLimit = await client.PostAsJsonAsync(
                "/api/restore", Build(maximum + 1));

            Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);

            Assert.Contains(
                "maximum 5000",
                await overLimit.Content.ReadAsStringAsync());

            Assert.Equal(maximum, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task ConcurrentRestoresOfSameBackupDoNotCreateDuplicates()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "restore-concurrent@test.local");

            Authenticate(client, token);

            const int itemCount = 200;
            const int concurrentRequests = 5;

            var json = JsonSerializer.Serialize(
                Enumerable.Range(0, itemCount)
                    .Select(i => BackupItem("task", $"Concurrent task {i}"))
                    .ToArray());

            // Start every request before awaiting any of them so they overlap. Each request gets its own
            // DbContext and SQLite connection; the endpoint's BEGIN IMMEDIATE transaction must serialize the
            // read-check-insert section so exactly one request imports and the others skip.
            var pending = Enumerable.Range(0, concurrentRequests)
                .Select(_ => RestoreRawAsync(client, json))
                .ToArray();

            var responses = await Task.WhenAll(pending);

            var totalImported = 0;
            var totalSkipped = 0;

            foreach (var response in responses)
            {
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync();

                    Assert.True(
                        response.StatusCode == HttpStatusCode.OK,
                        $"Expected 200 OK from a concurrent restore but got {(int)response.StatusCode}: {body}");

                    var result = JsonSerializer.Deserialize<RestoreResult>(
                        body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));

                    Assert.NotNull(result);
                    Assert.Equal(itemCount, result.Total);

                    totalImported += result.Imported;
                    totalSkipped += result.Skipped;
                }
            }

            // Every record is imported exactly once across all requests, and the rest are reported as skipped.
            Assert.Equal(itemCount, totalImported);
            Assert.Equal(itemCount * (concurrentRequests - 1), totalSkipped);

            Assert.Equal(itemCount, await CountItemsAsync(client));
        });
    }

    [Fact]
    public async Task BackupExportsOnlyPortableItemFields()
    {
        await RunIsolatedAsync(async client =>
        {
            var token = await RegisterAndLoginAsync(
                client, "backup-shape@test.local");

            Authenticate(client, token);

            using var created = await CreateItemAsync(
                client, "Exported task", Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var response = await client.GetAsync("/api/backup");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
            Assert.Equal(1, json.RootElement.GetArrayLength());

            var item = json.RootElement[0];

            var properties = item.EnumerateObject()
                .Select(x => x.Name)
                .OrderBy(x => x)
                .ToArray();

            Assert.Equal(
                new[] { "amount", "category", "date", "kind", "title" },
                properties);

            Assert.Equal("Exported task", item.GetProperty("title").GetString());
        });
    }
}
