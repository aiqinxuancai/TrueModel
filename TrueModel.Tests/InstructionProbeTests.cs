using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class InstructionProbeTests
{
    [Theory]
    [InlineData("是", true)]
    [InlineData("否", true)]
    [InlineData("是。", true)]
    [InlineData("否。", true)]
    [InlineData(" \n否。\r\n", true)]
    [InlineData("是，因为需要适配。", false)]
    [InlineData("否。\n你没有要求。", false)]
    [InlineData("是.", false)]
    [InlineData("**是**", false)]
    [InlineData("", false)]
    public async Task JudgesEntireResponseAndPreservesIt(string answer, bool passes)
    {
        var handler = new Handler(answer);
        var client = new ModelTraceClient(new HttpClient(handler));
        var result = await client.ProbeInstruction("https://example.test", "secret", "any-model", CancellationToken.None);
        Assert.Equal(passes ? "Success" : "Unavailable", result.InstructionStatus);
        Assert.Equal(answer, result.InstructionResponse);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ErrorsAreDistinctAndSecretsAreRedacted()
    {
        var client = new ModelTraceClient(new HttpClient(new Handler("secret", true)));
        var result = await client.ProbeInstruction("https://example.test", "secret", "any-model", CancellationToken.None);
        Assert.Equal("Failed", result.InstructionStatus);
        Assert.Null(result.InstructionResponse);
        Assert.DoesNotContain("secret", result.InstructionError!);
        Assert.Contains("HTTP 400", result.InstructionError!);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAProbeError()
    {
        var client = new ModelTraceClient(new HttpClient(new Handler("是")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ProbeInstruction("https://example.test", "secret", "any-model", cancellation.Token));
    }

    private sealed class Handler(string answer, bool error = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(InstructionProbe.Prompt, body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.False(body.TryGetProperty("tools", out _));
            return error
                ? new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = answer }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } }) };
        }
    }
}
