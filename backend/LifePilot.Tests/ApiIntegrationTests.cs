using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

                    // Avoid running the reminder background worker in these tests.
                    var workers = services
                        .Where(x =>
                            x.ServiceType == typeof(IHostedService) &&
                            x.ImplementationType == typeof(ReminderWorker))
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
}