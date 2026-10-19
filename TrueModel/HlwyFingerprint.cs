// Statistical algorithm and prompt ported from hanlinwenyuan/hlwy-ai-checker
// 7bddead107da09ddec459522df4b74eb0c7f8ac6. LGPL-2.1; see THIRD-PARTY-NOTICES.md.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueModel;

public static class HlwyFingerprint
{
    public const string Prompt = "请从1到355之间随机选择一个数字，只输出这个数字，不要有任何其他内容。";
    public static string BuildPrompt(bool antiTarget)
    {
        if (!antiTarget) return Prompt;
        var gaps = Enumerable.Range(1, Prompt.Length - 1).Where(i => !(char.IsAsciiDigit(Prompt[i - 1]) && char.IsAsciiDigit(Prompt[i]))).ToArray();
        return Prompt.Insert(gaps[RandomNumberGenerator.GetInt32(gaps.Length)], " ");
    }
    public static int? Parse(string text)
    {
        var match = Regex.Match(text, "[0-9]+");
        return int.TryParse(match.Value, out var value) && value is >= 1 and <= 355 ? value : null;
    }
    public static JsonElement[] Baselines(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "hlwy_baselines.json"));
        if (json.Length > 2_000_000) throw new InvalidOperationException("基准文件不能超过 2 MB。");
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        if (rows.Length is < 1 or > 100) throw new InvalidOperationException("需要 1–100 个 HLWY 基准。");
        foreach (var row in rows)
        {
            var name = row.GetProperty("name").GetString();
            var dist = row.GetProperty("distribution").EnumerateArray().Select(n => n.GetDouble()).ToArray();
            var mode = row.GetProperty("stats").GetProperty("mode").GetInt32();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || dist.Length != 355 || dist.Any(n => !double.IsFinite(n) || n < 0) || !double.IsFinite(dist.Sum()) || dist.Sum() <= 0 || mode is < 1 or > 355)
                throw new InvalidOperationException("基准需要名称、355 项非负分布和 1–355 的众数。");
        }
        return rows.Select(r => r.Clone()).ToArray();
    }
    public static (double Score, double Cosine, double Js, double Mode) Similarity(double[] a, double[] b, int modeA, int modeB)
    {
        var sumA = a.Sum(); var sumB = b.Sum();
        double dot = 0, normA = 0, normB = 0, js = 0;
        for (var i = 0; i < 355; i++)
        {
            var p = a[i] / sumA; var q = b[i] / sumB; var m = (p + q) / 2;
            dot += p * q; normA += p * p; normB += q * q;
            if (p > 0) js += .5 * p * Math.Log((p + 1e-12) / (m + 1e-12));
            if (q > 0) js += .5 * q * Math.Log((q + 1e-12) / (m + 1e-12));
        }
        var cosine = dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        var mode = Math.Max(0, 1 - Math.Abs(modeA - modeB) / 50d);
        return (Math.Clamp(.5 * mode + .5 * cosine * Math.Exp(-js), 0, 1), cosine, js, mode);
    }
    public static object Analyze(IReadOnlyList<int> numbers, JsonElement[] baselines, string model, int requested, bool antiTarget)
    {
        if (numbers.Count < 40) throw new InvalidOperationException($"HLWY 仅获得 {numbers.Count} 个有效样本，至少需要 40 个。");
        var distribution = new double[355];
        foreach (var n in numbers) distribution[n - 1] += 1d / numbers.Count;
        var mode = Array.IndexOf(distribution, distribution.Max()) + 1;
        var mean = numbers.Average();
        var stats = new { mean, median = numbers.Order().ElementAt(numbers.Count / 2), stdDev = Math.Sqrt(numbers.Average(n => Math.Pow(n - mean, 2))), min = numbers.Min(), max = numbers.Max(), unique = numbers.Distinct().Count(), mode, modeCount = numbers.Count(n => n == mode) };
        var candidates = baselines.Select(b =>
        {
            var s = Similarity(distribution, b.GetProperty("distribution").EnumerateArray().Select(v => v.GetDouble()).ToArray(), mode, b.GetProperty("stats").GetProperty("mode").GetInt32());
            var name = b.GetProperty("name").GetString()!;
            return new { Model = name, DisplayName = name, Family = "—", Score = s.Score, Similarity = s.Score, Cosine = s.Cosine, JsDivergence = s.Js, ModeScore = s.Mode };
        }).OrderByDescending(c => c.Score).ToArray();
        return new { Method = "hlwy", UsedOutputs = numbers.Count, Requested = requested, AntiTarget = antiTarget, Candidates = candidates,
            BaselinesSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(baselines))),
            Baseline = new { name = model, model, apiType = "openai", timestamp = DateTime.UtcNow, iterations = numbers.Count, distribution, stats, rawData = numbers } };
    }
}
