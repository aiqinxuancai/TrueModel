using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TrueModel;

namespace TrueModel.Tests;

public class HlwyBaselineUpdaterTests
{
    [Fact]
    public async Task ImportsIndexFilesCachesDefaultsAndPreservesCustomBaselines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hlwy-update-" + Guid.NewGuid());
        try
        {
            var handler = new Handler();
            using var client = new HttpClient(handler);
            using var updater = new HlwyBaselineUpdater(client, Path.Combine(directory, "defaults.json"), NullLogger<HlwyBaselineUpdater>.Instance);
            Assert.NotEmpty(updater.Baselines(""));
            await updater.RefreshAsync(CancellationToken.None);
            Assert.Equal(new[] { "first", "second.v1" }, updater.Baselines("").Select(b => b.GetProperty("name").GetString()));
            Assert.Equal("custom", Assert.Single(updater.Baselines(Baseline("custom"))).GetProperty("name").GetString());
            using var restored = new HlwyBaselineUpdater(client, Path.Combine(directory, "defaults.json"), NullLogger<HlwyBaselineUpdater>.Instance);
            restored.LoadCache();
            Assert.Equal(2, restored.Baselines("").Length);
            var snapshot = JsonSerializer.Serialize(updater.Baselines(""));
            await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite("Data Source=:memory:").Options);
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
            db.Settings.Add(new AppSettings { DetectionMethod = "hlwy" });
            db.Sites.Add(new Site { Keys = [new SiteKey { Models = [new MonitoredModel { Name = "mock" }] }] });
            await db.SaveChangesAsync();
            var run = await DetectionJobs.Enqueue(db, new(null, null, null), "Manual", updater);
            Assert.Equal(snapshot, JsonSerializer.Deserialize<AppSettings>(run.DetectionSettingsJson!)!.HlwyBaselinesJson);
            Assert.Equal("", (await db.Settings.SingleAsync()).HlwyBaselinesJson);
            var cache = await File.ReadAllTextAsync(Path.Combine(directory, "defaults.json"));
            foreach (var failure in new[] { "invalid-file", "offline", "invalid-index", "empty-index" })
            {
                handler.Failure = failure;
                await Assert.ThrowsAnyAsync<Exception>(() => updater.RefreshAsync(CancellationToken.None));
                Assert.Equal(snapshot, JsonSerializer.Serialize(updater.Baselines("")));
                Assert.Equal(cache, await File.ReadAllTextAsync(Path.Combine(directory, "defaults.json")));
            }
            Assert.DoesNotContain(handler.Paths, p => p.Contains(".."));
            await File.WriteAllTextAsync(Path.Combine(directory, "defaults.json"), "broken");
            using var corrupt = new HlwyBaselineUpdater(client, Path.Combine(directory, "defaults.json"), NullLogger<HlwyBaselineUpdater>.Instance);
            corrupt.LoadCache();
            Assert.NotEmpty(corrupt.Baselines(""));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string Baseline(string name) => JsonSerializer.Serialize(new
    {
        name, distribution = Enumerable.Repeat(1d / 355, 355), stats = new { mode = 1 }
    });

    private sealed class Handler : HttpMessageHandler
    {
        public string? Failure { get; set; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Paths.Add(uri);
            Assert.StartsWith(HlwyBaselineUpdater.Source, uri);
            if (Failure == "offline") throw new HttpRequestException("offline");
            var file = uri[HlwyBaselineUpdater.Source.Length..];
            var body = file switch
            {
                "index.json" => Failure == "invalid-index" ? "[\"../escape\"]" : Failure == "empty-index" ? "[]" : "[\"first\",\"second.v1\"]",
                "first.json" => Baseline("first"),
                "second.v1.json" => Failure == "invalid-file" ? "{}" : "[" + Baseline("second.v1") + "]",
                _ => throw new InvalidOperationException(file)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
