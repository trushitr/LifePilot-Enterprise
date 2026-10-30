using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ApiIntegrationTests {
    [Fact] public async Task HealthIsAvailableAndItemsRequireAuthentication() {
        // Uses the same local configuration as the app; a test-only secret is safe.
        Environment.SetEnvironmentVariable("LIFEPILOT_JWT_KEY", "test-only-32-byte-secret-0123456789abcdef");
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();
        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var privateData = await client.GetAsync("/api/items");
        Assert.Equal(HttpStatusCode.Unauthorized, privateData.StatusCode);
    }
}
