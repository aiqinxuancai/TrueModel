using System.Net;
using System.Text;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class StreamingCompletionTests
{
    private static HttpResponseMessage Events(params string[] data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(data.Select(d => $"data: {d}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    [Fact]
    public async Task AssemblesCodexResponsesDeltasAndUsage()
    {
        var handler = new Handler((request, body) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
            return Events(
                """{"type":"response.created","response":{"id":"resp_1","status":"in_progress"}}""",
                """{"type":"response.reasoning_summary_text.delta","delta":"thinking"}""",
                """{"type":"response.output_text.delta","delta":"```javascript\n"}""",
                """{"type":"response.output_text.delta","delta":"x = 1"}""",
                """{"type":"response.completed","response":{"id":"resp_1","status":"completed","error":null,"usage":{"input_tokens":10,"output_tokens":400,"output_tokens_details":{"reasoning_tokens":321}}}}""");
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion("https://mock.test", "secret", "model", "q", CancellationToken.None,
            observeReasoningTokens: value => tokens = value, stream: true);
        Assert.Equal("```javascript\nx = 1", text);
        Assert.Equal(321, tokens);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FallsBackToChatCompletionsDeltasAndUsage()
    {
        var handler = new Handler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/responses"))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
            return Events("""{"choices":[{"delta":{"role":"assistant"}}]}""", """{"choices":[{"delta":{"content":"```javascript\n"}}]}""",
                """{"choices":[{"delta":{"content":"x = 1"},"finish_reason":null}]}""", """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                """{"choices":[],"usage":{"completion_tokens_details":{"reasoning_tokens":321}}}""", "[DONE]");
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion("https://mock.test", "secret", "model", "q", CancellationToken.None,
            observeReasoningTokens: value => tokens = value, stream: true);
        Assert.Equal("```javascript\nx = 1", text);
        Assert.Equal(321, tokens);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FallsBackToAnthropicEvents()
    {
        var handler = new Handler((request, _) => !request.RequestUri!.AbsolutePath.EndsWith("/messages")
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") }
            : Events("""{"type":"message_start"}""", """{"type":"content_block_delta","delta":{"type":"text_delta","text":"hel"}}""",
                """{"type":"content_block_delta","delta":{"type":"text_delta","text":"lo"}}""", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
                """{"type":"message_stop"}"""));
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion("https://mock.test", "secret", "model", "q", CancellationToken.None, stream: true);
        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task TruncatedStreamFailsWithoutTryingAnotherProtocol()
    {
        var handler = new Handler((_, _) => Events(
            """{"type":"response.output_text.delta","delta":"partial"}""",
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}"""));
        var error = await Assert.ThrowsAsync<UpstreamException>(() => new ModelTraceClient(new HttpClient(handler))
            .Completion("https://mock.test", "secret", "model", "q", CancellationToken.None, stream: true));
        Assert.Contains("max_output_tokens", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExplainsCloudflareOriginTimeouts()
    {
        var handler = new Handler((_, _) => new HttpResponseMessage((HttpStatusCode)524) { Content = new StringContent("error code: 524") });
        var error = await Assert.ThrowsAsync<UpstreamException>(() => new ModelTraceClient(new HttpClient(handler))
            .Completion("https://mock.test", "secret", "model", "q", CancellationToken.None, maxAttempts: 1));
        Assert.Contains("Cloudflare 等待源站响应超时", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, JsonElement, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return respond(request, body.RootElement.Clone());
        }
    }
}
