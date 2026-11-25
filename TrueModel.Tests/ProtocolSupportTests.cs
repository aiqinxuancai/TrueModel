using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class ProtocolSupportTests
{
    [Theory]
    [InlineData("https://api.anthropic.com", "anthropic")]
    [InlineData("https://api.anthropic.com/v1", "anthropic")]
    [InlineData("https://relay.test/v1/messages", "anthropic")]
    [InlineData("https://relay.test/v1/messages?beta=true", "anthropic")]
    [InlineData("https://api.openai.com", "openai")]
    [InlineData("https://api.openai.com/v1", "openai")]
    [InlineData("https://chatgpt.com/backend-api/codex", "openai")]
    [InlineData("https://relay.test/v1/responses", "openai")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "gemini")]
    [InlineData("https://relay.test/v1beta/models/gemini-2.5-pro:generateContent", "gemini")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "openai")]
    [InlineData("https://relay.test/v1/chat/completions", "openai")]
    [InlineData("https://relay.test", null)]
    public void InfersProtocolFromKnownUrls(string url, string? expected) =>
        Assert.Equal(expected, ApiProtocols.InferFromUrl(url));

    [Theory]
    [InlineData("https://api.openai.com", "openai", "gpt-5.4", false, "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com/v1/", "openai", "gpt-5.4", true, "https://api.openai.com/v1/responses")]
    [InlineData("https://chatgpt.com/backend-api/codex", "openai", "gpt-5.4", true, "https://chatgpt.com/backend-api/codex/responses")]
    [InlineData("https://relay.test/v1/responses", "openai", "gpt-5.4", false, "https://relay.test/v1/responses")]
    [InlineData("https://relay.test/v1/chat/completions", "openai", "gpt-5.4", false, "https://relay.test/v1/chat/completions")]
    [InlineData("https://api.anthropic.com", "anthropic", "claude-sonnet-4-6", false, "https://api.anthropic.com/v1/messages?beta=true")]
    [InlineData("https://api.anthropic.com/v1/", "anthropic", "claude-sonnet-4-6", true, "https://api.anthropic.com/v1/messages?beta=true")]
    [InlineData("https://api.anthropic.com/v1/messages?beta=true", "anthropic", "claude-sonnet-4-6", true, "https://api.anthropic.com/v1/messages?beta=true")]
    [InlineData("https://relay.test/v1/chat/completions", "anthropic", "claude-sonnet-4-6", false, "https://relay.test/v1/messages?beta=true")]
    [InlineData("https://relay.test/v1/responses", "anthropic", "claude-sonnet-4-6", false, "https://relay.test/v1/messages?beta=true")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini", "gemini-2.5-pro", false, "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini", "models/gemini-2.5-pro", true, "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse")]
    [InlineData("https://generativelanguage.googleapis.com/v1/", "gemini", "gemini-3-pro", false, "https://generativelanguage.googleapis.com/v1/models/gemini-3-pro:generateContent")]
    [InlineData("https://relay.test/v1beta/models", "gemini", "gemini-2.5-flash", false, "https://relay.test/v1beta/models/gemini-2.5-flash:generateContent")]
    [InlineData("https://relay.test/v1beta/models/gemini-2.5-pro:generateContent", "gemini", "gemini-2.5-pro", true, "https://relay.test/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "openai", "gemini-2.5-pro", false, "https://generativelanguage.googleapis.com/v1beta/openai/responses")]
    public void BuildsCompletionUrlsForEachProtocol(string baseUrl, string protocol, string model, bool stream, string expected) =>
        Assert.Equal(expected, ModelTraceClient.CompletionUrl(baseUrl, protocol, model, stream));

    [Theory]
    [InlineData("https://api.anthropic.com", "anthropic", "https://api.anthropic.com/v1/models")]
    [InlineData("https://api.anthropic.com/v1/messages", "anthropic", "https://api.anthropic.com/v1/models")]
    [InlineData("https://api.anthropic.com/v1/messages?beta=true", "anthropic", "https://api.anthropic.com/v1/models")]
    [InlineData("https://api.openai.com/v1/responses", "openai", "https://api.openai.com/v1/models")]
    [InlineData("https://chatgpt.com/backend-api/codex/responses", "openai", "https://chatgpt.com/backend-api/codex/models")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini", "https://generativelanguage.googleapis.com/v1beta/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1/", "gemini", "https://generativelanguage.googleapis.com/v1/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent", "gemini", "https://generativelanguage.googleapis.com/v1beta/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "openai", "https://generativelanguage.googleapis.com/v1beta/openai/models")]
    public void BuildsModelsUrlsForEachProtocol(string baseUrl, string protocol, string expected) =>
        Assert.Equal(expected, ModelDiscovery.ModelsUrl(baseUrl, protocol));

    [Fact]
    public async Task AnthropicNativeSimulatesClaudeCodeAndFiltersThinkingBlocks()
    {
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages?beta=true", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("sk-ant-test", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.Equal(ModelTraceClient.ClaudeCodeUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal("cli", request.Headers.GetValues("x-app").Single());
            Assert.Equal("true", request.Headers.GetValues("anthropic-dangerous-direct-browser-access").Single());
            Assert.Equal(ModelTraceClient.ClaudeCodeBeta, request.Headers.GetValues("anthropic-beta").Single());
            Assert.Equal("js", request.Headers.GetValues("x-stainless-lang").Single());
            Assert.Equal("node", request.Headers.GetValues("x-stainless-runtime").Single());
            Assert.Equal(ModelTraceClient.ClaudeCodeStainlessPackageVersion, request.Headers.GetValues("x-stainless-package-version").Single());
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
            Assert.Equal("claude-sonnet-4-6", body.GetProperty("model").GetString());
            Assert.True(body.GetProperty("stream").GetBoolean());
            var system = Assert.Single(body.GetProperty("system").EnumerateArray());
            Assert.Equal("text", system.GetProperty("type").GetString());
            Assert.Equal(ModelTraceClient.ClaudeCodeSystemPrompt, system.GetProperty("text").GetString());
            Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());
            var userId = body.GetProperty("metadata").GetProperty("user_id").GetString()!;
            Assert.Matches(@"^user_[a-f0-9]{64}_account__session_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", userId);
            var messages = body.GetProperty("messages");
            Assert.Equal(3, messages.GetArrayLength());
            Assert.Equal("text", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
            Assert.Equal("question", messages[2].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", body.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.True(body.GetProperty("max_tokens").GetInt32() >= 8192);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    content = new object[]
                    {
                        new { type = "thinking", thinking = "internal 999" },
                        new { type = "redacted_thinking", data = "opaque" },
                        new { type = "text", text = "21" }
                    },
                    stop_reason = "end_turn",
                    usage = new { input_tokens = 10, output_tokens = 50, thinking_tokens = 35 }
                })
            };
        });
        long? reasoningTokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://api.anthropic.com", "sk-ant-test", "claude-sonnet-4-6", "question", CancellationToken.None,
            reasoningEffort: "high", observeReasoningTokens: value => reasoningTokens = value);
        Assert.Equal("21", text);
        Assert.Equal(35, reasoningTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("claude-3-7-sonnet-20250219", "medium", "enabled", 4096, null)]
    [InlineData("claude-sonnet-4-5-20250929", "high", "enabled", 8192, null)]
    [InlineData("claude-opus-4-6", "xhigh", "adaptive", null, "max")]
    [InlineData("claude-opus-5-5", "low", "adaptive", null, "low")]
    [InlineData("claude-sonnet-4-6", "none", "disabled", null, null)]
    public async Task AnthropicMapsEffortByModelGeneration(string model, string effort, string expectedType, int? expectedBudget, string? expectedOutputEffort)
    {
        var handler = new DelegateHandler((request, body) =>
        {
            var thinking = body.GetProperty("thinking");
            Assert.Equal(expectedType, thinking.GetProperty("type").GetString());
            if (expectedBudget is int budget)
            {
                Assert.Equal(budget, thinking.GetProperty("budget_tokens").GetInt32());
                Assert.True(body.GetProperty("max_tokens").GetInt32() > budget);
                Assert.False(body.TryGetProperty("output_config", out _));
            }
            if (expectedOutputEffort is not null)
                Assert.Equal(expectedOutputEffort, body.GetProperty("output_config").GetProperty("effort").GetString());
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { content = new[] { new { type = "text", text = "ok" } }, stop_reason = "end_turn" })
            };
        });
        Assert.Equal("ok", await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://relay.test", "secret", model, "q", CancellationToken.None, reasoningEffort: effort, protocol: ApiProtocols.Anthropic));
    }

    [Fact]
    public async Task AnthropicFallsBackToBearerOn401AndStreamsOnlyTextDeltas()
    {
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.Headers.Authorization is null)
                return new(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = new { message = "use bearer" } }) };
            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("proxy-token", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            return Sse(
                """{"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""",
                """{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"secret 123"}}""",
                """{"type":"content_block_delta","delta":{"type":"text_delta","text":"final "}}""",
                """{"type":"content_block_delta","delta":{"type":"text_delta","text":"21"}}""",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20,"thinking_tokens":14}}""",
                """{"type":"message_stop"}""");
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://claude-proxy.test", "proxy-token", "claude-sonnet-4-6", "q", CancellationToken.None,
            observeReasoningTokens: v => tokens = v, stream: true, protocol: ApiProtocols.Anthropic);
        Assert.Equal("final 21", text);
        Assert.Equal(14, tokens);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GeminiNativeUsesXGoogApiKeyMapsRolesSkipsThoughtsAndReadsThoughtsTokenCount()
    {
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent", request.RequestUri!.AbsoluteUri);
            Assert.DoesNotContain("gemini-secret", request.RequestUri.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("gemini-secret", request.Headers.GetValues("x-goog-api-key").Single());
            var contents = body.GetProperty("contents");
            Assert.Equal(3, contents.GetArrayLength());
            Assert.Equal("user", contents[0].GetProperty("role").GetString());
            Assert.Equal("model", contents[1].GetProperty("role").GetString());
            Assert.Equal("user", contents[2].GetProperty("role").GetString());
            Assert.Equal("question", contents[2].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(16384, body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    candidates = new[]
                    {
                        new
                        {
                            content = new
                            {
                                role = "model",
                                parts = new object[]
                                {
                                    new { thought = true, text = "internal reasoning 999" },
                                    new { thought = false, text = "answer: " },
                                    new { text = "21" }
                                }
                            },
                            finishReason = "STOP"
                        }
                    },
                    usageMetadata = new { promptTokenCount = 12, candidatesTokenCount = 10, thoughtsTokenCount = 245, totalTokenCount = 267 }
                })
            };
        });
        long? reasoningTokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://generativelanguage.googleapis.com", "gemini-secret", "gemini-2.5-pro", "question", CancellationToken.None,
            reasoningEffort: "high", observeReasoningTokens: v => reasoningTokens = v);
        Assert.Equal("answer: 21", text);
        Assert.Equal(245, reasoningTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("gemini-2.5-flash", "none", 0, null)]
    [InlineData("gemini-2.5-pro", "medium", 4096, null)]
    [InlineData("gemini-3-pro", "minimal", null, "MINIMAL")]
    [InlineData("gemini-3-pro", "xhigh", null, "HIGH")]
    [InlineData("gemini-4-pro", "xhigh", null, "XHIGH")]
    [InlineData("gemini-4-pro", "none", 0, null)]
    public async Task GeminiMapsThinkingConfigByModelGeneration(string model, string effort, int? expectedBudget, string? expectedLevel)
    {
        var handler = new DelegateHandler((_, body) =>
        {
            var config = body.GetProperty("generationConfig").GetProperty("thinkingConfig");
            if (expectedBudget is int budget) Assert.Equal(budget, config.GetProperty("thinkingBudget").GetInt32());
            if (expectedLevel is not null) Assert.Equal(expectedLevel, config.GetProperty("thinkingLevel").GetString());
            return GeminiOk("ok", thoughtsTokenCount: 0);
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://relay.test", "secret", model, "q", CancellationToken.None,
            reasoningEffort: effort, observeReasoningTokens: v => tokens = v, protocol: ApiProtocols.Gemini);
        Assert.Equal("ok", text);
        Assert.Equal(0L, tokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GeminiStreamingSupportsSseAndJsonChunkArrayFallback(bool sse)
    {
        var handler = new DelegateHandler((request, _) =>
        {
            Assert.Equal("https://relay.test/v1beta/models/gemini-3-flash:streamGenerateContent?alt=sse", request.RequestUri!.AbsoluteUri);
            if (sse)
            {
                return Sse(
                    """{"candidates":[{"content":{"role":"model","parts":[{"thought":true,"text":"thinking..."}]}}]}""",
                    """{"candidates":[{"content":{"role":"model","parts":[{"text":"<svg>"}]}}]}""",
                    """{"candidates":[{"content":{"role":"model","parts":[{"text":"<circle/></svg>"}]},"finishReason":"STOP"}],"usageMetadata":{"thoughtsTokenCount":512}}""");
            }
            var chunks = """
                [
                  {"candidates":[{"content":{"role":"model","parts":[{"thought":true,"text":"thinking..."}]}}]},
                  {"candidates":[{"content":{"role":"model","parts":[{"text":"<svg>"}]}}]},
                  {"candidates":[{"content":{"role":"model","parts":[{"text":"<circle/></svg>"}]},"finishReason":"STOP"}],"usageMetadata":{"thoughtsTokenCount":512}}
                ]
                """;
            return new(HttpStatusCode.OK) { Content = new StringContent(chunks, Encoding.UTF8, "application/json") };
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://relay.test", "secret", "gemini-3-flash", "draw", CancellationToken.None,
            observeReasoningTokens: v => tokens = v, stream: true, protocol: ApiProtocols.Gemini);
        Assert.Equal("<svg><circle/></svg>", text);
        Assert.Equal(512, tokens);
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""", "SAFETY")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"partial"}]},"finishReason":"MAX_TOKENS"}]}""", "MAX_TOKENS")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""", "SAFETY")]
    public async Task GeminiRejectsBlockedOrTruncatedResponses(string json, string expected)
    {
        var handler = new DelegateHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<UpstreamException>(() => new ModelTraceClient(new HttpClient(handler))
            .Completion("https://relay.test", "secret", "gemini-2.5-pro", "q", CancellationToken.None, protocol: ApiProtocols.Gemini));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task AutoModeFallsBackThroughOpenAiAndAnthropicToGeminiEvenWithEffortOn404()
    {
        var paths = new List<string>();
        var handler = new DelegateHandler((request, body) =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            if (!request.RequestUri.AbsolutePath.Contains(":streamGenerateContent"))
                return new(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
            Assert.Equal("MEDIUM", body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
            return Sse("""{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}],"usageMetadata":{"thoughtsTokenCount":77}}""");
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://gemini-only.test", "secret", "gemini-3-pro", "q", CancellationToken.None,
            reasoningEffort: "medium", observeReasoningTokens: v => tokens = v, stream: true, protocol: ApiProtocols.Auto);
        Assert.Equal("ok", text);
        Assert.Equal(77, tokens);
        Assert.Equal(["/v1/responses", "/v1/chat/completions", "/v1/messages?beta=true", "/v1beta/models/gemini-3-pro:streamGenerateContent?alt=sse"], paths);
    }

    [Fact]
    public async Task GeminiStatisticalHlwyAndCandyWorkEndToEnd()
    {
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.StartsWith("https://relay.test/v1beta/models/gemini-2.5-flash:generateContent", request.RequestUri!.AbsoluteUri);
            var prompt = body.GetProperty("contents").EnumerateArray().Last().GetProperty("parts")[0].GetProperty("text").GetString()!;
            if (CandyTestPrompts.IsCandy(prompt))
            {
                Assert.Equal(4096, body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
                return GeminiOk("21", thoughtsTokenCount: 88);
            }
            Assert.Equal(1.0, body.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
            Assert.Equal(256, body.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
            return GeminiOk("42");
        });
        var result = await new ModelTraceClient(new HttpClient(handler)).TestHlwy(
            "https://relay.test", "secret", "gemini-2.5-flash",
            new AppSettings { HlwySampleCount = 40, CandyReasoningEffort = "medium" },
            CancellationToken.None, protocol: ApiProtocols.Gemini);
        Assert.Equal(40, result.GetProperty("result").GetProperty("UsedOutputs").GetInt32());
        Assert.Equal("gemini", result.GetProperty("result").GetProperty("Baseline").GetProperty("apiType").GetString());
        Assert.Equal("Success", result.GetProperty("candy").GetProperty("CandyStatus").GetString());
        Assert.Equal(88, result.GetProperty("candy").GetProperty("CandyReasoningTokens").GetInt64());
        Assert.Equal(43, handler.Calls);
    }

    [Fact]
    public async Task ModelDiscoverySupportsAnthropicPaginationAndBearerFallback()
    {
        var calls = 0;
        var handler = new RawHandler(request =>
        {
            calls++;
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            if (calls == 1)
            {
                Assert.Equal("proxy-key", request.Headers.GetValues("x-api-key").Single());
                return new(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") };
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.RequestUri!.Query == "")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "claude-opus-5-5" } }, has_more = true, last_id = "claude-opus-5-5" }) };
            Assert.Equal("?after_id=claude-opus-5-5", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "claude-sonnet-4-6" } }, has_more = false }) };
        });
        var models = await new ModelDiscovery(new HttpClient(handler)).Fetch("https://claude-relay.test", "proxy-key", CancellationToken.None, ApiProtocols.Anthropic);
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-4-6"], models);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ModelDiscoverySupportsGeminiPaginationPrefixStrippingAndFiltering()
    {
        var calls = 0;
        var handler = new RawHandler(request =>
        {
            calls++;
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("AIza-test", request.Headers.GetValues("x-goog-api-key").Single());
            Assert.DoesNotContain("AIza-test", request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Query == "")
            {
                Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models", request.RequestUri.AbsoluteUri);
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        models = new object[]
                        {
                            new { name = "models/gemini-2.5-pro", supportedGenerationMethods = new[] { "generateContent", "countTokens" } },
                            new { name = "models/text-embedding-004", supportedGenerationMethods = new[] { "embedContent" } }
                        },
                        nextPageToken = "page-2"
                    })
                };
            }
            Assert.Equal("?pageToken=page-2", request.RequestUri.Query);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    models = new object[]
                    {
                        new { name = "models/gemini-3-flash" }
                    }
                })
            };
        });
        var models = await new ModelDiscovery(new HttpClient(handler)).Fetch("https://generativelanguage.googleapis.com", "AIza-test", CancellationToken.None);
        Assert.Equal(["gemini-2.5-pro", "gemini-3-flash"], models);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ModelDiscoveryAutoFallsBackToGemini()
    {
        var paths = new List<string>();
        var handler = new RawHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1beta/models")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { models = new[] { new { name = "models/gemini-2.5-flash", supportedGenerationMethods = new[] { "generateContent" } } } }) };
            return new(HttpStatusCode.NotFound);
        });
        var models = await new ModelDiscovery(new HttpClient(handler)).Fetch("https://relay.test", "secret", CancellationToken.None, ApiProtocols.Auto);
        Assert.Equal(["gemini-2.5-flash"], models);
        Assert.Equal(["/v1/models", "/v1/models", "/v1beta/models"], paths);
    }

    [Fact]
    public async Task WebApiUpgradesLegacyDbPersistsProtocolsAndRoutesWorkersByEffectiveProtocol()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-protocols-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "test.db")}";
        await using (var legacy = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(connection).Options))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Sites.Add(new Site { Name = "legacy", BaseUrl = "https://legacy.test", Keys = [new SiteKey { Name = "k", Models = [new MonitoredModel { Name = "old-model" }] }] });
            await legacy.SaveChangesAsync();
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Sites DROP COLUMN Protocol");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN Protocol");
        }

        var recordedPaths = new List<string>();
        var sync = new object();
        var probeHandler = new RawHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (sync) recordedPaths.Add(path);
            if (path.Contains("/messages"))
            {
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                var prompt = TestConversation.Prompt(body);
                var reply = CandyTestPrompts.IsCandy(prompt) ? "21" : string.Join(',', Enumerable.Range(1, 310));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { content = new[] { new { type = "text", text = reply } }, stop_reason = "end_turn" }) };
            }
            if (path.Contains(":generateContent") || path.Contains(":streamGenerateContent"))
            {
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                var prompt = body.GetProperty("contents").EnumerateArray().Last().GetProperty("parts")[0].GetProperty("text").GetString()!;
                if (path.Contains(":streamGenerateContent"))
                    return Sse("""{"candidates":[{"content":{"parts":[{"text":"<svg xmlns=\"http://www.w3.org/2000/svg\"><circle cx=\"5\" cy=\"5\" r=\"4\"/></svg>"}]},"finishReason":"STOP"}],"usageMetadata":{"thoughtsTokenCount":99}}""");
                var reply = CandyTestPrompts.IsCandy(prompt) ? "21" : string.Join(',', Enumerable.Range(1, 310));
                return GeminiOk(reply, thoughtsTokenCount: 66);
            }
            return new(HttpStatusCode.NotFound);
        });
        var discoveryHandler = new RawHandler(request =>
        {
            Assert.Equal("/v1beta/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("gemini-key", request.Headers.GetValues("x-goog-api-key").Single());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { models = new[] { new { name = "models/gemini-2.5-pro", supportedGenerationMethods = new[] { "generateContent" } } } }) };
        });

        WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["ConnectionStrings:Default"] = connection,
                ["Admin:Password"] = "test-password-123!",
                ["Desktop:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => probeHandler);
                services.AddHttpClient<ModelDiscovery>().ConfigurePrimaryHttpMessageHandler(() => discoveryHandler);
            });
        });

        int relaySiteId, claudeModelId, geminiModelId;
        await using (var factory = CreateFactory())
        {
            using var client = factory.CreateClient();
            await Login(client);
            var existingSites = await client.GetFromJsonAsync<JsonElement>("/api/sites");
            Assert.Equal("auto", existingSites[0].GetProperty("protocol").GetString());
            Assert.Equal(JsonValueKind.Null, existingSites[0].GetProperty("keys")[0].GetProperty("models")[0].GetProperty("protocol").ValueKind);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/sites", new { name = "bad", baseUrl = "https://bad.test", protocol = "invalid" })).StatusCode);
            var geminiSite = await (await client.PostAsJsonAsync("/api/sites", new { name = "Gemini Direct", baseUrl = "https://relay-gemini.test", protocol = "gemini" })).Content.ReadFromJsonAsync<JsonElement>();
            var geminiKey = await (await client.PostAsJsonAsync($"/api/sites/{geminiSite.GetProperty("id")}/keys", new { name = "k", value = "gemini-key" })).Content.ReadFromJsonAsync<JsonElement>();
            var discovered = await (await client.PostAsJsonAsync($"/api/keys/{geminiKey.GetProperty("id")}/discover-models", new { })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["gemini-2.5-pro"], discovered.GetProperty("models").EnumerateArray().Select(m => m.GetString()!).ToArray());

            relaySiteId = (await (await client.PostAsJsonAsync("/api/sites", new { name = "Multi Relay", baseUrl = "https://relay.test", protocol = "anthropic" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            var relayKey = (await (await client.PostAsJsonAsync($"/api/sites/{relaySiteId}/keys", new { name = "relay-key", value = "relay-secret" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            claudeModelId = (await (await client.PostAsJsonAsync($"/api/keys/{relayKey}/models", new { name = "claude-sonnet-4-6" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            geminiModelId = (await (await client.PostAsJsonAsync($"/api/keys/{relayKey}/models", new { name = "gemini-2.5-pro", protocol = "gemini" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/models/{claudeModelId}/protocol", new { protocol = "nope" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/sites/{relaySiteId}", new { name = "Multi Relay", baseUrl = "https://relay.test", protocol = "openai" })).StatusCode);
            var batchUpdate = await client.PostAsJsonAsync("/api/models/batch-protocol", new { ids = new[] { claudeModelId }, protocol = "anthropic" });
            batchUpdate.EnsureSuccessStatusCode();
            Assert.Equal(1, (await batchUpdate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());

            var detect = await client.PostAsJsonAsync("/api/detect", new { siteId = relaySiteId });
            Assert.Equal(HttpStatusCode.Accepted, detect.StatusCode);
            var runId = (await detect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            JsonElement results;
            while ((results = await client.GetFromJsonAsync<JsonElement>($"/api/results?runId={runId}", deadline.Token)).GetArrayLength() < 2)
                await Task.Delay(50, deadline.Token);
            Assert.All(results.EnumerateArray(), r => Assert.Equal("Success", r.GetProperty("status").GetString()));
            var geminiResult = results.EnumerateArray().Single(r => r.GetProperty("modelId").GetInt32() == geminiModelId);
            Assert.Equal(66, geminiResult.GetProperty("candyReasoningTokens").GetInt64());

            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/models/{geminiModelId}/crane-riding", new { })).StatusCode);
            JsonElement craneHistory;
            while ((craneHistory = await client.GetFromJsonAsync<JsonElement>($"/api/models/{geminiModelId}/crane-rides", deadline.Token)).GetArrayLength() == 0 ||
                   craneHistory[0].GetProperty("status").GetString() is "Queued" or "Running")
                await Task.Delay(50, deadline.Token);
            Assert.Equal("Success", craneHistory[0].GetProperty("status").GetString());
            Assert.Equal(99, craneHistory[0].GetProperty("reasoningTokens").GetInt64());
        }

        // Verify persistence across restart.
        await using (var restarted = CreateFactory())
        {
            using var client = restarted.CreateClient();
            await Login(client);
            var sites = await client.GetFromJsonAsync<JsonElement>("/api/sites");
            var relay = sites.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == relaySiteId);
            Assert.Equal("openai", relay.GetProperty("protocol").GetString());
            var models = relay.GetProperty("keys")[0].GetProperty("models").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetInt32(), m => m.GetProperty("protocol").GetString());
            Assert.Equal("anthropic", models[claudeModelId]);
            Assert.Equal("gemini", models[geminiModelId]);
        }

        Assert.Contains("/v1/messages?beta=true", recordedPaths);
        Assert.Contains("/v1beta/models/gemini-2.5-pro:generateContent", recordedPaths);
        Assert.Contains("/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse", recordedPaths);
    }

    [Fact]
    public async Task CodexResponsesSendsFullCliHeadersAndBodyAndStreamsOutput()
    {
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("sk-codex-test", request.Headers.Authorization.Parameter);
            Assert.Equal(ModelTraceClient.CodexUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal(ModelTraceClient.CodexOriginator, request.Headers.GetValues("originator").Single());
            Assert.Equal(ModelTraceClient.CodexOpenAiBeta, request.Headers.GetValues("OpenAI-Beta").Single());
            var sessionId = request.Headers.GetValues("session_id").Single();
            Assert.True(Guid.TryParse(sessionId, out _));
            Assert.Equal(sessionId, request.Headers.GetValues("conversation_id").Single());
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");

            Assert.Equal("gpt-5.4", body.GetProperty("model").GetString());
            Assert.Equal(ModelTraceClient.CodexInstructions, body.GetProperty("instructions").GetString());
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            var input = body.GetProperty("input");
            Assert.Equal(3, input.GetArrayLength());
            Assert.Equal("message", input[0].GetProperty("type").GetString());
            Assert.Equal("user", input[0].GetProperty("role").GetString());
            Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
            Assert.Equal("assistant", input[1].GetProperty("role").GetString());
            Assert.Equal("output_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
            Assert.Equal("user", input[2].GetProperty("role").GetString());
            Assert.Equal("input_text", input[2].GetProperty("content")[0].GetProperty("type").GetString());
            Assert.Equal("question", input[2].GetProperty("content")[0].GetProperty("text").GetString());

            return Sse(
                """{"type":"response.created","response":{"id":"resp_1","status":"in_progress","error":null}}""",
                """{"type":"response.reasoning_summary_text.delta","delta":"internal reasoning"}""",
                """{"type":"response.output_text.delta","delta":"2"}""",
                """{"type":"response.output_text.delta","delta":"1"}""",
                """{"type":"response.completed","response":{"id":"resp_1","status":"completed","error":null,"usage":{"input_tokens":15,"output_tokens":80,"output_tokens_details":{"reasoning_tokens":64}}}}""");
        });
        long? reasoningTokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://api.openai.com", "sk-codex-test", "gpt-5.4", "question", CancellationToken.None,
            reasoningEffort: "high", observeReasoningTokens: v => reasoningTokens = v);
        Assert.Equal("21", text);
        Assert.Equal(64, reasoningTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "method not allowed")]
    [InlineData(HttpStatusCode.NotImplemented, "not implemented")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid URL (POST /v1/responses)"}}""")]
    public async Task CodexFallsBackToChatCompletionsOnUnsupportedResponsesEndpoint(HttpStatusCode status, string errorBody)
    {
        var paths = new List<string>();
        string? firstSession = null;
        var handler = new DelegateHandler((request, body) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(ModelTraceClient.CodexUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal(ModelTraceClient.CodexOriginator, request.Headers.GetValues("originator").Single());
            var session = request.Headers.GetValues("session_id").Single();
            firstSession ??= session;
            Assert.Equal(firstSession, session);
            if (request.RequestUri.AbsolutePath == "/v1/responses")
            {
                Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
                return new(status) { Content = new StringContent(errorBody, Encoding.UTF8, "application/json") };
            }
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            Assert.False(request.Headers.Contains("OpenAI-Beta"));
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Assert.Equal("question", TestConversation.Prompt(body));
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { message = new { content = "21" }, finish_reason = "stop" } },
                    usage = new { completion_tokens_details = new { reasoning_tokens = 48 } }
                })
            };
        });
        long? tokens = null;
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://legacy-openai.test", "secret", "gpt-5.4", "question", CancellationToken.None,
            reasoningEffort: "high", observeReasoningTokens: v => tokens = v, protocol: ApiProtocols.OpenAi);
        Assert.Equal("21", text);
        Assert.Equal(48, tokens);
        Assert.Equal(["/v1/responses", "/v1/chat/completions"], paths);
    }

    [Fact]
    public async Task CodexRespectsExplicitChatCompletionsOrResponsesEndpointsWithoutCrossFallback()
    {
        var chatPaths = new List<string>();
        var chatHandler = new DelegateHandler((request, body) =>
        {
            chatPaths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(ModelTraceClient.CodexUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal("medium", body.GetProperty("reasoning_effort").GetString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "ok" }, finish_reason = "stop" } } }) };
        });
        Assert.Equal("ok", await new ModelTraceClient(new HttpClient(chatHandler)).Completion(
            "https://relay.test/v1/chat/completions", "secret", "gpt-5.4", "q", CancellationToken.None, reasoningEffort: "medium"));
        Assert.Equal(["/v1/chat/completions"], chatPaths);

        var responsesPaths = new List<string>();
        var responsesHandler = new DelegateHandler((request, _) =>
        {
            responsesPaths.Add(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.NotFound) { Content = new StringContent("missing") };
        });
        await Assert.ThrowsAsync<UpstreamException>(() => new ModelTraceClient(new HttpClient(responsesHandler)).Completion(
            "https://relay.test/v1/responses", "secret", "gpt-5.4", "q", CancellationToken.None, protocol: ApiProtocols.OpenAi));
        Assert.Equal(["/v1/responses"], responsesPaths);
    }

    [Fact]
    public async Task CodexDoesNotFallBackWhenExplicitEffortIsRejectedOnResponses()
    {
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("xhigh", body.GetProperty("reasoning").GetProperty("effort").GetString());
            return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { message = "Unsupported parameter: reasoning.effort" } }) };
        });
        var error = await Assert.ThrowsAsync<UpstreamException>(() => new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://codex.test", "secret", "gpt-5.4", "q", CancellationToken.None, reasoningEffort: "xhigh", protocol: ApiProtocols.OpenAi));
        Assert.Contains("reasoning.effort", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CodexStatisticalRetriesWithoutSamplingParamsWhenStrictBackendRejectsThem()
    {
        var seenTemperature = new List<bool>();
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            var hasTemp = body.TryGetProperty("temperature", out _);
            seenTemperature.Add(hasTemp);
            if (hasTemp)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { message = "Unsupported parameter: 'temperature' is not supported with this model." } }) };
            Assert.False(body.TryGetProperty("max_output_tokens", out _));
            return Sse(
                """{"type":"response.output_text.delta","delta":"42"}""",
                """{"type":"response.completed","response":{"status":"completed"}}""");
        });
        var text = await new ModelTraceClient(new HttpClient(handler)).Completion(
            "https://codex.test", "secret", "gpt-5.4", "pick", CancellationToken.None, statistical: true, protocol: ApiProtocols.OpenAi);
        Assert.Equal("42", text);
        Assert.Equal([true, false], seenTemperature);
    }

    [Fact]
    public async Task CodexJuiceSupportsResponsesSseAndRemembersChatCompletionsFallback()
    {
        var sseHandler = new DelegateHandler((request, _) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            return Sse(
                """{"type":"response.output_text.delta","delta":"96"}""",
                """{"type":"response.completed","response":{"status":"completed"}}""");
        });
        var sseResult = await new ModelTraceClient(new HttpClient(sseHandler)).ProbeJuice(
            "https://codex.test", "secret", "gpt-5.4", CancellationToken.None, "direct", protocol: ApiProtocols.OpenAi);
        Assert.Equal("Success", sseResult["juice_status"]);
        Assert.Equal(96, sseResult["juice_value"]);
        Assert.Equal(1, sseHandler.Calls);

        var paths = new List<string>();
        var fallbackHandler = new DelegateHandler((request, body) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/responses")
                return new(HttpStatusCode.NotFound) { Content = new StringContent("no responses") };
            var prompt = TestConversation.Prompt(body);
            var reply = prompt == JuiceProbe.Prompt ? "64" : "I cannot share that.";
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = reply }, finish_reason = "stop" } } }) };
        });
        var fallbackResult = await new ModelTraceClient(new HttpClient(fallbackHandler)).ProbeJuice(
            "https://legacy-openai.test", "secret", "gpt-5.4", CancellationToken.None, protocol: ApiProtocols.OpenAi);
        Assert.Equal("Success", fallbackResult["juice_status"]);
        Assert.Equal(64, fallbackResult["juice_value"]);
        // First method tries /v1/responses -> 404 -> /v1/chat/completions; second method goes directly to /v1/chat/completions.
        Assert.Equal(["/v1/responses", "/v1/chat/completions", "/v1/chat/completions"], paths);
    }

    [Fact]
    public async Task ClaudeCodeUsesOAuthBetaForOAuthTokensAndSendsCliHeadersOnModelDiscovery()
    {
        var completionHandler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages?beta=true", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("sk-ant-oat01-secret", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal(ModelTraceClient.ClaudeCodeOAuthBeta, request.Headers.GetValues("anthropic-beta").Single());
            Assert.Equal(ModelTraceClient.ClaudeCodeUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal("cli", request.Headers.GetValues("x-app").Single());
            Assert.True(body.GetProperty("stream").GetBoolean());
            return Sse(
                """{"type":"message_start","message":{"usage":{"input_tokens":10}}}""",
                """{"type":"content_block_delta","delta":{"type":"text_delta","text":"ok"}}""",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
                """{"type":"message_stop"}""");
        });
        Assert.Equal("ok", await new ModelTraceClient(new HttpClient(completionHandler)).Completion(
            "https://api.anthropic.com", "sk-ant-oat01-secret", "claude-sonnet-4-6", "q", CancellationToken.None));

        var discoveryHandler = new RawHandler(request =>
        {
            Assert.Equal(ModelTraceClient.ClaudeCodeUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal("cli", request.Headers.GetValues("x-app").Single());
            Assert.Equal(ModelTraceClient.ClaudeCodeOAuthBeta, request.Headers.GetValues("anthropic-beta").Single());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "claude-sonnet-4-6" } }, has_more = false }) };
        });
        var models = await new ModelDiscovery(new HttpClient(discoveryHandler)).Fetch(
            "https://api.anthropic.com", "sk-ant-oat01-secret", CancellationToken.None, ApiProtocols.Anthropic);
        Assert.Equal(["claude-sonnet-4-6"], models);
    }

    private static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events.Select(e => $"data: {e}\n\n")), Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage GeminiOk(string text, long? thoughtsTokenCount = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["candidates"] = new[]
            {
                new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = "STOP" }
            }
        };
        if (thoughtsTokenCount is not null)
            payload["usageMetadata"] = new { promptTokenCount = 10, candidatesTokenCount = 5, thoughtsTokenCount = thoughtsTokenCount.Value, totalTokenCount = 15 + thoughtsTokenCount.Value };
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static async Task Login(HttpClient client)
    {
        async Task Csrf()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await Csrf();
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).EnsureSuccessStatusCode();
        await Csrf();
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, JsonElement, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return respond(request, doc.RootElement.Clone());
        }
    }

    private sealed class RawHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }
}
