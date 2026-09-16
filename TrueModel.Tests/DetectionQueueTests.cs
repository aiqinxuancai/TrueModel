using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TrueModel;

namespace TrueModel.Tests;

public class DetectionQueueTests
{
    [Fact]
    public async Task CompletedTargetsCanRepeatAndSameNameUnderAnotherKeyIsIndependent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var site = new Site { Keys = [new SiteKey { Models = [new MonitoredModel { Name = "same" }, new MonitoredModel { Name = "pending" }] },
            new SiteKey { Models = [new MonitoredModel { Name = "same" }] }] };
        db.Sites.Add(site);
        db.Banks.Add(new FingerprintBank { Active = true });
        await db.SaveChangesAsync();
        var first = site.Keys[0].Models[0];
        var run = await DetectionJobs.Enqueue(db, new(null, site.Keys[0].Id, null), "Manual");
        run.Status = "Running";
        run.Completed = 1;
        db.Results.Add(new DetectionResult { RunId = run.Id, ModelId = first.Id, Status = "Success" });
        await db.SaveChangesAsync();
        var next = await DetectionJobs.Enqueue(db, new(site.Id, null, null), "Manual");
        var targets = JsonSerializer.Deserialize<Target[]>(next.TargetsJson)!;
        Assert.Equal(2, targets.Length);
        Assert.Contains(targets, t => t.ModelId == first.Id);
        Assert.Contains(targets, t => t.ModelId == site.Keys[1].Models[0].Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DetectionJobs.Enqueue(db, new(site.Id, null, null), "Scheduled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectionsShareSlotsAcrossRunsAndCancellationSkipsPendingTargets(bool batch)
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-queue-" + Guid.NewGuid());
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
        async Task<JsonElement> Run(int id)
        {
            var runs = await client.GetFromJsonAsync<JsonElement>("/api/runs");
            return runs.EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == id);
        }
        await Token();
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).EnsureSuccessStatusCode();
        await Token();
        (await client.PutAsJsonAsync("/api/settings", new { challengeCount = 3, intervalMinutes = 0, maxConcurrency = 2, timeoutSeconds = 240 })).EnsureSuccessStatusCode();
        var site = await Create("/api/sites", new { name = "site", baseUrl = "https://mock.test" });
        var key = await Create($"/api/sites/{site}/keys", new { name = "first", value = "secret" });
        var first = await Create($"/api/keys/{key}/models", new { name = "first" });
        var second = await Create($"/api/keys/{key}/models", new { name = "second" });
        var otherKey = await Create($"/api/sites/{site}/keys", new { name = "other", value = "secret" });
        var third = await Create($"/api/keys/{otherKey}/models", new { name = "third" });
        var fourth = await Create($"/api/keys/{otherKey}/models", new { name = "fourth" });
        var firstRun = await Create("/api/detect", batch ? new { keyId = key } : (object)new { modelId = first });
        await handler.Started("first").WaitAsync(TimeSpan.FromSeconds(10));
        var secondRun = batch ? firstRun : await Create("/api/detect", new { modelId = second });
        await handler.Started("second").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/detect", new { modelId = first })).StatusCode);
        var submissions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/detect", new { modelId = third })));
        Assert.Single(submissions, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(3, submissions.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var thirdRun = (await submissions.Single(r => r.StatusCode == HttpStatusCode.Accepted).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var cancelledRun = await Create("/api/detect", new { siteId = site });
        Assert.Equal(fourth, (await Run(cancelledRun)).GetProperty("targets")[0].GetProperty("modelId").GetInt32());
        Assert.Equal(1, (await Run(cancelledRun)).GetProperty("total").GetInt32());
        (await client.PostAsJsonAsync($"/api/runs/{cancelledRun}/cancel", new { })).EnsureSuccessStatusCode();
        var cancellationDeadline = DateTime.UtcNow.AddSeconds(5);
        while ((await Run(cancelledRun)).GetProperty("status").GetString() != "Cancelled" && DateTime.UtcNow < cancellationDeadline)
            await Task.Delay(100);
        Assert.False(handler.Started("third").IsCompleted);
        Assert.Equal("Queued", (await Run(thirdRun)).GetProperty("status").GetString());
        Assert.Equal("Cancelled", (await Run(cancelledRun)).GetProperty("status").GetString());

        // Free just one slot while the first detection remains blocked.
        handler.Release("second");
        await handler.Started("third").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Running", (await Run(firstRun)).GetProperty("status").GetString());
        Assert.Contains(first, (await Run(firstRun)).GetProperty("activeModelIds").EnumerateArray().Select(v => v.GetInt32()));
        (await client.PostAsJsonAsync($"/api/runs/{firstRun}/cancel", new { })).EnsureSuccessStatusCode();
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Running", (await Run(thirdRun)).GetProperty("status").GetString());
        handler.Release("third");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Run(thirdRun)).GetProperty("status").GetString() == "Completed" &&
                (await Run(firstRun)).GetProperty("status").GetString() == "Cancelled") break;
            await Task.Delay(100);
        }
        Assert.Equal("Completed", (await Run(thirdRun)).GetProperty("status").GetString());
        Assert.Equal("Cancelled", (await Run(firstRun)).GetProperty("status").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.Equal(3, await db.Results.CountAsync());
        Assert.False(await db.Results.AnyAsync(r => r.RunId == cancelledRun));
        Assert.Equal(batch ? 2 : 1, (await Run(secondRun)).GetProperty("completed").GetInt32());
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> releases = new();
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started(string model) => started.GetOrAdd(model, _ => Signal()).Task;
        public void Release(string model) => releases.GetOrAdd(model, _ => Signal()).TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            var model = body.GetProperty("model").GetString()!;
            started.GetOrAdd(model, _ => Signal()).TrySetResult();
            try { await releases.GetOrAdd(model, _ => Signal()).Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { message = "fixture" } }) };
        }
    }
}
