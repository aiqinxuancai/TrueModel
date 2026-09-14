using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrueModel;

// Native port of ModelTrace enrollment.py's completion adapter.
public sealed class ModelTraceClient(HttpClient client)
{
    public const string DefaultUserAgent = "Codex Desktop/0.147.0-alpha.1.2 (Windows 10.0.26200; x86_64) unknown (codex_exec; 0.147.0-alpha.1.2)";
    private static readonly int[] Retryable = [408, 429, 500, 502, 503, 504];
    public static string CompletionUrl(string url, bool anthropic)
    {
        var normalized = url.TrimEnd('/');
        var suffix = anthropic ? "/messages" : "/chat/completions";
        return normalized.EndsWith(suffix, StringComparison.Ordinal) ? normalized : normalized + (normalized.EndsWith("/v1", StringComparison.Ordinal) ? "" : "/v1") + suffix;
    }
    public async Task<string> Completion(string url, string key, string model, string prompt, CancellationToken token, Action<int>? observeStatus = null)
    {
        var errors = new List<string>();
        foreach (var anthropic in new[] { false, true })
        {
            try { return await Request(url, key, model, prompt, anthropic, token, observeStatus); }
            catch (UpstreamException e) { errors.Add($"{(anthropic ? "anthropic" : "openai")}: {e.Message}"); }
        }
        throw new UpstreamException("接口格式自动探测失败；" + string.Join('；', errors));
    }
    private async Task<string> Request(string url, string key, string model, string prompt, bool anthropic, CancellationToken token, Action<int>? observeStatus)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(240));
            using var request = new HttpRequestMessage(HttpMethod.Post, CompletionUrl(url, anthropic));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var agent = Environment.GetEnvironmentVariable("MODELTRACE_USER_AGENT") ?? Environment.GetEnvironmentVariable("GPT56_USER_AGENT");
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(agent) ? DefaultUserAgent : agent.Trim());
            var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = new[] { new { role = "user", content = prompt } } };
            if (anthropic) { body["max_tokens"] = 4096; request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01"); }
            request.Content = JsonContent.Create(body);
            try
            {
                using var response = await client.SendAsync(request, timeout.Token);
                observeStatus?.Invoke((int)response.StatusCode);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < 3 && Retryable.Contains((int)response.StatusCode)) { await Delay(attempt, token); continue; }
                    throw new UpstreamException($"HTTP {(int)response.StatusCode}: {CompactError(text)}" + (attempt > 1 ? $"（已自动重试 {attempt - 1} 次）" : ""));
                }
                using var payload = JsonDocument.Parse(text);
                return ExtractContent(payload.RootElement, anthropic);
            }
            catch (HttpRequestException e)
            {
                if (attempt < 3) { await Delay(attempt, token); continue; }
                throw new UpstreamException("无法连接接口：" + e.HttpRequestError);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new UpstreamException("接口请求超时（240 秒）"); }
        }
        throw new UpstreamException("无法连接接口");
    }
    private static Task Delay(int attempt, CancellationToken token) => Task.Delay(TimeSpan.FromSeconds(attempt + RandomNumberGenerator.GetInt32(500001) / 1_000_000d), token);
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
    public async Task<JsonElement> Test(string baseUrl, string key, string model, string bank, int challengeCount, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var responses = new List<object>();
        var outputs = new List<ProbeOutput>();
        var errors = new List<string>();
        var statusCode = 0;
        foreach (var challenge in Challenges.Generate(challengeCount))
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
public sealed class UpstreamException(string message) : Exception(message);
