using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class HlwyProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportIsVisibleBeforeSupplementalFinishesAndSurvivesCancellation(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "hlwy-progress-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var handler = new Handler();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var client = factory.CreateClient();
        async Task Token()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        async Task<int> Create(string path, object input)
        {
            var response = await client.PostAsJsonAsync(path, input);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        await Token();
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).EnsureSuccessStatusCode();
        await Token();
        (await client.PutAsJsonAsync("/api/settings", new { detectionMethod = "hlwy", hlwySampleCount = 40, maxConcurrency = 1 })).EnsureSuccessStatusCode();
        var site = await Create("/api/sites", new { name = "test", baseUrl = "https://mock.test" });
        var key = await Create($"/api/sites/{site}/keys", new { name = "key", value = "secret" });
        var model = await Create($"/api/keys/{key}/models", new { name = "gpt-mock" });
        var run = await Create("/api/detect", new { modelId = model });
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/results?runId={run}")).EnumerateArray());
            Assert.Equal("Success", result.GetProperty("status").GetString());
            Assert.Equal("Running", result.GetProperty("candyStatus").GetString());
            Assert.Equal("Running", result.GetProperty("candy2Status").GetString());
            var report = result.GetProperty("attributionJson").GetString();
            Assert.Equal(40, JsonDocument.Parse(report!).RootElement.GetProperty("UsedOutputs").GetInt32());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/detect", new { modelId = model })).StatusCode);
            if (cancel) (await client.PostAsJsonAsync($"/api/runs/{run}/cancel", new { })).EnsureSuccessStatusCode();
            else handler.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            JsonElement final;
            do
            {
                await Task.Delay(25, timeout.Token);
                final = Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/results?runId={run}", timeout.Token)).EnumerateArray());
            } while (final.GetProperty("candyStatus").GetString() == "Running");
            Assert.Equal(result.GetProperty("id").GetInt32(), final.GetProperty("id").GetInt32());
            Assert.Equal("Success", final.GetProperty("status").GetString());
            Assert.Equal(report, final.GetProperty("attributionJson").GetString());
            Assert.Equal(cancel ? "Interrupted" : "Success", final.GetProperty("candyStatus").GetString());
            Assert.Equal(cancel ? "Interrupted" : "Success", final.GetProperty("candy2Status").GetString());
            Assert.Equal("Success", final.GetProperty("juiceStatus").GetString());
        }
        finally { handler.Release.TrySetResult(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            var candy = CandyTestPrompts.IsCandy(body.GetProperty("messages")[0].GetProperty("content").GetString());
            if (candy) { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = candy ? "21" : "42" }, finish_reason = "stop" } } }) };
        }
    }
}
