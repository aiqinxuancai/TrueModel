using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueModel;

namespace TrueModel.Tests;

public class JapaneseCandyTests
{
    [Fact]
    public void RandomSpacingChangesAllThreeVariantsWithoutChangingNumbersOrExistingWhitespace()
    {
        foreach (var source in new[] { CandyProbe.Prompt, CandyProbe.CreateObfuscatedPrompt(), CandyProbe.CreateSpaceObfuscatedPrompt() })
        {
            var variants = new HashSet<string>();
            for (var i = 0; i < 100; i++)
            {
                var prompt = CandyProbe.RandomizeSpacing(source);
                Assert.NotEqual(source, prompt);
                Assert.Equal(source.Replace(" ", ""), prompt.Replace(" ", ""));
                Assert.Equal(Regex.Matches(source, @"\d+").Select(m => m.Value), Regex.Matches(prompt, @"\d+").Select(m => m.Value));
                Assert.InRange(prompt.Length - source.Length, 12, 36);
                Assert.True(variants.Add(prompt));
            }
        }
        Assert.Equal("123 456", CandyProbe.RandomizeSpacing("123 456"));
    }

    [Fact]
    public void JapanesePromptPreservesConditionsAndOnlyAddsSpacesInsideKeywords()
    {
        Assert.Equal(new[] { "3", "2", "7", "9", "8", "7", "6", "4" }, Regex.Matches(CandyProbe.Prompt, @"\d+").Select(m => m.Value));
        Assert.Contains("外部ツールを一切使わず", CandyProbe.Prompt);
        Assert.Contains("形は手触りで区別できます", CandyProbe.Prompt);
        Assert.Contains("個数を事前に決める", CandyProbe.Prompt);
        Assert.Contains("形の異なる", CandyProbe.Prompt);
        Assert.Contains("同時に手元に持っていることを確実にする", CandyProbe.Prompt);
        Assert.Contains("丸形のリンゴ味と五芒星形のモモ味", CandyProbe.Prompt);
        Assert.Contains("丸形のモモ味と五芒星形のリンゴ味", CandyProbe.Prompt);
        Assert.Contains("リンゴ味  モモ味  スイカ味", CandyProbe.Prompt);
        Assert.Contains("丸形         7       9       8", CandyProbe.Prompt);
        Assert.Contains("五芒星形     7       6       4", CandyProbe.Prompt);
        var variants = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var prompt = CandyProbe.CreateSpaceObfuscatedPrompt();
            variants.Add(prompt);
            Assert.NotEqual(CandyProbe.Prompt, prompt);
            Assert.Equal(CandyProbe.Prompt.Replace(" ", ""), prompt.Replace(" ", ""));
            Assert.DoesNotMatch("キャンディー|リンゴ味|モモ味|スイカ味|丸形|五芒星形|最少", prompt);
        }
        Assert.True(variants.Count > 1);
    }

    [Theory]
    [InlineData("21", false, "Success")]
    [InlineData("29", false, "Unavailable")]
    [InlineData("secret error", true, "Failed")]
    public async Task AllAttemptsUseJapaneseAndThirdPersistsActualPrompt(string answer, bool error, string expected)
    {
        var handler = new Handler(answer, error);
        var result = await new ModelTraceClient(new HttpClient(handler)).ProbeCandy("https://example.test", "secret", "model", CancellationToken.None, "high");
        Assert.Equal(3, handler.Prompts.Count);
        Assert.Equal("Failed", result.CandyStatus);
        Assert.Equal("Success", result.Candy2Status);
        Assert.Equal(expected, result.Candy3Status);
        Assert.Equal(handler.Prompts[2], result.Candy3Prompt);
        Assert.All(handler.Prompts, prompt => Assert.Equal(CandyTestPrompts.WithoutWhitespace(CandyProbe.Prompt), CandyTestPrompts.WithoutWhitespace(prompt)));
        Assert.All(handler.Prompts, prompt => Assert.NotEqual(CandyProbe.Prompt, prompt));
        Assert.Equal(3, handler.Prompts.Distinct().Count());
        Assert.Equal(handler.Prompts[0], result.CandyPrompt);
        Assert.Equal(handler.Prompts[1], result.Candy2Prompt);
        Assert.Equal(CandyTestPrompts.WithoutWhitespace(CandyProbe.Prompt), CandyTestPrompts.WithoutWhitespace(result.Candy3Prompt!));
        Assert.Equal(error ? null : answer, result.Candy3Response);
        Assert.Equal(123L, result.Candy2ReasoningTokens);
        Assert.Equal(error ? (long?)null : 789L, result.Candy3ReasoningTokens);
        Assert.NotNull(result.Candy3LatencyMs);
        Assert.DoesNotContain("secret", result.Candy3Error ?? "");
    }

    [Fact]
    public async Task CancellationAfterSecondAttemptDoesNotSendThirdRequest()
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
            Assert.Equal(3, body.GetProperty("messages").GetArrayLength());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Prompts.Add(TestConversation.Prompt(body)!);
            if (Prompts.Count == 2) AfterSecond?.Invoke();
            if (Prompts.Count == 1 || (Prompts.Count == 3 && error))
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "secret error" }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = Prompts.Count == 2 ? "21" : answer }, finish_reason = "stop" } }, usage = new { completion_tokens_details = new { reasoning_tokens = Prompts.Count == 2 ? 123 : 789 } } }) };
        }
    }
}
