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
    [InlineData("https://generativelanguage.googleapis.com", "gemini")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "gemini")]
    [InlineData("https://relay.test/v1beta/models/gemini-2.5-pro:generateContent", "gemini")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "openai")]
    [InlineData("https://relay.test/v1/chat/completions", "openai")]
    [InlineData("https://relay.test", null)]
    public void InfersProtocolFromKnownUrls(string url, string? expected) =>
        Assert.Equal(expected, ApiProtocols.InferFromUrl(url));

    [Theory]
    [InlineData("https://api.anthropic.com", "anthropic", "claude-sonnet-4-6", false, "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1/", "anthropic", "claude-sonnet-4-6", true, "https://api.anthropic.com/v1/messages")]
    [InlineData("https://relay.test/v1/chat/completions", "anthropic", "claude-sonnet-4-6", false, "https://relay.test/v1/messages")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini", "gemini-2.5-pro", false, "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini", "models/gemini-2.5-pro", true, "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse")]
    [InlineData("https://generativelanguage.googleapis.com/v1/", "gemini", "gemini-3-pro", false, "https://generativelanguage.googleapis.com/v1/models/gemini-3-pro:generateContent")]
    [InlineData("https://relay.test/v1beta/models", "gemini", "gemini-2.5-flash", false, "https://relay.test/v1beta/models/gemini-2.5-flash:generateContent")]
    [InlineData("https://relay.test/v1beta/models/gemini-2.5-pro:generateContent", "gemini", "gemini-2.5-pro", true, "https://relay.test/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "openai", "gemini-2.5-pro", false, "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    public void BuildsCompletionUrlsForEachProtocol(string baseUrl, string protocol, string model, bool stream, string expected) =>
        Assert.Equal(expected, ModelTraceClient.CompletionUrl(baseUrl, protocol, model, stream));

    [Theory]
    [InlineData("https://api.anthropic.com", "anthropic", "https://api.anthropic.com/v1/models")]
    [InlineData("https://api.anthropic.com/v1/messages", "anthropic", "https://api.anthropic.com/v1/models")]
    [InlineData("https://generativelanguage.googleapis.com", "gemini", "https://generativelanguage.googleapis.com/v1beta/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1/", "gemini", "https://generativelanguage.googleapis.com/v1/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent", "gemini", "https://generativelanguage.googleapis.com/v1beta/models")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "openai", "https://generativelanguage.googleapis.com/v1beta/openai/models")]
    public void BuildsModelsUrlsForEachProtocol(string baseUrl, string protocol, string expected) =>
        Assert.Equal(expected, ModelDiscovery.ModelsUrl(baseUrl, protocol));

    [Fact]
    public async Task AnthropicNativeUsesXApiKeyWithoutBearerAndFiltersThinkingBlocks()
    {
        var handler = new DelegateHandler((request, body) =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("sk-ant-test", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.Equal("claude-sonnet-4-6", body.GetProperty("model").GetString());
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
        Assert.Equal(["/v1/chat/completions", "/v1/messages", "/v1beta/models/gemini-3-pro:streamGenerateContent?alt=sse"], paths);
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
            if (path.EndsWith("/messages"))
            {
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                var prompt = body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
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

        Assert.Contains("/v1/messages", recordedPaths);
        Assert.Contains("/v1beta/models/gemini-2.5-pro:generateContent", recordedPaths);
        Assert.Contains("/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse", recordedPaths);
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
