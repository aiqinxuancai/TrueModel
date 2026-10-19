using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class HlwyWebTests
{
    [Fact]
    public async Task WorkerRunsSelectedMethodAndSettingsSurviveRestartAndLegacyUpgrade()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-hlwy-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "test.db");
        WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = $"Data Source={database}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => new Handler()));
        });
        async Task Login(HttpClient client)
        {
            async Task Token()
            {
                var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
                client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
            }
            await Token();
            (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).EnsureSuccessStatusCode();
            await Token();
        }
        await using (var factory = Factory())
        {
            using var client = factory.CreateClient();
            await Login(client);
            (await client.PutAsJsonAsync("/api/settings", new { detectionMethod = "hlwy", hlwyAntiTarget = true, hlwySampleCount = 40 })).EnsureSuccessStatusCode();
            async Task<int> Create(string path, object value)
            {
                var response = await client.PostAsJsonAsync(path, value); response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            }
            var site = await Create("/api/sites", new { name = "Test", baseUrl = "https://example.test" });
            var key = await Create($"/api/sites/{site}/keys", new { name = "Test", value = "test-secret" });
            var model = await Create($"/api/keys/{key}/models", new { name = "mock" });
            var run = await Create("/api/detect", new { modelId = model });
            JsonElement result = default;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (result.ValueKind == JsonValueKind.Undefined)
            {
                var results = await client.GetFromJsonAsync<JsonElement>($"/api/results?runId={run}", timeout.Token);
                if (results.GetArrayLength() > 0) result = results[0];
                else await Task.Delay(50, timeout.Token);
            }
            Assert.Equal("Success", result.GetProperty("status").GetString());
            var report = JsonSerializer.Deserialize<JsonElement>(result.GetProperty("attributionJson").GetString()!);
            Assert.Equal("hlwy", report.GetProperty("Method").GetString());
            Assert.True(report.GetProperty("AntiTarget").GetBoolean());
            Assert.Equal(40, report.GetProperty("UsedOutputs").GetInt32());
        }
        await using (var factory = Factory())
        {
            using var client = factory.CreateClient(); await Login(client);
            var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.Equal("hlwy", settings.GetProperty("detectionMethod").GetString());
            Assert.True(settings.GetProperty("hlwyAntiTarget").GetBoolean());
        }
        // Emulate a database produced by v1.0.19; upgrade must preserve existing data.
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            foreach (var column in new[] { "DetectionMethod", "HlwyAntiTarget", "HlwySampleCount", "HlwyBaselinesJson" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE Settings DROP COLUMN {column}";
                await command.ExecuteNonQueryAsync();
            }
            using var runCommand = connection.CreateCommand();
            runCommand.CommandText = "ALTER TABLE Runs DROP COLUMN DetectionSettingsJson";
            await runCommand.ExecuteNonQueryAsync();
        }
        await using (var factory = Factory())
        {
            using var client = factory.CreateClient(); await Login(client);
            var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.Equal("modeltrace", settings.GetProperty("detectionMethod").GetString());
            Assert.False(settings.GetProperty("hlwyAntiTarget").GetBoolean());
            Assert.Equal(100, settings.GetProperty("hlwySampleCount").GetInt32());
            Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/results")).EnumerateArray());
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "42" }, finish_reason = "stop" } } }) });
    }
}
