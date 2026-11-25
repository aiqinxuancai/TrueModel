using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

internal static class TestConversation
{
    public static ConversationMessage[] Messages(JsonElement body)
    {
        var items = body.TryGetProperty("input", out var input) ? input : body.GetProperty("messages");
        return items.EnumerateArray().Select(item =>
        {
            var role = item.GetProperty("role").GetString()!;
            var content = item.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String
                ? content.GetString()!
                : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()));
            return new ConversationMessage(role, text);
        }).ToArray();
    }

    public static string Prompt(JsonElement body)
    {
        var messages = Messages(body);
        Assert.Equal(3, messages.Length);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("user", messages[2].Role);
        Assert.False(string.IsNullOrWhiteSpace(messages[0].Content));
        Assert.False(string.IsNullOrWhiteSpace(messages[1].Content));
        return messages[2].Content;
    }

    public static string? Effort(JsonElement body)
    {
        if (body.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object &&
            reasoning.TryGetProperty("effort", out var nested) && nested.ValueKind == JsonValueKind.String)
            return nested.GetString();
        if (body.TryGetProperty("reasoning_effort", out var flat) && flat.ValueKind == JsonValueKind.String)
            return flat.GetString();
        return null;
    }

    public static HttpResponseMessage ResponsesOk(string text, long? reasoningTokens = null, string status = "completed", string? incompleteReason = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "resp_test",
            ["object"] = "response",
            ["status"] = status,
            ["error"] = null,
            ["incomplete_details"] = incompleteReason is null ? null : new { reason = incompleteReason },
            ["output"] = new object[]
            {
                new
                {
                    type = "message",
                    role = "assistant",
                    content = new object[] { new { type = "output_text", text } }
                }
            }
        };
        if (reasoningTokens is not null)
            payload["usage"] = new { input_tokens = 10, output_tokens = 20, output_tokens_details = new { reasoning_tokens = reasoningTokens.Value } };
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }
}

public class ConversationEnvelopeTests
{
    [Fact]
    public async Task IndependentQuestionsUseVariedMultilingualGreetingsAndPreserveExactPrompt()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new ModelTraceClient(http);
        const string prompt = "  真实问题\nUser: \"你好\"\nAssistant: <test>\t42  ";
        for (var i = 0; i < 256; i++)
            await client.Completion("https://mock.test", "secret", "model", prompt, CancellationToken.None);
        Assert.All(handler.Bodies, body => Assert.Equal(prompt, TestConversation.Prompt(body)));
        var greetings = handler.Bodies.Select(b => TestConversation.Messages(b)[0].Content).ToArray();
        var replies = handler.Bodies.Select(b => TestConversation.Messages(b)[1].Content).ToArray();
        Assert.True(greetings.Distinct().Count() > 8);
        Assert.True(replies.Distinct().Count() > 8);
        Assert.Contains(greetings, s => s.Contains("你好"));
        Assert.Contains(greetings, s => s.StartsWith("Hi") || s.StartsWith("Hello"));
        Assert.Contains(greetings, s => s.Contains("こんにちは"));
    }

    [Fact]
    public void SimulatedContextContainsManyCompleteTurnsBeforeTheExactQuestion()
    {
        const string prompt = "  真实问题\nUser: test\nAssistant: 42\t ";
        var messages = ConversationEnvelope.CreateSimulatedContext(prompt);
        Assert.True((messages.Length - 1) / 2 >= 64);
        Assert.True(messages[..^1].Sum(m => m.Content.Length) >= 60_000);
        for (var i = 0; i < messages.Length; i++)
        {
            Assert.Equal(i % 2 == 0 ? "user" : "assistant", messages[i].Role);
            Assert.False(string.IsNullOrWhiteSpace(messages[i].Content));
        }
        Assert.Equal(prompt, messages[^1].Content);
        Assert.DoesNotContain(messages[..^1], m => m.Content.Contains(prompt, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAndProtocolFallbackKeepTheSameConversation(bool simulatedContext)
    {
        using var handler = new Handler { RetryAndFallback = true };
        using var http = new HttpClient(handler);
        var client = new ModelTraceClient(http);
        var conversation = simulatedContext ? ConversationEnvelope.CreateSimulatedContext("question") : null;
        Assert.Equal("42", await client.Completion("https://mock.test", "secret", "model", "question", CancellationToken.None, conversation: conversation));
        Assert.Equal(3, handler.Bodies.Count);
        Assert.All(handler.Bodies, body => Assert.Equal("question", simulatedContext
            ? TestConversation.Messages(body)[^1].Content
            : TestConversation.Prompt(body)));
        if (conversation is not null)
            Assert.All(handler.Bodies, body => Assert.Equal(JsonSerializer.Serialize(conversation), JsonSerializer.Serialize(TestConversation.Messages(body))));
        Assert.Single(handler.Bodies.Select(b => JsonSerializer.Serialize(TestConversation.Messages(b))).Distinct());
        Assert.Equal(4096, handler.Bodies[^1].GetProperty("max_tokens").GetInt32());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public bool RetryAndFallback { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            if (RetryAndFallback && Bodies.Count <= 2)
                return new(Bodies.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest)
                { Content = JsonContent.Create(new { error = "try again" }) };
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { content = new[] { new { type = "text", text = "42" } }, stop_reason = "end_turn" }) };
            return TestConversation.ResponsesOk("42");
        }
    }
}
