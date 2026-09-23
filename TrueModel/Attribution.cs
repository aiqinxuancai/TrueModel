using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueModel;

// Port of ModelTrace fingerprint.py at 60949ef; see THIRD-PARTY-NOTICES.md.
public static partial class Attribution
{
    public static int[] ParseNumbers(string text)
    {
        var best = new List<int>();
        var current = new List<int>();
        var end = 0;
        foreach (Match match in Regex.Matches(text, @"\d+", RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            if (text[end..match.Index].Any(char.IsLetter))
            { if (current.Count > best.Count) best = current; current = []; }
            if (int.TryParse(match.Value, out var n) && n is >= 1 and <= 355) current.Add(n);
            end = match.Index + match.Length;
        }
        return (current.Count > best.Count ? current : best).ToArray();
    }

    public static double[] Standardize(double[] x)
    {
        var mean = x.Average();
        var scale = Math.Max(Math.Sqrt(x.Average(v => (v - mean) * (v - mean))), 1e-12);
        return x.Select(v => (v - mean) / scale).ToArray();
    }
    private static double[] Feature(int[] counts)
    {
        var total = counts.Sum() + .5 * counts.Length;
        return counts.Select(c => Math.Sqrt((c + .5) / total)).ToArray();
    }
    private static double[] Array(JsonElement x) => x.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    private static double Dot(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();
    private static double[] Normalize(double[] x)
    {
        var norm = Math.Max(Math.Sqrt(Dot(x, x)), 1e-12);
        return x.Select(v => v / norm).ToArray();
    }
    private static double[] Transform(double[] feature, JsonElement artifact)
    {
        var mean = Array(artifact.GetProperty("feature_mean"));
        var scale = Array(artifact.GetProperty("feature_scale"));
        return feature.Select((v, i) => (v - mean[i]) / scale[i]).ToArray();
    }
    private static double[] Project(double[] values, JsonElement artifact)
    {
        var result = values.ToArray();
        foreach (var row in artifact.GetProperty("nuisance_basis").EnumerateArray())
        {
            var basis = Array(row);
            var coefficient = Dot(values, basis);
            for (var i = 0; i < result.Length; i++) result[i] -= coefficient * basis[i];
        }
        return Normalize(result);
    }
    private static double[] Scores(double[] feature, JsonElement artifact)
    {
        var projected = Project(Transform(feature, artifact), artifact);
        return Standardize(artifact.GetProperty("centroids").EnumerateArray().Select(row => Dot(projected, Array(row))).ToArray());
    }
    public static double[] Score(int[] numbers, JsonElement bank)
    {
        var counts = new int[355];
        foreach (var n in numbers) counts[n - 1]++;
        var robust = bank.GetProperty("robust");
        var marginal = Standardize(Scores(Feature(counts), robust.GetProperty("hellinger")));
        if (!robust.TryGetProperty("ordered_blocks", out var ordered)) return marginal;
        var weight = ordered.GetProperty("weight").GetDouble();
        if (weight == 0) return marginal;
        var values = OrderedFeature(numbers);
        var normalized = Normalize(Transform(values, ordered));
        var environments = ordered.GetProperty("environment_centroids").EnumerateArray()
            .Select(env => env.EnumerateArray().Select(row => Dot(normalized, Array(row))).ToArray()).ToArray();
        var template = Standardize(Enumerable.Range(0, marginal.Length).Select(i => environments.Max(e => e[i])).ToArray());
        var nuisance = Scores(values, ordered);
        var fused = Standardize(template.Zip(nuisance, (a, b) => .5 * a + .5 * b).ToArray());
        return marginal.Zip(fused, (a, b) => (1 - weight) * a + weight * b).ToArray();
    }
    private static double[] OrderedFeature(int[] numbers)
    {
        var parts = new List<double>();
        var offset = 0;
        for (var part = 0; part < 4; part++)
        {
            var size = numbers.Length / 4 + (part < numbers.Length % 4 ? 1 : 0);
            var histogram = new int[16];
            foreach (var n in numbers.AsSpan(offset, size)) histogram[(n - 1) * 16 / 355]++;
            parts.AddRange(Feature(histogram)); offset += size;
        }
        var digits = new int[10];
        foreach (var n in numbers) digits[n % 10]++;
        parts.AddRange(Feature(digits));
        return parts.ToArray();
    }
    public static AttributionReport Analyze(IReadOnlyList<ProbeOutput> outputs, string bankJson)
    {
        using var document = JsonDocument.Parse(bankJson);
        var bank = document.RootElement;
        var accepted = outputs.Select(o => ParseNumbers(o.Text)).Where((numbers, i) => numbers.Length >= Math.Max(80, Math.Ceiling(outputs[i].ExpectedCount * .55))).ToArray();
        if (accepted.Length == 0) throw new InvalidOperationException("No usable numeric output; refusal or truncation cannot be attributed.");
        var scores = accepted.Select(n => Score(n, bank)).ToArray();
        var beta = bank.GetProperty("calibration").GetProperty(Math.Min(accepted.Length, 3).ToString()).GetProperty("beta").GetDouble();
        var combined = Enumerable.Range(0, scores[0].Length).Select(i => scores.Average(s => s[i])).ToArray();
        var maximum = combined.Max();
        var weights = combined.Select(s => Math.Exp(beta * (s - maximum))).ToArray();
        var sum = weights.Sum();
        var candidates = bank.GetProperty("models").EnumerateArray().Select((m, i) => new Candidate(
            m.GetProperty("id").GetString()!, m.GetProperty("display_name").GetString()!,
            m.TryGetProperty("family", out var family) ? family.GetString()! : "models", weights[i] / sum, combined[i])).OrderByDescending(c => c.Probability).ToArray();
        return new AttributionReport(accepted.Length, beta, candidates, candidates.GroupBy(c => c.Family).ToDictionary(g => g.Key, g => g.Sum(c => c.Probability)));
    }
    public static void Validate(string json, bool allowIncomplete = false)
    {
        using var document = JsonDocument.Parse(json);
        var bank = document.RootElement;
        var ids = bank.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
        if ((!allowIncomplete && ids.Length < 2) || ids.Length > 1000 || ids.Distinct().Count() != ids.Length) throw new FormatException("Bank must contain 2-1000 unique models to be activated.");
        if (!ids.SequenceEqual(bank.GetProperty("robust").GetProperty("model_order").EnumerateArray().Select(m => m.GetString()))) throw new FormatException("Model order mismatch.");
        foreach (var (name, dimension) in new[] { ("hellinger", 355), ("ordered_blocks", 74) })
        {
            var artifact = bank.GetProperty("robust").GetProperty(name);
            foreach (var field in new[] { "feature_mean", "feature_scale" })
            {
                var vector = Array(artifact.GetProperty(field));
                if (vector.Length != dimension || vector.Any(v => !double.IsFinite(v) || (field == "feature_scale" && v <= 0))) throw new FormatException("Invalid feature dimensions or scale.");
            }
            void ValidateRows(JsonElement matrix, int? rows)
            {
                if (rows.HasValue && matrix.GetArrayLength() != rows) throw new FormatException("Invalid centroid count.");
                foreach (var row in matrix.EnumerateArray()) if (row.GetArrayLength() != dimension || Array(row).Any(v => !double.IsFinite(v))) throw new FormatException("Invalid matrix.");
            }
            ValidateRows(artifact.GetProperty("centroids"), ids.Length);
            ValidateRows(artifact.GetProperty("nuisance_basis"), null);
            if (name == "ordered_blocks")
            {
                var weight = artifact.GetProperty("weight").GetDouble();
                if (!double.IsFinite(weight) || weight < 0 || weight > 1) throw new FormatException("Invalid weight.");
                var envs = artifact.GetProperty("environment_centroids");
                if (envs.GetArrayLength() == 0) throw new FormatException("Missing environments.");
                foreach (var env in envs.EnumerateArray()) ValidateRows(env, ids.Length);
            }
        }
        for (var i = 1; i <= 3; i++)
        {
            var beta = bank.GetProperty("calibration").GetProperty(i.ToString()).GetProperty("beta").GetDouble();
            if (!double.IsFinite(beta) || beta <= 0) throw new FormatException("Invalid calibration.");
        }
        if (ids.Length >= 2) Analyze([new ProbeOutput(string.Join(',', Enumerable.Range(1, 310)), 310)], json);
    }
    public static Dictionary<string, object?> AnalyzeGlobal(IReadOnlyList<ProbeOutput> outputs, string json)
    {
        var report = Analyze(outputs, json);
        using var document = JsonDocument.Parse(json);
        var bank = document.RootElement;
        var models = bank.GetProperty("models").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!);
        var diagnostics = outputs.Select((o, i) => new { index = i, parsed_numbers = ParseNumbers(o.Text).Length, minimum_numbers = Math.Max(80, (int)Math.Ceiling(o.ExpectedCount * .55)), accepted = ParseNumbers(o.Text).Length >= Math.Max(80, Math.Ceiling(o.ExpectedCount * .55)) }).ToArray();
        var counts = new int[355];
        var nuisanceScores = new List<double[]>();
        for (var i = 0; i < outputs.Count; i++) if (diagnostics[i].accepted)
        {
            var local = new int[355];
            foreach (var n in ParseNumbers(outputs[i].Text)) { counts[n - 1]++; local[n - 1]++; }
            nuisanceScores.Add(Scores(Feature(local), bank.GetProperty("robust").GetProperty("hellinger")));
        }
        string FamilyName(string family) => models.Values.Where(m => (m.TryGetProperty("family", out var f) ? f.GetString() : "models") == family)
            .Select(m => m.TryGetProperty("family_name", out var n) ? n.GetString() : null).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? (family == "gpt" ? "GPT" : family == "claude" ? "Claude" : family);
        var order = models.Keys.ToArray();
        var results = report.Candidates.Select(c => new { model = c.Model, display_name = c.DisplayName, probability = c.Probability,
            profile_similarity = JsSimilarity(counts, models[c.Model].GetProperty("counts").EnumerateArray().Select(x => x.GetInt32()).ToArray()),
            score = c.Score, nuisance_score = nuisanceScores.Average(x => x[System.Array.IndexOf(order, c.Model)]), family = c.Family,
            family_name = FamilyName(c.Family), conditional_probability = c.Probability / report.Families[c.Family] }).ToArray();
        var winner = report.Candidates[0];
        var familyWinner = report.Families.MaxBy(f => f.Value);
        return new()
        {
            ["prediction"] = winner.Model, ["prediction_name"] = winner.DisplayName, ["probability"] = winner.Probability,
            ["used_outputs"] = report.UsedOutputs, ["results"] = results, ["diagnostics"] = diagnostics,
            ["calibration"] = new { queries = Math.Min(report.UsedOutputs, 3).ToString(), beta = report.Beta, cv_accuracy = bank.GetProperty("calibration").GetProperty(Math.Min(report.UsedOutputs, 3).ToString()).GetProperty("cv_accuracy").GetDouble() },
            ["family_prediction"] = familyWinner.Key, ["family_prediction_name"] = FamilyName(familyWinner.Key), ["family_probability"] = familyWinner.Value,
            ["family_probabilities"] = report.Families.Select(f => new { family = f.Key, display_name = FamilyName(f.Key), probability = f.Value }).ToArray(),
            ["method"] = "统一全局稳健数字指纹"
        };
    }
    private static double JsSimilarity(int[] left, int[] right)
    {
        var l = (double)left.Sum(); var r = right.Sum() + .5 * 355;
        var divergence = 0d;
        for (var i = 0; i < left.Length; i++)
        {
            var p = left[i] / l; var q = (right[i] + .5) / r; var middle = (p + q) / 2;
            if (p > 0) divergence += p * Math.Log(p / middle) / 2;
            divergence += q * Math.Log(q / middle) / 2;
        }
        return 1 - Math.Sqrt(divergence / Math.Log(2));
    }
}
public sealed record ProbeOutput(string Text, int ExpectedCount);
public sealed record Candidate(string Model, string DisplayName, string Family, double Probability, double Score);
public sealed record AttributionReport(int UsedOutputs, double Beta, Candidate[] Candidates, Dictionary<string, double> Families);
