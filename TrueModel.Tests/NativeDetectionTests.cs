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
    public async Task UsesConfiguredCountAndWrappedRequestShape(int count)
    {
        var handler = new RecordingHandler();
        var engine = new ModelTraceClient(new HttpClient(handler));
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        var report = await engine.Test("https://example.test", "test-secret", "mock", bank, count, CancellationToken.None);
        Assert.Equal(count + 3, handler.Requests.Count);
        Assert.True(CandyTestPrompts.IsCandy(TestConversation.Prompt(handler.Requests[^3])));
        Assert.NotEqual(CandyProbe.Prompt, TestConversation.Prompt(handler.Requests[^3]));
        var secondPrompt = TestConversation.Prompt(handler.Requests[^2]);
        Assert.NotEqual(CandyProbe.Prompt, secondPrompt);
        Assert.True(CandyTestPrompts.IsCandy(secondPrompt));
        var thirdPrompt = TestConversation.Prompt(handler.Requests[^1])!;
        Assert.NotEqual(CandyProbe.Prompt, thirdPrompt);
        Assert.Equal(CandyTestPrompts.WithoutWhitespace(CandyProbe.Prompt), CandyTestPrompts.WithoutWhitespace(thirdPrompt));
        Assert.Equal(count, report.GetProperty("result").GetProperty("used_outputs").GetInt32());
        Assert.Equal(count, report.GetProperty("responses").EnumerateArray().Select(r => r.GetProperty("ExpectedCount").GetInt32()).Distinct().Count());
        foreach (var body in handler.Requests)
        {
            TestConversation.Prompt(body);
            Assert.False(body.TryGetProperty("max_tokens", out _));
            Assert.False(body.TryGetProperty("max_output_tokens", out _));
            Assert.False(body.TryGetProperty("temperature", out _));
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal(ModelTraceClient.CodexInstructions, body.GetProperty("instructions").GetString());
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
        Assert.All(handler.Requests, body => Assert.Equal("prompt", TestConversation.Prompt(body)));
        Assert.Equal(4096, handler.Requests[1].GetProperty("max_tokens").GetInt32());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public bool TruncateOpenAi { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            var text = string.Join(',', Enumerable.Range(1, 310));
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
            {
                Assert.Equal(ModelTraceClient.ClaudeCodeUserAgent, request.Headers.UserAgent.ToString());
                Assert.Equal("test-secret", request.Headers.GetValues("x-api-key").Single());
                Assert.Equal("cli", request.Headers.GetValues("x-app").Single());
                Assert.Equal("?beta=true", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { content = new[] { new { type = "text", text } }, stop_reason = "end_turn" }) };
            }
            Assert.Equal(ModelTraceClient.CodexUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal(ModelTraceClient.CodexOriginator, request.Headers.GetValues("originator").Single());
            return TestConversation.ResponsesOk(text, status: TruncateOpenAi ? "incomplete" : "completed", incompleteReason: TruncateOpenAi ? "max_output_tokens" : null);
        }
    }
}
