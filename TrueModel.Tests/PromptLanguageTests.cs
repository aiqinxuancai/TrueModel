using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueModel;

namespace TrueModel.Tests;

public class PromptLanguageTests
{
    public static TheoryData<string> Languages => new() { "zh", "en", "ja", "ko", "fr", "es", "de", "pt" };
    private static string Clean(string value) => CandyTestPrompts.WithoutWhitespace(value);

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task EachLanguageReachesAttributionEnrollmentHlwyAndAllCandyAttempts(string language)
    {
        var translation = PromptLanguages.Get(language);
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new ModelTraceClient(http);
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "unified_bank.json"));
        var result = await client.Test("https://mock.test", "secret", "model", bank, 3, CancellationToken.None,
            attributionLanguage: language, candyLanguage: language, candy2Language: language, candy3Language: language);
        Assert.Equal(6, handler.Prompts.Count);
        var responses = result.GetProperty("responses");
        for (var i = 0; i < 3; i++)
        {
            var prompt = handler.Prompts[i];
            Assert.Equal(prompt, responses[i].GetProperty("Prompt").GetString());
            if (language != "zh")
                Assert.Equal(Clean(translation.Attribution.Replace("{count}", responses[i].GetProperty("ExpectedCount").GetInt32().ToString())), Clean(prompt));
            else Assert.Contains("本任务必须由当前语言模型直接完成", Clean(prompt));
            Assert.Contains("355", prompt);
        }
        Assert.All(handler.Prompts.Skip(3), prompt => Assert.Equal(Clean(translation.Candy), Clean(prompt)));
        for (var i = 1; i <= 3; i++)
            Assert.Equal(handler.Prompts[i + 2], result.GetProperty("candy").GetProperty(i == 1 ? "CandyPrompt" : $"Candy{i}Prompt").GetString());
        Assert.Equal(new[] { "7", "9", "8", "7", "6", "4" }, Regex.Matches(translation.Candy, @"\d+").Select(m => m.Value).TakeLast(6));
        Assert.All(handler.Prompts, AssertWhitespace);
        handler.Prompts.Clear();
        await FingerprintEnrollment.Collect(client, "https://mock.test", "secret", "model", 3, CancellationToken.None, language);
        Assert.Equal(3, handler.Prompts.Count);
        Assert.All(handler.Prompts, AssertWhitespace);
        handler.Prompts.Clear();
        await client.TestHlwy("https://mock.test", "secret", "model", new AppSettings { AttributionLanguage = language, HlwySampleCount = 40, HlwyConcurrency = 1 }, CancellationToken.None, false);
        Assert.Equal(40, handler.Prompts.Count);
        Assert.All(handler.Prompts, prompt => { Assert.Equal(Clean(translation.Hlwy), Clean(prompt)); AssertWhitespace(prompt); });
    }

    [Fact]
    public async Task CandyAttemptsUseIndependentLanguagesAndNewWhitespaceOnRepeat()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new ModelTraceClient(http);
        for (var i = 0; i < 2; i++)
            await client.ProbeCandy("https://mock.test", "secret", "model", CancellationToken.None, language: "en", language2: "ja", language3: "fr");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Clean(PromptLanguages.Get(new[] { "en", "ja", "fr" }[i]).Candy), Clean(handler.Prompts[i]));
            Assert.Equal(Clean(handler.Prompts[i]), Clean(handler.Prompts[i + 3]));
            Assert.NotEqual(handler.Prompts[i], handler.Prompts[i + 3]);
        }
        Assert.All(handler.Prompts, AssertWhitespace);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProbeCandy("https://mock.test", "secret", "model", CancellationToken.None, language3: "invalid"));
        Assert.Equal(6, handler.Prompts.Count);
    }

    private static void AssertWhitespace(string prompt)
    {
        Assert.Contains(" ", prompt);
        Assert.Contains("\n\n", prompt);
        Assert.DoesNotContain("{count}", prompt);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            var prompt = TestConversation.Prompt(body);
            Prompts.Add(prompt);
            var answer = body.TryGetProperty("temperature", out _) ? "42" : CandyTestPrompts.IsCandy(prompt) ? "21" : string.Join(',', Enumerable.Range(1, 310));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } }) };
        }
    }
}
