using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrueModel;

namespace TrueModel.Tests;

public class HlwyTests
{
    [Fact]
    public void AntiTargetInsertsExactlyOneInternalSpaceWithoutSplittingNumbers()
    {
        Assert.Equal(HlwyFingerprint.Prompt, HlwyFingerprint.BuildPrompt(false));
        for (var i = 0; i < 500; i++)
        {
            var prompt = HlwyFingerprint.BuildPrompt(true);
            Assert.Equal(HlwyFingerprint.Prompt, prompt.Replace(" ", ""));
            Assert.Equal(HlwyFingerprint.Prompt.Length + 1, prompt.Length);
            var pos = prompt.IndexOf(' ');
            Assert.InRange(pos, 1, prompt.Length - 2);
            Assert.False(char.IsAsciiDigit(prompt[pos - 1]) && char.IsAsciiDigit(prompt[pos + 1]));
        }
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("数字：355。", 355)]
    [InlineData("356 或 42", null)]
    [InlineData("0", null)]
    [InlineData("拒绝", null)]
    public void UsesUpstreamFirstNumberParsing(string text, int? expected) => Assert.Equal(expected, HlwyFingerprint.Parse(text));

    [Fact]
    public void ScoresHaveUpstreamSemanticsAndExportRoundTrips()
    {
        var baselines = HlwyFingerprint.Baselines("");
        Assert.Equal(7, baselines.Length);
        var dist = new double[355]; dist[41] = 1;
        Assert.Equal(1, HlwyFingerprint.Similarity(dist, dist, 42, 42).Score, 12);
        var other = new double[355]; other[141] = 1;
        var mismatch = HlwyFingerprint.Similarity(dist, other, 42, 142);
        Assert.Equal(0, mismatch.Score);
        Assert.Equal(Math.Log(2), mismatch.Js, 9);
        // Nontrivial fixture generated with the upstream JS calculateSimilarity.
        other = new double[355]; other[41] = .3; other[42] = .7;
        dist[41] = .8; dist[42] = .2;
        var mixed = HlwyFingerprint.Similarity(dist, other, 42, 43);
        Assert.Equal(0.7549956602323071, mixed.Score, 12);
        var report = JsonSerializer.SerializeToElement(HlwyFingerprint.Analyze(Enumerable.Repeat(42, 40).ToArray(), baselines, "trusted", 50, true));
        var exported = report.GetProperty("Baseline").GetRawText();
        var reimported = HlwyFingerprint.Baselines(exported);
        var match = JsonSerializer.SerializeToElement(HlwyFingerprint.Analyze(Enumerable.Repeat(42, 40).ToArray(), reimported, "target", 40, false));
        Assert.Equal(1, match.GetProperty("Candidates")[0].GetProperty("Similarity").GetDouble(), 12);
        Assert.False(match.GetProperty("Candidates")[0].TryGetProperty("Probability", out _));
        Assert.Throws<InvalidOperationException>(() => HlwyFingerprint.Analyze(new int[39], baselines, "target", 40, false));
        Assert.Throws<InvalidOperationException>(() => HlwyFingerprint.Baselines("[]"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendsSamplingParametersAndPreservesSupplementalProbes(bool antiTarget)
    {
        var handler = new SamplingHandler();
        var client = new ModelTraceClient(new HttpClient(handler));
        var result = await client.TestHlwy("https://example.test", "secret", "mock", new AppSettings { HlwySampleCount = 40, HlwyAntiTarget = antiTarget }, CancellationToken.None);
        Assert.Equal(40, result.GetProperty("result").GetProperty("UsedOutputs").GetInt32());
        Assert.Equal(antiTarget, result.GetProperty("result").GetProperty("AntiTarget").GetBoolean());
        Assert.Equal("Success", result.GetProperty("candy").GetProperty("CandyStatus").GetString());
        Assert.Equal(41, handler.Bodies.Count);
        foreach (var request in handler.Bodies.Take(40))
        {
            Assert.Equal(1, request.GetProperty("temperature").GetDouble());
            Assert.Equal(256, request.GetProperty("max_tokens").GetInt32());
            var prompt = request.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Equal(HlwyFingerprint.Prompt.Length + (antiTarget ? 1 : 0), prompt.Length);
        }
        Assert.False(handler.Bodies[^1].TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task InvalidSamplesStopEarlyAndCancellationStopsRequests()
    {
        var handler = new SamplingHandler { Text = "无法回答" };
        var client = new ModelTraceClient(new HttpClient(handler));
        var result = await client.TestHlwy("https://example.test", "secret", "mock", new AppSettings { HlwyConcurrency = 1 }, CancellationToken.None);
        Assert.Contains("至少需要 40", result.GetProperty("error").GetString());
        Assert.Equal(5, result.GetProperty("responses").GetArrayLength());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TestHlwy("https://example.test", "secret", "mock", new AppSettings(), cancellation.Token));
        Assert.Equal(6, handler.Bodies.Count);
    }

    [Fact]
    public async Task QueueSnapshotsSettingsAndDoesNotRequireModelTraceBank()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var settings = new AppSettings { DetectionMethod = "hlwy", HlwyAntiTarget = true, HlwySampleCount = 40, HlwyConcurrency = 7 };
        db.Settings.Add(settings);
        db.Sites.Add(new Site { Keys = [new SiteKey { Models = [new MonitoredModel { Name = "mock" }] }] });
        await db.SaveChangesAsync();
        var run = await DetectionJobs.Enqueue(db, new(null, null, null), "Manual");
        settings.DetectionMethod = "modeltrace"; settings.HlwyAntiTarget = false;
        await db.SaveChangesAsync();
        var snapshot = JsonSerializer.Deserialize<AppSettings>(run.DetectionSettingsJson!)!;
        Assert.Equal("hlwy", snapshot.DetectionMethod);
        Assert.True(snapshot.HlwyAntiTarget);
        Assert.Equal(7, snapshot.HlwyConcurrency);
        Assert.Equal(7, HlwyFingerprint.Baselines(snapshot.HlwyBaselinesJson).Length);
        Assert.Equal(0, run.BankId);
    }

    private sealed class SamplingHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public string Text { get; init; } = "42";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(body);
            var candy = body.GetProperty("messages")[0].GetProperty("content").GetString() == CandyProbe.Prompt;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = candy ? "21" : Text }, finish_reason = "stop" } } }) };
        }
    }
}
