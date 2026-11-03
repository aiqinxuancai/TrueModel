using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrueModel;

// Native port of ModelTrace enrollment.py's completion adapter.
public sealed partial class ModelTraceClient(HttpClient client, JuiceHistory? juiceHistory = null)
{
    public const string DefaultUserAgent = "Codex Desktop/0.147.0-alpha.1.2 (Windows 10.0.26200; x86_64) unknown (codex_exec; 0.147.0-alpha.1.2)";
    private static readonly int[] Retryable = [408, 429, 500, 502, 503, 504];
    public static string CompletionUrl(string url, bool anthropic)
    {
        var normalized = url.TrimEnd('/');
        var suffix = anthropic ? "/messages" : "/chat/completions";
        return normalized.EndsWith(suffix, StringComparison.Ordinal) ? normalized : normalized + (normalized.EndsWith("/v1", StringComparison.Ordinal) ? "" : "/v1") + suffix;
    }
    public async Task<string> Completion(string url, string key, string model, string prompt, CancellationToken token, Action<int>? observeStatus = null,
        string? reasoningEffort = null, Action<long?>? observeReasoningTokens = null, bool statistical = false, TimeSpan? timeout = null, int? maxTokens = null, int maxAttempts = 3, bool stream = false)
    {
        var errors = new List<string>();
        var messages = ConversationEnvelope.Create(prompt);
        // An explicit OpenAI effort cannot be silently discarded on protocol fallback.
        foreach (var anthropic in reasoningEffort is null ? new[] { false, true } : new[] { false })
        {
            try { return await Request(url, key, model, messages, anthropic, token, observeStatus, maxAttempts, reasoningEffort: reasoningEffort, observeReasoningTokens: observeReasoningTokens, statistical: statistical, timeout: timeout, maxTokens: maxTokens, stream: stream); }
            // A broken stream after a 2xx response means the protocol was right; retrying another one only wastes the wait.
            catch (UpstreamException e) when (e.ProtocolAccepted) { throw new UpstreamException($"{(anthropic ? "anthropic" : "openai")}: {e.Message}"); }
            catch (UpstreamException e) { errors.Add($"{(anthropic ? "anthropic" : "openai")}: {e.Message}"); }
        }
        throw new UpstreamException("接口格式自动探测失败；" + string.Join('；', errors));
    }
    private async Task<string> Request(string url, string key, string model, ConversationMessage[] messages, bool anthropic, CancellationToken token, Action<int>? observeStatus, int maxAttempts = 3, bool juice = false,
        string? reasoningEffort = null, Action<long?>? observeReasoningTokens = null, bool statistical = false, Action<string, string>? observeJuiceFailure = null, TimeSpan? timeout = null, int? maxTokens = null, bool stream = false)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(240);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(limit);
            using var request = new HttpRequestMessage(HttpMethod.Post, CompletionUrl(url, anthropic));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var agent = Environment.GetEnvironmentVariable("MODELTRACE_USER_AGENT") ?? Environment.GetEnvironmentVariable("GPT56_USER_AGENT");
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(agent) ? DefaultUserAgent : agent.Trim());
            var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
            if (reasoningEffort is not null) body["reasoning_effort"] = reasoningEffort;
            if (anthropic) { body["max_tokens"] = maxTokens ?? 4096; request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01"); }
            if (statistical) { body["temperature"] = 1.0; body["max_tokens"] = 256; }
            if (stream) body["stream"] = true;
            request.Content = JsonContent.Create(body);
            try
            {
                using var response = await client.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, deadline.Token);
                observeStatus?.Invoke((int)response.StatusCode);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    return await ReadEventStream(response, anthropic, observeReasoningTokens, deadline.Token);
                var text = await response.Content.ReadAsStringAsync(deadline.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < maxAttempts && Retryable.Contains((int)response.StatusCode)) { await Delay(attempt, token); continue; }
                    throw new UpstreamException($"HTTP {(int)response.StatusCode}: {CompactError(text)}" + ((int)response.StatusCode == 524 ? "（Cloudflare 等待源站响应超时，通常为 100 秒）" : "") + (attempt > 1 ? $"（已自动重试 {attempt - 1} 次）" : ""));
                }
                // Some gateways ignore "stream" and answer with a single JSON body.
                using var payload = JsonDocument.Parse(text);
                if (juice)
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
                var content = ExtractContent(payload.RootElement, anthropic);
                observeReasoningTokens?.Invoke(ExtractReasoningTokens(payload.RootElement));
                return content;
            }
            catch (HttpRequestException e)
            {
                if (attempt < maxAttempts) { await Delay(attempt, token); continue; }
                throw new UpstreamException("无法连接接口：" + e.HttpRequestError);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new UpstreamException($"接口请求超时（{limit.TotalSeconds:0} 秒）"); }
        }
        throw new UpstreamException("无法连接接口");
    }
    // Server-sent events keep long reasoning requests alive behind proxies such as Cloudflare (HTTP 524 after ~100 s idle).
    private static async Task<string> ReadEventStream(HttpResponseMessage response, bool anthropic, Action<long?>? observeReasoningTokens, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
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
                if (anthropic)
                {
                    var type = root.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                    if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var piece) && piece.ValueKind == JsonValueKind.String)
                        text.Append(piece.GetString());
                    else if (type == "message_delta" && root.TryGetProperty("delta", out var summary) && summary.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String)
                        finish = stop.GetString();
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
        if (finish is "length" or "max_tokens" or "content_filter" or "refusal")
            throw new UpstreamException($"回答未正常完成（{finish}），本次回答不计入", protocolAccepted: true);
        if (events == 0) throw new UpstreamException("流式响应为空，未收到任何数据", protocolAccepted: true);
        observeReasoningTokens?.Invoke(reasoningTokens);
        return text.ToString();
    }
    private static Task Delay(int attempt, CancellationToken token) => Task.Delay(TimeSpan.FromSeconds(attempt + RandomNumberGenerator.GetInt32(500001) / 1_000_000d), token);
    public static long? ExtractReasoningTokens(JsonElement payload)
    {
        // Prefer the standard Chat Completions field, including an explicit zero.
        // Only read named usage fields; never search message text or infer from output tokens.
        string[][] paths =
        [
            ["usage", "completion_tokens_details", "reasoning_tokens"],
            ["usage", "output_tokens_details", "reasoning_tokens"],
            ["usage", "reasoning_tokens"],
            ["usage", "reasoning_output_tokens"],
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
    public static string ExtractContent(JsonElement payload, bool anthropic)
    {
        if (anthropic)
        {
            var reason = payload.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
            if (reason == "refusal") throw new UpstreamException("模型拒绝生成，本次回答不计入");
            if (reason == "max_tokens") throw new UpstreamException("回答因 max_tokens 截断，本次回答不计入");
            return string.Concat(payload.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : ""));
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
            if (doc.RootElement.TryGetProperty("error", out var error)) text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.ToString() : error.ToString();
        }
        catch (JsonException) { }
        return text[..Math.Min(500, text.Length)];
    }
    public async Task<Dictionary<string, object?>> ProbeJuice(string baseUrl, string key, string model, CancellationToken token, string? methodId = null, string reasoningEffort = "default")
    {
        if (!CandyProbe.ValidReasoningEffort(reasoningEffort)) throw new ArgumentException("不支持的 Juice 思考等级", nameof(reasoningEffort));
        var envelope = new Dictionary<string, object?>();
        if (JuiceProbe.Supports(model))
        {
            token.ThrowIfCancellationRequested();
            var endpoint = CompletionUrl(baseUrl, false);
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
                    var text = await Request(baseUrl, key, model, ConversationEnvelope.Create(method.Prompt), false, token, null, maxAttempts: 1, juice: true,
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
        string language = "zh", string language2 = "zh", string language3 = "zh")
    {
        if (!CandyProbe.ValidReasoningEffort(reasoningEffort)) throw new ArgumentException("不支持的糖果思考等级", nameof(reasoningEffort));
        var firstPrompt = PromptLanguages.Get(language).Candy;
        var secondPrompt = CandyProbe.CreateObfuscatedPrompt(language2);
        var thirdPrompt = CandyProbe.CreateSpaceObfuscatedPrompt(language3);
        var first = await ProbeCandyAttempt(baseUrl, key, model, firstPrompt, token, reasoningEffort);
        var second = await ProbeCandyAttempt(baseUrl, key, model, secondPrompt, token, reasoningEffort);
        var third = await ProbeCandyAttempt(baseUrl, key, model, thirdPrompt, token, reasoningEffort);
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
    private async Task<CandyProbeResult> ProbeCandyAttempt(string baseUrl, string key, string model, string prompt, CancellationToken token, string reasoningEffort)
    {
        token.ThrowIfCancellationRequested();
        prompt = CandyProbe.RandomizeSpacing(prompt);
        var watch = Stopwatch.StartNew();
        long? reasoningTokens = null;
        try
        {
            var text = await Completion(baseUrl, key, model, prompt, token,
                reasoningEffort: reasoningEffort == "default" ? null : reasoningEffort, observeReasoningTokens: value => reasoningTokens = value);
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
    public async Task<JsonElement> Test(string baseUrl, string key, string model, string bank, int challengeCount, CancellationToken token, string candyReasoningEffort = "default", string juiceReasoningEffort = "default",
        string attributionLanguage = "zh", string candyLanguage = "zh", string candy2Language = "zh", string candy3Language = "zh")
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
            try { text = await Completion(baseUrl, key, model, challenge.Prompt, token, code => statusCode = code); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is UpstreamException or JsonException or KeyNotFoundException or InvalidOperationException)
            { error = e.Message; errors.Add(error); }
            outputs.Add(new(text, challenge.ExpectedCount));
            responses.Add(new { challenge.Id, challenge.Prompt, Text = text, challenge.ExpectedCount, DurationMs = before.ElapsedMilliseconds, Error = error });
        }
        var envelope = new Dictionary<string, object?> { ["responses"] = responses, ["duration_ms"] = watch.ElapsedMilliseconds, ["status_code"] = statusCode };
        foreach (var entry in await ProbeJuice(baseUrl, key, model, token, reasoningEffort: juiceReasoningEffort)) envelope[entry.Key] = entry.Value;
        envelope["candy"] = await ProbeCandy(baseUrl, key, model, token, candyReasoningEffort, candyLanguage, candy2Language, candy3Language);
        envelope["duration_ms"] = watch.ElapsedMilliseconds;
        try
        {
            var report = Attribution.AnalyzeGlobal(outputs, bank);
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
public sealed class UpstreamException(string message, bool protocolAccepted = false) : Exception(message)
{
    public bool ProtocolAccepted { get; } = protocolAccepted;
}
