using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class NativeDetectionTests
{
    [Fact]
    public void ScoresMatchLocalModelTraceForActualResponses()
    {
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/modeltrace-replay.json")));
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var outputs = fixture.GetProperty("outputs").EnumerateArray().Select(o => new ProbeOutput(o.GetProperty("text").GetString()!, o.GetProperty("expected_count").GetInt32())).ToArray();
            var actual = JsonSerializer.SerializeToElement(Attribution.AnalyzeGlobal(outputs, bank));
            var expected = fixture.GetProperty("expected");
            Assert.Equal(expected.GetProperty("prediction").GetString(), actual.GetProperty("prediction").GetString());
            Assert.Equal(expected.GetProperty("used_outputs").GetInt32(), actual.GetProperty("used_outputs").GetInt32());
            foreach (var row in expected.GetProperty("results").EnumerateArray())
            {
                var match = actual.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("model").GetString() == row.GetProperty("model").GetString());
                foreach (var field in new[] { "probability", "score", "nuisance_score", "profile_similarity", "conditional_probability" })
                    Assert.True(Math.Abs(row.GetProperty(field).GetDouble() - match.GetProperty(field).GetDouble()) < 1e-10, $"{fixture.GetProperty("name")} / {field}");
            }
        }
    }
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public async Task UsesConfiguredCountAndOriginalRequestShape(int count)
    {
        var handler = new RecordingHandler();
        var engine = new ModelTraceClient(new HttpClient(handler));
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        var report = await engine.Test("https://example.test", "test-secret", "mock", bank, count, CancellationToken.None);
        Assert.Equal(count, handler.Requests.Count);
        Assert.Equal(count, report.GetProperty("result").GetProperty("used_outputs").GetInt32());
        Assert.Equal(count, report.GetProperty("responses").EnumerateArray().Select(r => r.GetProperty("ExpectedCount").GetInt32()).Distinct().Count());
        foreach (var body in handler.Requests)
        {
            Assert.False(body.TryGetProperty("max_tokens", out _));
            Assert.False(body.TryGetProperty("temperature", out _));
            Assert.False(body.TryGetProperty("stream", out _));
        }
    }
    [Fact]
    public async Task RejectsTruncationAndFallsBackToAnthropic()
    {
        var handler = new RecordingHandler { TruncateOpenAi = true };
        var engine = new ModelTraceClient(new HttpClient(handler));
        var content = await engine.Completion("https://example.test/v1", "test-secret", "mock", "prompt", CancellationToken.None);
        Assert.NotEmpty(content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(4096, handler.Requests[1].GetProperty("max_tokens").GetInt32());
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public bool TruncateOpenAi { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            Assert.Equal(ModelTraceClient.DefaultUserAgent, request.Headers.UserAgent.ToString());
            var text = string.Join(',', Enumerable.Range(1, 310));
            object response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
            {
                Assert.Equal("test-secret", request.Headers.GetValues("x-api-key").Single());
                response = new { content = new[] { new { type = "text", text } }, stop_reason = "end_turn" };
            }
            else response = new { choices = new[] { new { message = new { content = text }, finish_reason = TruncateOpenAi ? "length" : "stop" } } };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
