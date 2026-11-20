using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueModel;

// Native port of ModelTrace enrollment.py's completion adapter with OpenAI, Anthropic (Claude), and Gemini protocol support.
public sealed partial class ModelTraceClient(HttpClient client, JuiceHistory? juiceHistory = null)
{
    public const string DefaultUserAgent = "Codex Desktop/0.147.0-alpha.1.2 (Windows 10.0.26200; x86_64) unknown (codex_exec; 0.147.0-alpha.1.2)";
    private static readonly int[] Retryable = [408, 429, 500, 502, 503, 504];

    public static string CompletionUrl(string url, bool anthropic) =>
        CompletionUrl(url, anthropic ? ApiProtocols.Anthropic : ApiProtocols.OpenAi);

    public static string CompletionUrl(string url, string protocol, string? model = null, bool stream = false)
    {
        var normalized = url.TrimEnd('/');
        if (protocol == ApiProtocols.Gemini)
        {
            var action = stream ? ":streamGenerateContent?alt=sse" : ":generateContent";
            if (normalized.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase))
                return stream ? normalized[..^":generateContent".Length] + action : normalized;
            if (normalized.EndsWith(":streamGenerateContent", StringComparison.OrdinalIgnoreCase))
                return stream ? normalized + "?alt=sse" : normalized[..^":streamGenerateContent".Length] + action;
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^"/chat/completions".Length];
            else if (normalized.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^"/messages".Length];
            var escapedModel = Uri.EscapeDataString(ApiProtocols.StripGeminiModelPrefix(model ?? ""));
            if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return $"{normalized}/{escapedModel}{action}";
            if (normalized.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("/v1alpha", StringComparison.OrdinalIgnoreCase))
                return $"{normalized}/models/{escapedModel}{action}";
            return $"{normalized}/v1beta/models/{escapedModel}{action}";
        }
        if (protocol == ApiProtocols.Anthropic)
        {
            if (normalized.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) return normalized;
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return normalized[..^"/chat/completions".Length] + "/messages";
            return normalized + (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "" : "/v1") + "/messages";
        }
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return normalized;
        if (normalized.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return normalized[..^"/messages".Length] + "/chat/completions";
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            return normalized + "/chat/completions";
        return normalized + "/v1/chat/completions";
    }

    public async Task<string> Completion(string url, string key, string model, string prompt, CancellationToken token, Action<int>? observeStatus = null,
        string? reasoningEffort = null, Action<long?>? observeReasoningTokens = null, bool statistical = false, TimeSpan? timeout = null, int? maxTokens = null, int maxAttempts = 3, bool stream = false,
        ConversationMessage[]? conversation = null, string protocol = ApiProtocols.Auto)
    {
        var errors = new List<string>();
        var messages = conversation ?? ConversationEnvelope.Create(prompt);
        var candidates = ApiProtocols.Candidates(url, protocol);
        var canFallbackToGemini = candidates.Length == 1 || model.Contains("gemini", StringComparison.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate == ApiProtocols.Gemini && !canFallbackToGemini) continue;
            try
            {
                return await Request(url, key, model, messages, candidate, token, observeStatus, maxAttempts,
                    reasoningEffort: reasoningEffort, observeReasoningTokens: observeReasoningTokens, statistical: statistical,
                    timeout: timeout, maxTokens: maxTokens, stream: stream);
            }
            // A broken stream after a 2xx response, an explicit single protocol, or an explicit effort rejected by an existing endpoint should not silently try another protocol.
            catch (UpstreamException e) when (candidates.Length == 1 || e.ProtocolAccepted || (reasoningEffort is not null && e.StatusCode is not (404 or 405 or 501)))
            {
                throw new UpstreamException($"{candidate}: {e.Message}", protocolAccepted: e.ProtocolAccepted, statusCode: e.StatusCode);
            }
            catch (UpstreamException e)
            {
                if (e.StatusCode is 404 or 405 or 501) canFallbackToGemini = true;
                errors.Add($"{candidate}: {e.Message}");
            }
        }
        throw new UpstreamException("接口格式自动探测失败；" + string.Join('；', errors));
    }

    private async Task<string> Request(string url, string key, string model, ConversationMessage[] messages, string protocol, CancellationToken token, Action<int>? observeStatus, int maxAttempts = 3, bool juice = false,
        string? reasoningEffort = null, Action<long?>? observeReasoningTokens = null, bool statistical = false, Action<string, string>? observeJuiceFailure = null, TimeSpan? timeout = null, int? maxTokens = null, bool stream = false)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(240);
        var useBearerAuth = protocol == ApiProtocols.OpenAi ||
            (protocol == ApiProtocols.Anthropic && key.StartsWith("sk-ant-oat", StringComparison.OrdinalIgnoreCase));
        var authSwitched = false;
        var retries = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(limit);
            using var request = BuildRequest(url, key, model, messages, protocol, reasoningEffort, statistical, maxTokens, stream, useBearerAuth);
            try
            {
                using var response = await client.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, deadline.Token);
                var code = (int)response.StatusCode;
                observeStatus?.Invoke(code);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    return await ReadEventStream(response, protocol, observeReasoningTokens, deadline.Token);
                var text = await response.Content.ReadAsStringAsync(deadline.Token);
                if (!response.IsSuccessStatusCode)
                {
                    // Support proxies that require Authorization: Bearer on Anthropic/Gemini endpoints instead of x-api-key / x-goog-api-key (and vice versa).
                    if (code == 401 && protocol is ApiProtocols.Anthropic or ApiProtocols.Gemini && !authSwitched)
                    {
                        authSwitched = true;
                        useBearerAuth = !useBearerAuth;
                        attempt--;
                        continue;
                    }
                    if (attempt < maxAttempts && Retryable.Contains(code)) { retries++; await Delay(attempt, token); continue; }
                    throw new UpstreamException($"HTTP {code}: {CompactError(text)}" + (code == 524 ? "（Cloudflare 等待源站响应超时，通常为 100 秒）" : "") + (retries > 0 ? $"（已自动重试 {retries} 次）" : ""), statusCode: code);
                }
                // Some gateways ignore "stream" and answer with a single JSON body (or a JSON chunk array for Gemini).
                using var payload = JsonDocument.Parse(text);
                if (juice && protocol == ApiProtocols.OpenAi)
                {
                    var choice = payload.RootElement.GetProperty("choices")[0];
                    var message = choice.GetProperty("message");
                    var reply = message.TryGetProperty("content", out var raw) && raw.ValueKind != JsonValueKind.Null ? raw.ToString() : "";
                    if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString()))
                    { observeJuiceFailure?.Invoke("模型拒绝提供 Juice 数值", refusal.GetString()!); return ""; }
                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() is "length" or "content_filter")
                    { observeJuiceFailure?.Invoke(finish.GetString() == "length" ? "回复因输出长度限制被截断（length）" : "回复被内容过滤拦截（content_filter）", reply); return ""; }
                    if (raw.ValueKind == JsonValueKind.Null) return "";
                }
                if (protocol == ApiProtocols.Gemini && payload.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var contentFromChunks = ExtractGeminiChunks(payload.RootElement, out var chunkReasoningTokens);
                    observeReasoningTokens?.Invoke(chunkReasoningTokens);
                    return contentFromChunks;
                }
                var content = ExtractContent(payload.RootElement, protocol);
                observeReasoningTokens?.Invoke(ExtractReasoningTokens(payload.RootElement));
                return content;
            }
            catch (HttpRequestException e)
            {
                if (attempt < maxAttempts) { retries++; await Delay(attempt, token); continue; }
                throw new UpstreamException("无法连接接口：" + e.HttpRequestError);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new UpstreamException($"接口请求超时（{limit.TotalSeconds:0} 秒）"); }
        }
        throw new UpstreamException("无法连接接口");
    }

    private static HttpRequestMessage BuildRequest(string url, string key, string model, ConversationMessage[] messages, string protocol,
        string? reasoningEffort, bool statistical, int? maxTokens, bool stream, bool useBearerAuth)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CompletionUrl(url, protocol, model, stream));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var agent = Environment.GetEnvironmentVariable("MODELTRACE_USER_AGENT") ?? Environment.GetEnvironmentVariable("GPT56_USER_AGENT");
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(agent) ? DefaultUserAgent : agent.Trim());

        if (protocol == ApiProtocols.Anthropic)
        {
            if (useBearerAuth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
            var outputMaxTokens = statistical ? 256 : maxTokens ?? 4096;
            if (statistical) body["temperature"] = 1.0;
            else if (reasoningEffort is not null)
            {
                if (reasoningEffort == "none")
                    body["thinking"] = new Dictionary<string, object> { ["type"] = "disabled" };
                else if (ApiProtocols.UsesLegacyAnthropicThinkingBudget(model))
                {
                    var budget = reasoningEffort switch
                    {
                        "minimal" => 1024,
                        "low" => 2048,
                        "medium" => 4096,
                        "high" => 8192,
                        "xhigh" => 16384,
                        _ => 4096
                    };
                    body["thinking"] = new Dictionary<string, object> { ["type"] = "enabled", ["budget_tokens"] = budget };
                    outputMaxTokens = Math.Max(outputMaxTokens, budget + 4096);
                }
                else
                {
                    var effort = reasoningEffort switch
                    {
                        "minimal" => "low",
                        "low" => "low",
                        "medium" => "medium",
                        "high" => "high",
                        "xhigh" => "max",
                        _ => "medium"
                    };
                    body["thinking"] = new Dictionary<string, object> { ["type"] = "adaptive" };
                    body["output_config"] = new Dictionary<string, object> { ["effort"] = effort };
                    outputMaxTokens = Math.Max(outputMaxTokens, 8192);
                }
            }
            body["max_tokens"] = outputMaxTokens;
            if (stream) body["stream"] = true;
            request.Content = JsonContent.Create(body);
            return request;
        }

        if (protocol == ApiProtocols.Gemini)
        {
            if (useBearerAuth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else request.Headers.Add("x-goog-api-key", key);
            var contents = messages.Select(m => new
            {
                role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user",
                parts = new[] { new { text = m.Content } }
            }).ToArray();
            var body = new Dictionary<string, object> { ["contents"] = contents };
            var generationConfig = new Dictionary<string, object>();
            if (statistical)
            {
                generationConfig["temperature"] = 1.0;
                generationConfig["maxOutputTokens"] = 256;
            }
            else
            {
                if (maxTokens is not null) generationConfig["maxOutputTokens"] = maxTokens.Value;
                if (reasoningEffort is not null)
                {
                    if (ApiProtocols.UsesGeminiThinkingBudget(model))
                    {
                        var budget = reasoningEffort switch
                        {
                            "none" => 0,
                            "minimal" => 512,
                            "low" => 1024,
                            "medium" => 4096,
                            "high" => 16384,
                            "xhigh" => 24576,
                            _ => 4096
                        };
                        generationConfig["thinkingConfig"] = new Dictionary<string, object> { ["thinkingBudget"] = budget };
                    }
                    else if (reasoningEffort == "none")
                    {
                        generationConfig["thinkingConfig"] = new Dictionary<string, object> { ["thinkingBudget"] = 0 };
                    }
                    else
                    {
                        var level = reasoningEffort switch
                        {
                            "minimal" => "MINIMAL",
                            "low" => "LOW",
                            "medium" => "MEDIUM",
                            "high" => "HIGH",
                            "xhigh" => ApiProtocols.UsesGemini3ThinkingLevel(model) ? "HIGH" : "XHIGH",
                            _ => "MEDIUM"
                        };
                        generationConfig["thinkingConfig"] = new Dictionary<string, object> { ["thinkingLevel"] = level };
                    }
                }
            }
            if (generationConfig.Count > 0) body["generationConfig"] = generationConfig;
            request.Content = JsonContent.Create(body);
            return request;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var openAiBody = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
        if (reasoningEffort is not null) openAiBody["reasoning_effort"] = reasoningEffort;
        if (statistical) { openAiBody["temperature"] = 1.0; openAiBody["max_tokens"] = 256; }
        if (stream) openAiBody["stream"] = true;
        request.Content = JsonContent.Create(openAiBody);
        return request;
    }

    // Server-sent events keep long reasoning requests alive behind proxies such as Cloudflare (HTTP 524 after ~100 s idle).
    private static async Task<string> ReadEventStream(HttpResponseMessage response, string protocol, Action<long?>? observeReasoningTokens, CancellationToken token)
    {
        var text = new StringBuilder();
        string? finish = null;
        long? reasoningTokens = null;
        var events = 0;
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;
                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;
                events++;
                if (root.TryGetProperty("error", out _)) throw new UpstreamException("流式响应返回错误：" + CompactError(data), protocolAccepted: true);
                if (protocol == ApiProtocols.Anthropic)
                {
                    var type = root.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                    if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta) &&
                        (!delta.TryGetProperty("type", out var deltaType) || deltaType.GetString() == "text_delta") &&
                        delta.TryGetProperty("text", out var piece) && piece.ValueKind == JsonValueKind.String)
                        text.Append(piece.GetString());
                    else if (type == "message_delta" && root.TryGetProperty("delta", out var summary) && summary.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String)
                        finish = stop.GetString();
                    reasoningTokens = ExtractReasoningTokens(root) ?? reasoningTokens;
                    continue;
                }
                if (protocol == ApiProtocols.Gemini)
                {
                    AppendGeminiChunk(root, text, ref finish);
                    reasoningTokens = ExtractReasoningTokens(root) ?? reasoningTokens;
                    continue;
                }
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String)
                            text.Append(piece.GetString());
                        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finish = reason.GetString();
                    }
                reasoningTokens = ExtractReasoningTokens(root) ?? reasoningTokens;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        { throw new UpstreamException($"流式响应中断（已接收 {text.Length} 个字符）：{e.Message}", protocolAccepted: true); }
        catch (JsonException)
        { throw new UpstreamException("流式响应格式异常，无法解析", protocolAccepted: true); }
        if (protocol == ApiProtocols.Gemini)
        {
            if (!string.IsNullOrWhiteSpace(finish) && finish is not ("STOP" or "FINISH_REASON_UNSPECIFIED"))
                throw new UpstreamException($"回答未正常完成（{finish}），本次回答不计入", protocolAccepted: true);
        }
        else if (finish is "length" or "max_tokens" or "content_filter" or "refusal" or "model_context_window_exceeded")
        {
            throw new UpstreamException($"回答未正常完成（{finish}），本次回答不计入", protocolAccepted: true);
        }
        if (events == 0) throw new UpstreamException("流式响应为空，未收到任何数据", protocolAccepted: true);
        observeReasoningTokens?.Invoke(reasoningTokens);
        return text.ToString();
    }

    private static void AppendGeminiChunk(JsonElement root, StringBuilder text, ref string? finish)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object &&
            feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(block.GetString()))
            finish = block.GetString();
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return;
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object) return;
        if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("text", out var piece) && piece.ValueKind == JsonValueKind.String)
                    text.Append(piece.GetString());
            }
        }
        if (candidate.TryGetProperty("finishReason", out var reason) && reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString()))
            finish = reason.GetString();
    }

    private static string ExtractGeminiChunks(JsonElement chunks, out long? reasoningTokens)
    {
        var text = new StringBuilder();
        string? finish = null;
        reasoningTokens = null;
        var count = 0;
        foreach (var root in chunks.EnumerateArray())
        {
            if (root.ValueKind != JsonValueKind.Object) continue;
            count++;
            if (root.TryGetProperty("error", out _)) throw new UpstreamException("流式响应返回错误：" + CompactError(root.GetRawText()), protocolAccepted: true);
            AppendGeminiChunk(root, text, ref finish);
            reasoningTokens = ExtractReasoningTokens(root) ?? reasoningTokens;
        }
        if (!string.IsNullOrWhiteSpace(finish) && finish is not ("STOP" or "FINISH_REASON_UNSPECIFIED"))
            throw new UpstreamException($"回答未正常完成（{finish}），本次回答不计入", protocolAccepted: true);
        if (count == 0) throw new UpstreamException("响应为空，未收到任何数据", protocolAccepted: true);
        return text.ToString();
    }

    private static Task Delay(int attempt, CancellationToken token) => Task.Delay(TimeSpan.FromSeconds(attempt + RandomNumberGenerator.GetInt32(500001) / 1_000_000d), token);

    public static long? ExtractReasoningTokens(JsonElement payload)
    {
        // Prefer standard named usage fields, including an explicit zero.
        // Only read named usage fields; never search message text or infer from output tokens.
        string[][] paths =
        [
            ["usage", "completion_tokens_details", "reasoning_tokens"],
            ["usage", "output_tokens_details", "reasoning_tokens"],
            ["usage", "reasoning_tokens"],
            ["usage", "reasoning_output_tokens"],
            ["usage", "thinking_tokens"],
            ["message", "usage", "reasoning_tokens"],
            ["message", "usage", "thinking_tokens"],
            ["usageMetadata", "thoughtsTokenCount"],
            ["reasoning_tokens"],
            ["reasoning_output_tokens"]
        ];
        foreach (var path in paths)
        {
            var count = payload;
            foreach (var name in path)
            {
                if (count.ValueKind != JsonValueKind.Object || !count.TryGetProperty(name, out var next))
                { count = default; break; }
                count = next;
            }
            long value;
            if (count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out value) && value >= 0) return value;
            if (count.ValueKind == JsonValueKind.String && long.TryParse(count.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0) return value;
        }
        return null;
    }

    public static string ExtractContent(JsonElement payload, bool anthropic) =>
        ExtractContent(payload, anthropic ? ApiProtocols.Anthropic : ApiProtocols.OpenAi);

    public static string ExtractContent(JsonElement payload, string protocol)
    {
        if (protocol == ApiProtocols.Anthropic)
        {
            var reason = payload.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
            if (reason == "refusal") throw new UpstreamException("模型拒绝生成，本次回答不计入");
            if (reason == "max_tokens") throw new UpstreamException("回答因 max_tokens 截断，本次回答不计入");
            if (reason == "model_context_window_exceeded") throw new UpstreamException("回答超出上下文长度限制（model_context_window_exceeded），本次回答不计入");
            if (!payload.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                throw new UpstreamException("响应缺少 content 数组，本次回答不计入");
            return string.Concat(blocks.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(b => b.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : ""));
        }
        if (protocol == ApiProtocols.Gemini)
        {
            if (payload.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object &&
                feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(block.GetString()))
                throw new UpstreamException($"请求被安全策略拦截（{block.GetString()}），本次回答不计入");
            if (!payload.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                throw new UpstreamException("模型未返回候选回复，本次回答不计入");
            var candidate = candidates[0];
            var finishReason = candidate.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
            if (!string.IsNullOrWhiteSpace(finishReason) && finishReason is not ("STOP" or "FINISH_REASON_UNSPECIFIED"))
                throw new UpstreamException($"回答未正常完成（{finishReason}），本次回答不计入");
            if (!candidate.TryGetProperty("content", out var geminiContent) || geminiContent.ValueKind != JsonValueKind.Object ||
                !geminiContent.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                return "";
            return string.Concat(parts.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && !(p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                .Select(p => p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : ""));
        }
        var choice = payload.GetProperty("choices")[0];
        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
        if (finish is "length" or "content_filter") throw new UpstreamException($"回答未正常完成（{finish}），本次回答不计入");
        var content = choice.GetProperty("message").GetProperty("content");
        return content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : "")) : content.ValueKind == JsonValueKind.Null ? "None" : content.ToString();
    }

    private static string CompactError(string text)
    {
        if (new[] { "cloudflare", "just a moment", "cf-ray", "access denied", "attention required" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return "请求被上游网关拦截（Cloudflare/WAF 拦截页）";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Object)
                root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.ToString() : error.ToString();
        }
        catch (JsonException) { }
        return text[..Math.Min(500, text.Length)];
    }

    public async Task<Dictionary<string, object?>> ProbeJuice(string baseUrl, string key, string model, CancellationToken token, string? methodId = null, string reasoningEffort = "default", string protocol = ApiProtocols.Auto)
    {
        if (!CandyProbe.ValidReasoningEffort(reasoningEffort)) throw new ArgumentException("不支持的 Juice 思考等级", nameof(reasoningEffort));
        var envelope = new Dictionary<string, object?>();
        if (JuiceProbe.Supports(model))
        {
            token.ThrowIfCancellationRequested();
            var effective = ApiProtocols.Candidates(baseUrl, protocol)[0];
            var endpoint = CompletionUrl(baseUrl, effective, model);
            var methods = methodId is not null
                ? new[] { JuiceProbe.Methods.Single(m => m.Id == methodId) }
                : juiceHistory is null ? JuiceProbe.Methods.ToArray() : await juiceHistory.Ranked(endpoint, model, token);
            foreach (var method in methods)
            {
                token.ThrowIfCancellationRequested();
                envelope["juice_prompt"] = method.Prompt;
                envelope["juice_response"] = null;
                envelope["juice_error"] = null;
                string Redact(string text) => string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]", StringComparison.Ordinal);
                string? responseError = null, failedReply = null;
                try
                {
                    var text = await Request(baseUrl, key, model, ConversationEnvelope.Create(method.Prompt), effective, token, null, maxAttempts: 1, juice: true,
                        reasoningEffort: reasoningEffort == "default" ? null : reasoningEffort,
                        observeJuiceFailure: (error, reply) => { responseError = error; failedReply = reply; });
                    var value = JuiceProbe.Parse(text);
                    if (juiceHistory is not null) await juiceHistory.Record(endpoint, model, method.Id, value.HasValue, token);
                    envelope["juice_value"] = value;
                    envelope["juice_status"] = value.HasValue ? "Success" : "Unavailable";
                    envelope["juice_response"] = Redact(failedReply ?? text);
                    envelope["juice_error"] = value.HasValue ? null : responseError ?? (string.IsNullOrWhiteSpace(text)
                        ? "模型返回空回复，未获取到 Juice 数值" : "回复不是有效的非负整数，无法解析 Juice 数值（可能为拒答或包含额外文字）");
                    envelope["juice_method"] = method.Id;
                    if (value.HasValue) break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is UpstreamException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
                {
                    envelope["juice_status"] = "Failed";
                    envelope["juice_value"] = null;
                    envelope["juice_error"] = Redact(e is UpstreamException or HttpRequestException ? e.Message : "接口响应格式异常：" + e.Message);
                    break;
                }
            }
        }
        return envelope;
    }

    public async Task<CandyProbeResult> ProbeCandy(string baseUrl, string key, string model, CancellationToken token, string reasoningEffort = "default",
        string language = "zh", string language2 = "zh", string language3 = "zh", string protocol = ApiProtocols.Auto)
    {
        if (!CandyProbe.ValidReasoningEffort(reasoningEffort)) throw new ArgumentException("不支持的糖果思考等级", nameof(reasoningEffort));
        var firstPrompt = PromptLanguages.Get(language).Candy;
        var secondPrompt = CandyProbe.CreateObfuscatedPrompt(language2);
        var thirdPrompt = CandyProbe.CreateSpaceObfuscatedPrompt(language3);
        var first = await ProbeCandyAttempt(baseUrl, key, model, firstPrompt, token, reasoningEffort, protocol);
        var second = await ProbeCandyAttempt(baseUrl, key, model, secondPrompt, token, reasoningEffort, protocol);
        var third = await ProbeCandyAttempt(baseUrl, key, model, thirdPrompt, token, reasoningEffort, protocol);
        return first with
        {
            Candy2Status = second.CandyStatus,
            Candy2Prompt = second.CandyPrompt,
            Candy2Response = second.CandyResponse,
            Candy2Error = second.CandyError,
            Candy2ReasoningTokens = second.CandyReasoningTokens,
            Candy2LatencyMs = second.CandyLatencyMs,
            Candy3Status = third.CandyStatus,
            Candy3Prompt = third.CandyPrompt,
            Candy3Response = third.CandyResponse,
            Candy3Error = third.CandyError,
            Candy3ReasoningTokens = third.CandyReasoningTokens,
            Candy3LatencyMs = third.CandyLatencyMs
        };
    }

    private async Task<CandyProbeResult> ProbeCandyAttempt(string baseUrl, string key, string model, string prompt, CancellationToken token, string reasoningEffort, string protocol)
    {
        token.ThrowIfCancellationRequested();
        prompt = CandyProbe.RandomizeSpacing(prompt);
        var watch = Stopwatch.StartNew();
        long? reasoningTokens = null;
        try
        {
            var text = await Completion(baseUrl, key, model, prompt, token,
                reasoningEffort: reasoningEffort == "default" ? null : reasoningEffort, observeReasoningTokens: value => reasoningTokens = value, protocol: protocol);
            return new(CandyProbe.Passes(text) ? "Success" : "Unavailable", prompt,
                string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]", StringComparison.Ordinal), null, reasoningEffort, reasoningTokens, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is UpstreamException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            return new("Failed", prompt, null,
                string.IsNullOrEmpty(key) ? e.Message : e.Message.Replace(key, "[REDACTED]", StringComparison.Ordinal), reasoningEffort, reasoningTokens, watch.ElapsedMilliseconds);
        }
    }

    public Task<JsonElement> Test(string baseUrl, string key, string model, string bank, int challengeCount, CancellationToken token, string candyReasoningEffort = "default", string juiceReasoningEffort = "default",
        string attributionLanguage = "zh", string candyLanguage = "zh", string candy2Language = "zh", string candy3Language = "zh", string protocol = ApiProtocols.Auto) =>
        Test(baseUrl, key, model, [bank], challengeCount, token, candyReasoningEffort, juiceReasoningEffort, attributionLanguage, candyLanguage, candy2Language, candy3Language, 0, protocol);

    internal async Task<JsonElement> Test(string baseUrl, string key, string model, IReadOnlyList<string> banks, int challengeCount, CancellationToken token, string candyReasoningEffort, string juiceReasoningEffort,
        string attributionLanguage = "zh", string candyLanguage = "zh", string candy2Language = "zh", string candy3Language = "zh", int calibrationBankIndex = 0, string protocol = ApiProtocols.Auto)
    {
        var watch = Stopwatch.StartNew();
        var responses = new List<object>();
        var outputs = new List<ProbeOutput>();
        var errors = new List<string>();
        var statusCode = 0;
        foreach (var challenge in Challenges.Generate(challengeCount, attributionLanguage))
        {
            var before = Stopwatch.StartNew();
            string text = ""; string? error = null;
            try { text = await Completion(baseUrl, key, model, challenge.Prompt, token, code => statusCode = code, protocol: protocol); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is UpstreamException or JsonException or KeyNotFoundException or InvalidOperationException)
            { error = e.Message; errors.Add(error); }
            outputs.Add(new(text, challenge.ExpectedCount));
            responses.Add(new { challenge.Id, challenge.Prompt, Text = text, challenge.ExpectedCount, DurationMs = before.ElapsedMilliseconds, Error = error });
        }
        var envelope = new Dictionary<string, object?> { ["responses"] = responses, ["duration_ms"] = watch.ElapsedMilliseconds, ["status_code"] = statusCode };
        foreach (var entry in await ProbeJuice(baseUrl, key, model, token, reasoningEffort: juiceReasoningEffort, protocol: protocol)) envelope[entry.Key] = entry.Value;
        envelope["candy"] = await ProbeCandy(baseUrl, key, model, token, candyReasoningEffort, candyLanguage, candy2Language, candy3Language, protocol: protocol);
        envelope["duration_ms"] = watch.ElapsedMilliseconds;
        try
        {
            var report = Attribution.AnalyzeGlobal(outputs, banks, calibrationBankIndex);
            report["api_test"] = new { requested = challengeCount, attempted = challengeCount, max_attempts = challengeCount, received = report["used_outputs"], errors };
            envelope["result"] = report;
        }
        catch (InvalidOperationException e) { envelope["error"] = e.Message; }
        var json = JsonSerializer.Serialize(envelope);
        if (!string.IsNullOrEmpty(key)) json = json.Replace(key, "[REDACTED]", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class UpstreamException(string message, bool protocolAccepted = false, int? statusCode = null) : Exception(message)
{
    public bool ProtocolAccepted { get; } = protocolAccepted;
    public int? StatusCode { get; } = statusCode;
}
