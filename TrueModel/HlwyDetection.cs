using System.Diagnostics;
using System.Text.Json;

namespace TrueModel;

public sealed partial class ModelTraceClient
{
    public async Task<JsonElement> TestHlwy(string url, string key, string model, AppSettings settings, CancellationToken token)
    {
        var baselines = HlwyFingerprint.Baselines(settings.HlwyBaselinesJson);
        var numbers = new List<int>();
        var responses = new List<object>();
        var statusCode = 0;
        var consecutiveErrors = 0;
        for (var i = 0; i < settings.HlwySampleCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var prompt = HlwyFingerprint.BuildPrompt(settings.HlwyAntiTarget);
            var watch = Stopwatch.StartNew();
            string text = ""; string? error = null;
            try
            {
                text = await Completion(url, key, model, prompt, token, code => statusCode = code, statistical: true);
                if (HlwyFingerprint.Parse(text) is int n) numbers.Add(n);
                else error = "返回内容无法解析为 1–355 的数字。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is UpstreamException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
            { error = e.Message; }
            responses.Add(new { Id = $"hlwy-{i + 1}", Prompt = prompt, Text = text, ExpectedCount = 1, DurationMs = watch.ElapsedMilliseconds, Error = error });
            consecutiveErrors = error is null ? 0 : consecutiveErrors + 1;
            if (consecutiveErrors >= 5) break;
        }
        var envelope = new Dictionary<string, object?> { ["responses"] = responses, ["status_code"] = statusCode };
        try { envelope["result"] = HlwyFingerprint.Analyze(numbers, baselines, model, settings.HlwySampleCount, settings.HlwyAntiTarget); }
        catch (InvalidOperationException e) { envelope["error"] = e.Message; }
        foreach (var entry in await ProbeJuice(url, key, model, token)) envelope[entry.Key] = entry.Value;
        envelope["candy"] = await ProbeCandy(url, key, model, token, settings.CandyReasoningEffort);
        var json = JsonSerializer.Serialize(envelope);
        if (!string.IsNullOrEmpty(key)) json = json.Replace(key, "[REDACTED]", StringComparison.Ordinal);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
