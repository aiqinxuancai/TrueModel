using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class JuiceDiagnosticsTests
{
    [Theory]
    [InlineData("empty", "Unavailable", "空回复", "")]
    [InlineData("invalid", "Unavailable", "非负整数", "I cannot share [REDACTED]")]
    [InlineData("refusal", "Unavailable", "拒绝", "Refused [REDACTED]")]
    [InlineData("length", "Unavailable", "截断", "128")]
    [InlineData("content_filter", "Unavailable", "过滤", "128")]
    [InlineData("http", "Failed", "HTTP 401: Invalid [REDACTED]", null)]
    [InlineData("json", "Failed", "接口响应格式异常", null)]
    public async Task FailuresIncludeSpecificReasonsAndRedactedReplies(string mode, string status, string error, string? reply)
    {
        using var http = new HttpClient(new Handler(mode));
        var result = await new ModelTraceClient(http).ProbeJuice("https://mock.test", "test-secret", "gpt-mock", CancellationToken.None, "direct");
        Assert.Equal(status, result["juice_status"]);
        Assert.Contains(error, result["juice_error"] as string);
        Assert.Equal(reply, result["juice_response"]);
        Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(result));
    }

    private sealed class Handler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (mode == "http") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = "Invalid test-secret" }) });
            if (mode == "json") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new
            {
                message = new { content = mode == "empty" ? null : mode == "invalid" ? "I cannot share test-secret" : "128", refusal = mode == "refusal" ? "Refused test-secret" : null },
                finish_reason = mode is "length" or "content_filter" ? mode : "stop"
            } } }) });
        }
    }
}
