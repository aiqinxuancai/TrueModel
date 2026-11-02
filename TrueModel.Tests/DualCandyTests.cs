using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class DualCandyTests
{
    [Fact]
    public void ObfuscationChangesOnlyWhitespaceAndSplitsEveryKeyword()
    {
        var variants = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var prompt = CandyProbe.CreateObfuscatedPrompt();
            variants.Add(prompt);
            Assert.NotEqual(CandyProbe.Prompt, prompt);
            Assert.Equal(CandyTestPrompts.WithoutWhitespace(CandyProbe.Prompt), CandyTestPrompts.WithoutWhitespace(prompt));
            Assert.Contains('\t', prompt);
            Assert.Contains('\n', prompt);
            foreach (var keyword in new[] { "キャンディー", "リンゴ味", "モモ味", "スイカ味", "丸形", "五芒星形", "最少" })
                Assert.DoesNotContain(keyword, prompt);
        }
        Assert.True(variants.Count > 1);
    }

    [Theory]
    [InlineData("21", false, "29", false, "Success", "Unavailable")]
    [InlineData("29", false, "21", false, "Unavailable", "Success")]
    [InlineData("secret error", true, "21", false, "Failed", "Success")]
    [InlineData("21", false, "secret error", true, "Success", "Failed")]
    public async Task AttemptsHaveIndependentRequestsResultsAndUsage(string first, bool firstError, string second, bool secondError, string firstStatus, string secondStatus)
    {
        var handler = new Handler(first, firstError, second, secondError);
        var result = await new ModelTraceClient(new HttpClient(handler)).ProbeCandy("https://example.test", "secret", "model", CancellationToken.None, "high");
        Assert.Equal(3, handler.Prompts.Count);
        Assert.NotEqual(CandyProbe.Prompt, handler.Prompts[0]);
        Assert.True(CandyTestPrompts.IsCandy(handler.Prompts[0]));
        Assert.Equal(handler.Prompts[0], result.CandyPrompt);
        Assert.NotEqual(handler.Prompts[0], handler.Prompts[1]);
        Assert.Equal(handler.Prompts[1], result.Candy2Prompt);
        Assert.Equal(firstStatus, result.CandyStatus);
        Assert.Equal(secondStatus, result.Candy2Status);
        Assert.Equal(firstError ? null : first, result.CandyResponse);
        Assert.Equal(secondError ? null : second, result.Candy2Response);
        Assert.Equal(firstError ? (long?)null : 0L, result.CandyReasoningTokens);
        Assert.Equal(secondError ? (long?)null : 4321L, result.Candy2ReasoningTokens);
        Assert.NotNull(result.CandyLatencyMs);
        Assert.NotNull(result.Candy2LatencyMs);
        Assert.DoesNotContain("secret", result.CandyError ?? "");
        Assert.DoesNotContain("secret", result.Candy2Error ?? "");
    }

    [Fact]
    public async Task CancellationBetweenAttemptsDoesNotSendSecondRequest()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler("21", false, "21", false) { AfterFirst = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelTraceClient(new HttpClient(handler)).ProbeCandy("https://example.test", "secret", "model", cancel.Token, "high"));
        Assert.Single(handler.Prompts);
    }

    private sealed class Handler(string first, bool firstError, string second, bool secondError) : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];
        public Action? AfterFirst { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(3, body.GetProperty("messages").GetArrayLength());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Prompts.Add(TestConversation.Prompt(body)!);
            var initial = Prompts.Count == 1;
            var error = initial ? firstError : secondError;
            var answer = initial ? first : second;
            if (initial) AfterFirst?.Invoke();
            return error
                ? new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = answer }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } }, usage = new { completion_tokens_details = new { reasoning_tokens = initial ? 0 : 4321 } } }) };
        }
    }
}
