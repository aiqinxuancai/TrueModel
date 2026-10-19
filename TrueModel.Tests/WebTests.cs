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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/hlwy/baselines")).StatusCode);
        Assert.False((await client.PostAsJsonAsync("/api/hlwy/baselines/update", new { })).IsSuccessStatusCode);
        async Task RefreshToken()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await RefreshToken();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).StatusCode);
        await RefreshToken();
        var customBaseline = JsonSerializer.Serialize(new { name = "custom-test", distribution = Enumerable.Repeat(1d, 355), stats = new { mode = 42 } });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/hlwy/baselines", new { json = customBaseline })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/hlwy/baselines", new { json = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings", new { challengeCount = 4 })).StatusCode);
        var baselineState = await client.GetFromJsonAsync<JsonElement>("/api/hlwy/baselines");
        Assert.True(baselineState.GetProperty("custom").GetBoolean());
        Assert.Equal(customBaseline, baselineState.GetProperty("customJson").GetString());
        Assert.Equal("custom-test", baselineState.GetProperty("models")[0].GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/hlwy/baselines", new { json = "" })).StatusCode);
        Assert.Equal(4, (await client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("challengeCount").GetInt32());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/hlwy/baselines")).GetProperty("custom").GetBoolean());
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
        foreach (var antiTarget in new[] { true, false })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings", new { detectionMethod = "hlwy", hlwySampleCount = 40, hlwyAntiTarget = antiTarget })).StatusCode);
            var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.Equal("hlwy", settings.GetProperty("detectionMethod").GetString());
            Assert.Equal(40, settings.GetProperty("hlwySampleCount").GetInt32());
            Assert.Equal(antiTarget, settings.GetProperty("hlwyAntiTarget").GetBoolean());
        }
        foreach (var input in new object[] { new { detectionMethod = "invalid" }, new { detectionMethod = "hlwy", hlwySampleCount = 39 }, new { hlwySampleCount = 1001 }, new { hlwyBaselinesJson = "[]" }, new { hlwyBaselinesJson = "{}" }, new { hlwyBaselinesJson = "null" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", input)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings", new { detectionMethod = "modeltrace" })).StatusCode);
        Assert.Equal("modeltrace", (await client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("detectionMethod").GetString());
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
