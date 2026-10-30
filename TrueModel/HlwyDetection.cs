using System.Diagnostics;
using System.Text.Json;

namespace TrueModel;

public sealed partial class ModelTraceClient
{
    public async Task<JsonElement> TestHlwy(string url, string key, string model, AppSettings settings, CancellationToken token, bool includeSupplemental = true)
    {
        var baselines = HlwyFingerprint.Baselines(settings.HlwyBaselinesJson);
        var samples = new List<(int Index, int? Number, object Response)>();
        var sync = new object();
        var statusCode = 0;
        var consecutiveErrors = 0;
        var nextIndex = 0;
        if (settings.HlwyConcurrency is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(settings.HlwyConcurrency));
        async Task Sample()
        {
            while (true)
            {
                int i;
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (nextIndex >= settings.HlwySampleCount || consecutiveErrors >= 5) return;
                    i = nextIndex++;
                }
                var prompt = HlwyFingerprint.BuildPrompt(settings.HlwyAntiTarget);
                var watch = Stopwatch.StartNew();
                string text = ""; string? error = null;
                int? number = null;
                try
                {
                    text = await Completion(url, key, model, prompt, token, code => Interlocked.Exchange(ref statusCode, code), statistical: true);
                    number = HlwyFingerprint.Parse(text);
                    if (number is null) error = "返回内容无法解析为 1–355 的数字。";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is UpstreamException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
                { error = e.Message; }
                lock (sync)
                {
                    samples.Add((i, number, new { Id = $"hlwy-{i + 1}", Prompt = prompt, Text = text, ExpectedCount = 1, DurationMs = watch.ElapsedMilliseconds, Error = error }));
                    // Once the failure threshold is reached, no worker starts another request.
                    if (consecutiveErrors < 5) consecutiveErrors = error is null ? 0 : consecutiveErrors + 1;
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(settings.HlwyConcurrency, settings.HlwySampleCount)).Select(_ => Sample()));
        var ordered = samples.OrderBy(s => s.Index).ToArray();
        var numbers = ordered.Where(s => s.Number.HasValue).Select(s => s.Number!.Value).ToArray();
        var responses = ordered.Select(s => s.Response).ToArray();
        var envelope = new Dictionary<string, object?> { ["responses"] = responses, ["status_code"] = statusCode };
        try { envelope["result"] = HlwyFingerprint.Analyze(numbers, baselines, model, settings.HlwySampleCount, settings.HlwyAntiTarget); }
        catch (InvalidOperationException e) { envelope["error"] = e.Message; }
        if (includeSupplemental)
        {
            foreach (var entry in await ProbeJuice(url, key, model, token, reasoningEffort: settings.JuiceReasoningEffort)) envelope[entry.Key] = entry.Value;
            envelope["candy"] = await ProbeCandy(url, key, model, token, settings.CandyReasoningEffort);
        }
        var json = JsonSerializer.Serialize(envelope);
        if (!string.IsNullOrEmpty(key)) json = json.Replace(key, "[REDACTED]", StringComparison.Ordinal);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
