using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class CandyProbeTests
{
    [Theory]
    [InlineData("21", true)]
    [InlineData("最少取出21个糖果。", true)]
    [InlineData("**21**", true)]
    [InlineData(" \n21\r\n", true)]
    [InlineData("先考虑 21，再得出 29。", true)]
    [InlineData("21.0", true)]
    [InlineData("121", false)]
    [InlineData("210", false)]
    [InlineData("２21", false)]
    [InlineData("21３", false)]
    [InlineData("二十一", false)]
    [InlineData("是。", false)]
    [InlineData("否", false)]
    [InlineData("29", false)]
    [InlineData("", false)]
    public async Task JudgesEntireResponseAndPreservesIt(string answer, bool passes)
    {
        var handler = new Handler(answer);
        var client = new ModelTraceClient(new HttpClient(handler));
        var result = await client.ProbeCandy("https://example.test", "secret", "any-model", CancellationToken.None);
        Assert.Equal(passes ? "Success" : "Unavailable", result.CandyStatus);
        Assert.Equal(answer, result.CandyResponse);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ErrorsAreDistinctAndSecretsAreRedacted()
    {
        var client = new ModelTraceClient(new HttpClient(new Handler("secret", true)));
        var result = await client.ProbeCandy("https://example.test", "secret", "any-model", CancellationToken.None);
        Assert.Equal("Failed", result.CandyStatus);
        Assert.Null(result.CandyResponse);
        Assert.DoesNotContain("secret", result.CandyError!);
        Assert.Contains("HTTP 400", result.CandyError!);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAProbeError()
    {
        var client = new ModelTraceClient(new HttpClient(new Handler("21")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ProbeCandy("https://example.test", "secret", "any-model", cancellation.Token));
    }

    [Theory]
    [InlineData("default", null)]
    [InlineData("none", 0L)]
    [InlineData("high", 2048L)]
    [InlineData("xhigh", 4294967296L)]
    public async Task SendsEffortAndReadsReportedReasoningTokens(string effort, long? tokens)
    {
        var handler = new Handler("21", effort: effort, tokens: tokens);
        var result = await new ModelTraceClient(new HttpClient(handler)).ProbeCandy("https://example.test", "secret", "model", CancellationToken.None, effort);
        Assert.Equal("Success", result.CandyStatus);
        Assert.Equal(effort, result.CandyReasoningEffort);
        Assert.Equal(tokens, result.CandyReasoningTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnsupportedEffortIsNotSilentlyDropped()
    {
        var handler = new Handler("unsupported reasoning_effort", true, "high");
        var result = await new ModelTraceClient(new HttpClient(handler)).ProbeCandy("https://example.test", "secret", "model", CancellationToken.None, "high");
        Assert.Equal("Failed", result.CandyStatus);
        Assert.Equal("high", result.CandyReasoningEffort);
        Assert.Null(result.CandyReasoningTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"usage\":null}")]
    [InlineData("{\"usage\":{\"completion_tokens\":123}}")]
    [InlineData("{\"usage\":{\"completion_tokens_details\":{\"reasoning_tokens\":-1}}}")]
    [InlineData("{\"usage\":{\"completion_tokens_details\":{\"reasoning_tokens\":\"123\"}}}")]
    public void MissingOrInvalidReasoningUsageIsNotAnEstimate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(ModelTraceClient.ExtractReasoningTokens(doc.RootElement));
    }

    private sealed class Handler(string answer, bool error = false, string effort = "default", long? tokens = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(CandyProbe.Prompt, body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.False(body.TryGetProperty("tools", out _));
            if (effort == "default") Assert.False(body.TryGetProperty("reasoning_effort", out _));
            else Assert.Equal(effort, body.GetProperty("reasoning_effort").GetString());
            return error
                ? new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = answer }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } }, usage = new { completion_tokens_details = new { reasoning_tokens = tokens } } }) };
        }
    }
}
