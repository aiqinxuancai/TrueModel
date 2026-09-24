using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TrueModel.Tests;

public class WebTests
{
    [Fact]
    public async Task SettingsRequireAuthenticationAndPersistChallengeCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-test-" + Guid.NewGuid());
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataDirectory"] = directory,
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "truemodel.db")}",
            ["Admin:Password"] = "test-password-123!",
            ["Desktop:Enabled"] = "false",
            ["Logging:LogLevel:Default"] = "Warning"
        })));
        Directory.CreateDirectory(directory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);
        async Task RefreshToken()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await RefreshToken();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).StatusCode);
        await RefreshToken();
        foreach (var count in new[] { 3, 6 })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings", new { challengeCount = count, intervalMinutes = 0, maxConcurrency = 2, timeoutSeconds = 240 })).StatusCode);
            var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.Equal(count, settings.GetProperty("challengeCount").GetInt32());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", new { challengeCount = 7, intervalMinutes = 0, maxConcurrency = 2, timeoutSeconds = 240 })).StatusCode);
        foreach (var effort in new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh" })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings", new { challengeCount = 3, intervalMinutes = 0, maxConcurrency = 2, timeoutSeconds = 240, candyReasoningEffort = effort })).StatusCode);
            var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.Equal(effort, settings.GetProperty("candyReasoningEffort").GetString());
        }
        foreach (var effort in new string?[] { "invalid", "", null })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", new { challengeCount = 3, intervalMinutes = 0, maxConcurrency = 2, timeoutSeconds = 240, candyReasoningEffort = effort })).StatusCode);
        Assert.True(File.Exists(Path.Combine(directory, "truemodel.db")));
        var notificationInput = new { webhookEnabled = true, webhookUrl = "https://hook.test/secret", pushDeerEnabled = true, pushDeerEndpoint = "https://push.test/message/push", pushKey = "private-pushkey", onlyFailures = true };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/notifications", notificationInput)).StatusCode);
        var notificationJson = await client.GetStringAsync("/api/notifications");
        Assert.DoesNotContain("private-pushkey", notificationJson);
        Assert.DoesNotContain("hook.test/secret", notificationJson);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/notifications", new { webhookEnabled = true, webhookUrl = "", pushDeerEnabled = true, pushDeerEndpoint = "https://push.test/message/push", pushKey = "", onlyFailures = true })).StatusCode);
        using var notifications = JsonDocument.Parse(await client.GetStringAsync("/api/notifications"));
        Assert.True(notifications.RootElement.GetProperty("pushKeyConfigured").GetBoolean());
        Assert.True(notifications.RootElement.GetProperty("webhookConfigured").GetBoolean());
    }
}
