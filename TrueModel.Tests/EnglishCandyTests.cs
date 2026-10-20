using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueModel;

namespace TrueModel.Tests;

public class EnglishCandyTests
{
    [Fact]
    public void EnglishPromptPreservesCountsAndOnlyAddsSpacesInsideKeywords()
    {
        Assert.All(CandyProbe.EnglishPrompt, c => Assert.True(c < 128));
        Assert.Equal(new[] { "7", "9", "8", "7", "6", "4" }, Regex.Matches(CandyProbe.EnglishPrompt, @"\d+").Select(m => m.Value));
        Assert.Contains("decide in advance", CandyProbe.EnglishPrompt);
        Assert.Contains("guarantee", CandyProbe.EnglishPrompt);
        Assert.Contains("different shapes", CandyProbe.EnglishPrompt);
        Assert.Contains("round apple-flavored candy paired with a five-pointed star peach-flavored candy", CandyProbe.EnglishPrompt);
        Assert.Contains("round peach-flavored candy paired with a five-pointed star apple-flavored candy", CandyProbe.EnglishPrompt);
        var variants = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var prompt = CandyProbe.CreateEnglishObfuscatedPrompt();
            variants.Add(prompt);
            Assert.NotEqual(CandyProbe.EnglishPrompt, prompt);
            Assert.Equal(CandyProbe.EnglishPrompt.Replace(" ", ""), prompt.Replace(" ", ""));
            Assert.DoesNotMatch(@"(?i)\b(candy|candies|apple|peach|watermelon|round|star|minimum|shapes?)\b", prompt);
        }
        Assert.True(variants.Count > 1);
    }

    [Theory]
    [InlineData("21", false, "Success")]
    [InlineData("29", false, "Unavailable")]
    [InlineData("secret error", true, "Failed")]
    public async Task ThirdAttemptIsIndependentAndPersistsActualEnglishPrompt(string answer, bool error, string expected)
    {
        var handler = new Handler(answer, error);
        var result = await new ModelTraceClient(new HttpClient(handler)).ProbeCandy("https://example.test", "secret", "model", CancellationToken.None, "high");
        Assert.Equal(3, handler.Prompts.Count);
        Assert.Equal("Failed", result.CandyStatus);
        Assert.Equal("Success", result.Candy2Status);
        Assert.Equal(expected, result.Candy3Status);
        Assert.Equal(handler.Prompts[2], result.Candy3Prompt);
        Assert.Equal(CandyTestPrompts.WithoutWhitespace(CandyProbe.EnglishPrompt), CandyTestPrompts.WithoutWhitespace(result.Candy3Prompt!));
        Assert.Equal(error ? null : answer, result.Candy3Response);
        Assert.Equal(123L, result.Candy2ReasoningTokens);
        Assert.Equal(error ? (long?)null : 789L, result.Candy3ReasoningTokens);
        Assert.NotNull(result.Candy3LatencyMs);
        Assert.DoesNotContain("secret", result.Candy3Error ?? "");
    }

    [Fact]
    public async Task CancellationAfterSecondAttemptDoesNotSendEnglishRequest()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler("21", false) { AfterSecond = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelTraceClient(new HttpClient(handler))
            .ProbeCandy("https://example.test", "secret", "model", cancel.Token, "high"));
        Assert.Equal(2, handler.Prompts.Count);
    }

    private sealed class Handler(string answer, bool error) : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];
        public Action? AfterSecond { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(1, body.GetProperty("messages").GetArrayLength());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Prompts.Add(body.GetProperty("messages")[0].GetProperty("content").GetString()!);
            if (Prompts.Count == 2) AfterSecond?.Invoke();
            if (Prompts.Count == 1 || (Prompts.Count == 3 && error))
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "secret error" }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = Prompts.Count == 2 ? "21" : answer }, finish_reason = "stop" } }, usage = new { completion_tokens_details = new { reasoning_tokens = Prompts.Count == 2 ? 123 : 789 } } }) };
        }
    }
}
