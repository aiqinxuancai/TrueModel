using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrueModel;

namespace TrueModel.Tests;

public class DetectionDeletionTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("key")]
    [InlineData("site")]
    public async Task RemovingLastTargetCancelsBatchAndPreservesHistory(string kind)
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var site = new Site { Keys = [new SiteKey { Models = [new MonitoredModel { Name = "model" }] }] };
        db.Sites.Add(site); await db.SaveChangesAsync();
        var key = site.Keys[0]; var model = key.Models[0];
        var snapshot = JsonSerializer.Serialize(new[] { new Target(model.Id, model.Name, "key", "site", "https://test", "protected") });
        var queued = new DetectionRun { Status = "Queued", Total = 1, TargetsJson = snapshot };
        var active = new DetectionRun { Status = "Running", Total = 1, TargetsJson = snapshot };
        var completed = new DetectionRun { Status = "Completed", Total = 1, Completed = 1, TargetsJson = snapshot };
        db.Runs.AddRange(queued, active, completed); await db.SaveChangesAsync();
        db.Results.Add(new DetectionResult { RunId = completed.Id, ModelId = model.Id, Status = "Success" }); await db.SaveChangesAsync();
        var control = new DetectionControl(); using var cancellation = new CancellationTokenSource();
        control.Active.Add((active.Id, model.Id), cancellation);
        await control.Delete(db, siteId: kind == "site" ? site.Id : null, keyId: kind == "key" ? key.Id : null, modelId: kind == "model" ? model.Id : null);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(await db.Models.AnyAsync());
        foreach (var run in new[] { queued, active })
        {
            Assert.Equal("Cancelled", run.Status); Assert.Equal(0, run.Total);
            Assert.Equal("[]", run.TargetsJson); Assert.NotNull(run.CompletedAt);
        }
        Assert.Equal(snapshot, completed.TargetsJson);
        Assert.Equal(1, await db.Results.CountAsync());
    }

    [Fact]
    public async Task DeletionCancelsActiveRequestAndRemovesQueuedTargetsWithoutStoppingOtherModels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-delete-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var handler = new BlockingHandler();
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
        async Task<int> Create(string path, object body)
        {
            var response = await client.PostAsJsonAsync(path, body); response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        await Token(); await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" }); await Token();
        (await client.PutAsJsonAsync("/api/settings", new { challengeCount = 3, intervalMinutes = 0, maxConcurrency = 1, timeoutSeconds = 240 })).EnsureSuccessStatusCode();
        var site = await Create("/api/sites", new { name = "site", baseUrl = "https://mock.test" });
        var key = await Create($"/api/sites/{site}/keys", new { name = "key", value = "secret" });
        var deleted = await Create($"/api/keys/{key}/models", new { name = "blocked" });
        var kept = await Create($"/api/keys/{key}/models", new { name = "kept" });
        var active = await Create("/api/detect", new { });
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queuedKept = await Create($"/api/keys/{key}/models", new { name = "queued-kept" });
        var queued = await Create("/api/detect", new { });
        var queuedDeleted = await Create($"/api/keys/{key}/models", new { name = "queued-deleted" });
        var empty = await Create("/api/detect", new { modelId = queuedDeleted });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/models/{queuedDeleted}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/models/{deleted}")).StatusCode);
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement runs;
        do
        {
            runs = await client.GetFromJsonAsync<JsonElement>("/api/runs");
            if (runs.EnumerateArray().All(r => r.GetProperty("status").GetString() is "Completed" or "Cancelled")) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        foreach (var id in new[] { active, queued })
        {
            var run = runs.EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == id);
            Assert.Equal("Completed", run.GetProperty("status").GetString());
            Assert.Equal(1, run.GetProperty("total").GetInt32());
            Assert.Equal(1, run.GetProperty("completed").GetInt32());
            Assert.Equal(id == active ? kept : queuedKept, run.GetProperty("targets")[0].GetProperty("modelId").GetInt32());
        }
        var removedRun = runs.EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == empty);
        Assert.Equal("Cancelled", removedRun.GetProperty("status").GetString());
        Assert.Equal(0, removedRun.GetProperty("total").GetInt32());
        Assert.Equal(1, handler.BlockedRequests);
        var results = await client.GetFromJsonAsync<JsonElement>("/api/results");
        Assert.All(results.EnumerateArray(), r => Assert.Contains(r.GetProperty("modelId").GetInt32(), new[] { kept, queuedKept }));
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BlockedRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            if (body.GetProperty("model").GetString() == "blocked")
            {
                Interlocked.Increment(ref BlockedRequests); Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { message = "fixture response" } }) };
        }
    }
}
