using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>A controllable clock. The API resolves "now" through <see cref="TimeProvider"/>, so tests can move time forward.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
    public void Set(string iso) => Now = DateTimeOffset.Parse(iso, global::System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Shared helpers for API tests: isolated SQLite database per test, background workers disabled, optional fake clock.</summary>
public static class TestApp
{
    public const string Password = "TestPassword123!";

    public static WebApplicationFactory<Program> Create(string databasePath, TimeProvider? clock = null)
    {
        // This is a test-only key, never a production credential.
        Environment.SetEnvironmentVariable("LIFEPILOT_JWT_KEY", "test-only-32-byte-secret-0123456789abcdef");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(x => x.ServiceType == typeof(DbContextOptions<LifeDb>) || x.ServiceType == typeof(DbContextOptions)).ToList())
                    services.Remove(descriptor);
                services.AddDbContext<LifeDb>(options => options.UseSqlite($"Data Source={databasePath};Pooling=False"));
                // Background workers are driven explicitly in tests (see RunSchedulerAsync) so results are deterministic.
                foreach (var worker in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                             (x.ImplementationType == typeof(ReminderWorker) || x.ImplementationType == typeof(RecurringTaskWorker))).ToList())
                    services.Remove(worker);
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
            });
        });
    }

    public static string NewDatabasePath() => Path.Combine(Path.GetTempPath(), $"lifepilot-test-{Guid.NewGuid():N}.db");

    public static async Task RunIsolatedAsync(Func<WebApplicationFactory<Program>, HttpClient, Task> test, TimeProvider? clock = null)
    {
        var path = NewDatabasePath();
        try
        {
            using var app = Create(path, clock);
            using var client = app.CreateClient();
            await test(app, client);
        }
        finally { await DeleteDatabaseAsync(path); }
    }

    public static async Task DeleteDatabaseAsync(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            if (!File.Exists(file)) continue;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { File.Delete(file); break; }
                catch (IOException) when (attempt < 4) { await Task.Delay(200); }
            }
        }
    }

    /// <summary>Runs exactly one scheduler pass, the same unit of work the background worker performs every 15 seconds.</summary>
    public static async Task<SchedulerPassResult> RunSchedulerAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RecurringTaskScheduler>().RunOnceAsync(CancellationToken.None);
    }

    public static async Task<string> RegisterAndLoginAsync(HttpClient client, string email)
    {
        var credentials = new { email, password = Password };
        var registration = await client.PostAsJsonAsync("/api/auth/register", credentials);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    public static void Authenticate(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }
}
