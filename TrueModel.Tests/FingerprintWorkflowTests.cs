using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class FingerprintWorkflowTests
{
    [Fact]
    public async Task StopsQueuedAndRunningCollectionAndMatchesPublishedBanksWithTheDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-fingerprints-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var handler = new ProbeHandler();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        await Token();
        using (var login = await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" }))
            login.EnsureSuccessStatusCode();
        await Token();
        using (var settings = await client.PutAsJsonAsync("/api/settings", new { maxConcurrency = 1 }))
            settings.EnsureSuccessStatusCode();

        var site = await Post("/api/sites", new { name = "source", baseUrl = "https://mock.test" });
        var key = await Post($"/api/sites/{site}/keys", new { name = "key", value = "credential" });
        var slow = await Post($"/api/keys/{key}/models", new { name = "slow" });
        var usable = await Post($"/api/keys/{key}/models", new { name = "usable" });
        var detectionBlocker = await Post($"/api/keys/{key}/models", new { name = "hold-detection" });
        var defaultId = (await client.GetFromJsonAsync<JsonElement>("/api/banks"))[0].GetProperty("id").GetInt32();
        var defaultExport = await client.GetStringAsync($"/api/banks/{defaultId}/export");
        using var original = JsonDocument.Parse(defaultExport);
        var defaultDisplayName = original.RootElement.GetProperty("models").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "gpt-6-sol").GetProperty("display_name").GetString();
        var templateId = await Post("/api/banks/empty", new { name = "temporary template" });

        var running = await Enroll(slow, defaultId, "cancelled-running");
        await handler.EnrollmentBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (await GetTask(running)).GetProperty("collected").GetInt32());
        var queued = await Enroll(usable, defaultId, "cancelled-queued", count: 17);
        Assert.Equal("Queued", (await GetTask(queued)).GetProperty("status").GetString());
        Assert.Equal(17, (await GetTask(queued)).GetProperty("total").GetInt32());
        await Cancel(queued);
        var published = await Post($"/api/models/{usable}/fingerprint",
            new { bankId = templateId, model = "gpt-6-sol", displayName = "Local GPT", family = "custom" }, HttpStatusCode.Accepted);
        (await client.DeleteAsync($"/api/banks/{templateId}")).EnsureSuccessStatusCode();
        await Cancel(running);
        await handler.EnrollmentCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Cancelled", (await GetTask(queued)).GetProperty("status").GetString());
        Assert.Equal("Cancelled", (await GetTask(running)).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetTask(running)).GetProperty("resultBankId").ValueKind);

        var task = await WaitFor(() => GetTask(published), t => t.GetProperty("status").GetString() == "Completed");
        Assert.Equal(10, task.GetProperty("total").GetInt32());
        Assert.Equal(10, task.GetProperty("collected").GetInt32());
        var collectedBank = task.GetProperty("resultBankId").GetInt32();
        var banks = await client.GetFromJsonAsync<JsonElement>("/api/banks");
        Assert.Equal(2, banks.GetArrayLength());
        Assert.True(banks[0].GetProperty("isDefault").GetBoolean());
        Assert.True(banks[1].GetProperty("active").GetBoolean());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/banks/{collectedBank}");
        var fingerprint = Assert.Single(detail.GetProperty("models").EnumerateArray());
        Assert.Equal("gpt-6-sol", fingerprint.GetProperty("id").GetString());
        Assert.Equal(10, fingerprint.GetProperty("responseCount").GetInt32());

        var extra = await Post("/api/banks/empty", new { name = "second custom bank" });
        var counts = Enumerable.Repeat(1, 355).ToArray();
        await Post($"/api/banks/{extra}/models", new { model = "second-fingerprint", displayName = "Second", family = "custom", counts });
        (await client.PostAsJsonAsync($"/api/banks/{extra}/activate", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/banks/{defaultId}/deactivate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/banks/{defaultId}/models",
            new { model = "forbidden", displayName = "Forbidden", family = "custom", counts })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/banks/{defaultId}")).StatusCode);

        var blockedRun = await Post("/api/detect", new { modelId = detectionBlocker }, HttpStatusCode.Accepted);
        await handler.DetectionBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var run = await Post("/api/detect", new { modelId = usable }, HttpStatusCode.Accepted);
        var runs = await client.GetFromJsonAsync<JsonElement>("/api/runs");
        var entry = runs.EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == run);
        Assert.Equal("Queued", entry.GetProperty("status").GetString());
        Assert.Equal(defaultId, entry.GetProperty("bankId").GetInt32());
        Assert.Equal(new[] { extra, collectedBank, defaultId }, entry.GetProperty("bankIds").EnumerateArray().Select(i => i.GetInt32()));
        (await client.DeleteAsync($"/api/banks/{extra}")).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/banks/{collectedBank}/deactivate", new { })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/runs/{blockedRun}/cancel", new { })).EnsureSuccessStatusCode();

        var candidates = await Candidates(run);
        Assert.Equal("Local GPT", candidates["gpt-6-sol"]);
        Assert.Contains("second-fingerprint", candidates.Keys);
        Assert.Contains("claude-opus-5-5", candidates.Keys);
        var next = await Post("/api/detect", new { modelId = usable }, HttpStatusCode.Accepted);
        var nextCandidates = await Candidates(next);
        Assert.Equal(defaultDisplayName, nextCandidates["gpt-6-sol"]);
        Assert.DoesNotContain("second-fingerprint", nextCandidates.Keys);

        var stoppedAppend = await Enroll(slow, collectedBank, "gpt-6-sol", accumulate: true);
        await handler.AppendBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstAppend = await Enroll(usable, collectedBank, "gpt-6-sol", accumulate: true);
        var secondAppend = await Enroll(usable, collectedBank, "gpt-6-sol", count: 4, accumulate: true);
        Assert.Equal("Queued", (await GetTask(secondAppend)).GetProperty("status").GetString());
        var beforeAppend = await client.GetFromJsonAsync<JsonElement>($"/api/banks/{collectedBank}");
        Assert.Equal(detail.GetProperty("sha256").GetString(), beforeAppend.GetProperty("sha256").GetString());
        await Cancel(stoppedAppend);
        foreach (var appendId in new[] { firstAppend, secondAppend })
        {
            var appended = await WaitFor(() => GetTask(appendId), t => t.GetProperty("status").GetString() == "Completed");
            Assert.Equal(collectedBank, appended.GetProperty("destinationBankId").GetInt32());
            Assert.Equal(collectedBank, appended.GetProperty("resultBankId").GetInt32());
        }
        var afterAppend = await client.GetFromJsonAsync<JsonElement>($"/api/banks/{collectedBank}");
        var updated = Assert.Single(afterAppend.GetProperty("models").EnumerateArray());
        Assert.Equal(17, updated.GetProperty("responseCount").GetInt32());
        Assert.Equal(17 * 310, updated.GetProperty("validNumberCount").GetInt32());
        Assert.Equal("Local GPT", updated.GetProperty("displayName").GetString());
        Assert.False(afterAppend.GetProperty("active").GetBoolean());
        Assert.NotEqual(beforeAppend.GetProperty("sha256").GetString(), afterAppend.GetProperty("sha256").GetString());

        var added = await Enroll(usable, collectedBank, "new-fingerprint", accumulate: true);
        Assert.Equal(collectedBank, (await WaitFor(() => GetTask(added), t => t.GetProperty("status").GetString() == "Completed"))
            .GetProperty("resultBankId").GetInt32());
        var withNewModel = await client.GetFromJsonAsync<JsonElement>($"/api/banks/{collectedBank}");
        Assert.Equal(new[] { "gpt-6-sol", "new-fingerprint" }, withNewModel.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/banks")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/models/{usable}/fingerprint",
            new { bankId = defaultId, model = "gpt-6-sol", displayName = "Local GPT", family = "custom", accumulate = true })).StatusCode);
        Assert.Equal(defaultExport, await client.GetStringAsync($"/api/banks/{defaultId}/export"));

        async Task Token()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        async Task<int> Post(string url, object input, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsJsonAsync(url, input);
            Assert.Equal(expected, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        Task<int> Enroll(int model, int bankId, string name, int count = 3, bool accumulate = false) => Post($"/api/models/{model}/fingerprint",
            new { bankId, model = name, displayName = name, family = "custom", challengeCount = count, accumulate }, HttpStatusCode.Accepted);
        Task<JsonElement> GetTask(int id) => client.GetFromJsonAsync<JsonElement>($"/api/fingerprint-tasks/{id}");
        async Task Cancel(int id)
        {
            using var response = await client.PostAsJsonAsync($"/api/fingerprint-tasks/{id}/cancel", new { });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        async Task<Dictionary<string, string>> Candidates(int runId)
        {
            var results = await WaitFor(() => client.GetFromJsonAsync<JsonElement>($"/api/results?runId={runId}"), rows => rows.GetArrayLength() > 0);
            var result = results[0];
            Assert.Equal("Success", result.GetProperty("status").GetString());
            using var report = JsonDocument.Parse(result.GetProperty("attributionJson").GetString()!);
            return report.RootElement.GetProperty("Candidates").EnumerateArray()
                .ToDictionary(c => c.GetProperty("Model").GetString()!, c => c.GetProperty("DisplayName").GetString()!);
        }
    }

    private static async Task<JsonElement> WaitFor(Func<Task<JsonElement>> get, Func<JsonElement, bool> matches)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        JsonElement last;
        do
        {
            last = await get();
            if (matches(last)) return last;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Unexpected task state: {last}");
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        private int slowRequests;
        public TaskCompletionSource EnrollmentBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnrollmentCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AppendBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DetectionBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            var model = body.GetProperty("model").GetString();
            if (model == "slow")
            {
                var count = Interlocked.Increment(ref slowRequests);
                if (count is 2 or 4)
                {
                    (count == 2 ? EnrollmentBlocked : AppendBlocked).TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { EnrollmentCancelled.TrySetResult(); throw; }
                }
            }
            if (model == "hold-detection")
            {
                DetectionBlocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            var text = CandyTestPrompts.IsCandy(TestConversation.Prompt(body)) ? "21" : string.Join(',', Enumerable.Range(1, 310));
            return TestConversation.ResponsesOk(text);
        }
    }
}
