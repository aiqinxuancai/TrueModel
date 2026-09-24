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

    private sealed class Handler(string answer, bool error = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(CandyProbe.Prompt, body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.False(body.TryGetProperty("tools", out _));
            return error
                ? new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = answer }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } }) };
        }
    }
}
