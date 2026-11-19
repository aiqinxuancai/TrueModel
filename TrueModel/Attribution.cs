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
    private sealed record BankCandidates(JsonElement Bank, JsonElement[] Models, int[] Indices);

    private static BankCandidates[] SelectCandidates(IEnumerable<JsonElement> banks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<BankCandidates>();
        foreach (var bank in banks)
        {
            var models = bank.GetProperty("models").EnumerateArray().ToArray();
            var indices = Enumerable.Range(0, models.Length).Where(i => seen.Add(models[i].GetProperty("id").GetString()!)).ToArray();
            if (indices.Length > 0) candidates.Add(new(bank, indices.Select(i => models[i]).ToArray(), indices));
        }
        return candidates.ToArray();
    }

    private static double[] RawScores(double[] feature, JsonElement artifact)
    {
        var projected = Project(Transform(feature, artifact), artifact);
        return artifact.GetProperty("centroids").EnumerateArray().Select(row => Dot(projected, Array(row))).ToArray();
    }

    public static double[] Score(int[] numbers, JsonElement bank) => Score(numbers, SelectCandidates([bank])).Combined;

    private static (double[] Combined, double[] Marginal) Score(int[] numbers, BankCandidates[] banks)
    {
        var counts = new int[355];
        foreach (var n in numbers) counts[n - 1]++;
        var feature = Feature(counts);
        // The transforms are bank-specific; standardize only after pooling the candidates' cosine scores.
        var marginalScores = Standardize(banks.SelectMany(b =>
        {
            var raw = RawScores(feature, b.Bank.GetProperty("robust").GetProperty("hellinger"));
            return b.Indices.Select(i => raw[i]);
        }).ToArray());
        var marginal = Standardize(marginalScores);
        var artifacts = banks.Select(b =>
        {
            var hasOrdered = b.Bank.GetProperty("robust").TryGetProperty("ordered_blocks", out var ordered);
            return (b.Indices, Artifact: ordered, Weight: hasOrdered ? ordered.GetProperty("weight").GetDouble() : 0);
        }).ToArray();
        if (artifacts.All(a => a.Weight == 0)) return (marginal, marginalScores);
        var values = OrderedFeature(numbers);
        var orderedScores = artifacts.SelectMany(a =>
        {
            if (a.Artifact.ValueKind == JsonValueKind.Undefined) return a.Indices.Select(_ => (Template: 0d, Nuisance: 0d, a.Weight));
            var normalized = Normalize(Transform(values, a.Artifact));
            var environments = a.Artifact.GetProperty("environment_centroids").EnumerateArray()
                .Select(env => env.EnumerateArray().Select(row => Dot(normalized, Array(row))).ToArray()).ToArray();
            var raw = RawScores(values, a.Artifact);
            return a.Indices.Select(i => (Template: environments.Max(e => e[i]), Nuisance: raw[i], a.Weight));
        }).ToArray();
        var template = Standardize(orderedScores.Select(s => s.Template).ToArray());
        var nuisance = Standardize(orderedScores.Select(s => s.Nuisance).ToArray());
        var fused = Standardize(template.Zip(nuisance, (a, b) => .5 * a + .5 * b).ToArray());
        return (marginal.Select((score, i) => (1 - orderedScores[i].Weight) * score + orderedScores[i].Weight * fused[i]).ToArray(), marginalScores);
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
        var accepted = Samples(outputs).Where(s => s.Numbers.Length >= s.Minimum).Select(s => s.Numbers).ToArray();
        return Analyze(accepted, SelectCandidates([bank]), bank.GetProperty("calibration")).Report;
    }

    private static (int[] Numbers, int Minimum)[] Samples(IReadOnlyList<ProbeOutput> outputs) =>
        outputs.Select(o => (ParseNumbers(o.Text), Math.Max(80, (int)Math.Ceiling(o.ExpectedCount * .55)))).ToArray();

    private static string Family(JsonElement model) => model.TryGetProperty("family", out var family) ? family.GetString() ?? "models" : "models";

    private static (AttributionReport Report, double[] Marginal) Analyze(int[][] accepted, BankCandidates[] banks, JsonElement calibration)
    {
        if (accepted.Length == 0) throw new InvalidOperationException("No usable numeric output; refusal or truncation cannot be attributed.");
        if (banks.Length == 0) throw new InvalidOperationException("指纹库未包含可匹配的模型");
        var scores = accepted.Select(n => Score(n, banks)).ToArray();
        var beta = calibration.GetProperty(Math.Min(accepted.Length, 3).ToString()).GetProperty("beta").GetDouble();
        var indices = Enumerable.Range(0, scores[0].Combined.Length);
        var combined = indices.Select(i => scores.Average(s => s.Combined[i])).ToArray();
        var maximum = combined.Max();
        var weights = combined.Select(s => Math.Exp(beta * (s - maximum))).ToArray();
        var sum = weights.Sum();
        var candidates = banks.SelectMany(b => b.Models).Select((m, i) => new Candidate(
            m.GetProperty("id").GetString()!, m.GetProperty("display_name").GetString()!, Family(m), weights[i] / sum, combined[i]))
            .OrderByDescending(c => c.Probability).ToArray();
        var report = new AttributionReport(accepted.Length, beta, candidates, candidates.GroupBy(c => c.Family).ToDictionary(g => g.Key, g => g.Sum(c => c.Probability)));
        return (report, indices.Select(i => scores.Average(s => s.Marginal[i])).ToArray());
    }
    public static void Validate(string json, bool allowIncomplete = false)
    {
        using var document = JsonDocument.Parse(json);
        var bank = document.RootElement;
        var models = bank.GetProperty("models").EnumerateArray().ToArray();
        var ids = models.Select(m => m.GetProperty("id").GetString()).ToArray();
        if ((!allowIncomplete && ids.Length < 2) || ids.Length > 1000 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Length)
            throw new FormatException("Invalid model count or duplicate model IDs.");
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.GetProperty("display_name").GetString())) throw new FormatException("Missing model display name.");
            if ((model.TryGetProperty("family", out var family) && family.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) ||
                (model.TryGetProperty("family_name", out var familyName) && familyName.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                throw new FormatException("Invalid model family.");
            var counts = model.GetProperty("counts");
            if (counts.GetArrayLength() != 355 || counts.EnumerateArray().Any(n => n.GetInt32() < 0)) throw new FormatException("Invalid model counts.");
        }
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
    public static Dictionary<string, object?> AnalyzeGlobal(IReadOnlyList<ProbeOutput> outputs, string json) => AnalyzeGlobal(outputs, [json]);

    // Banks are supplied in priority order. The first bank supplies the calibration, normally the default.
    public static Dictionary<string, object?> AnalyzeGlobal(IReadOnlyList<ProbeOutput> outputs, IReadOnlyList<string> banks)
    {
        if (banks.Count == 0) throw new InvalidOperationException("没有可用的指纹库");
        var documents = new List<JsonDocument>(banks.Count);
        try
        {
            foreach (var bank in banks) documents.Add(JsonDocument.Parse(bank));
            var candidates = SelectCandidates(documents.Select(d => d.RootElement));
            var calibration = documents[0].RootElement.GetProperty("calibration");
            return BuildGlobalReport(outputs, candidates, calibration);
        }
        finally { foreach (var document in documents) document.Dispose(); }
    }

    private static Dictionary<string, object?> BuildGlobalReport(IReadOnlyList<ProbeOutput> outputs, BankCandidates[] banks, JsonElement calibration)
    {
        var samples = Samples(outputs);
        var diagnostics = samples.Select((s, i) => new { index = i, parsed_numbers = s.Numbers.Length, minimum_numbers = s.Minimum, accepted = s.Numbers.Length >= s.Minimum }).ToArray();
        var accepted = samples.Where(s => s.Numbers.Length >= s.Minimum).Select(s => s.Numbers).ToArray();
        var (report, marginal) = Analyze(accepted, banks, calibration);
        var models = banks.SelectMany(b => b.Models).ToArray();
        var profiles = models.Select((m, i) => new { Model = m, Index = i }).ToDictionary(row => row.Model.GetProperty("id").GetString()!);
        var counts = new int[355];
        foreach (var numbers in accepted)
            foreach (var n in numbers) counts[n - 1]++;
        var familyNames = models.GroupBy(Family).ToDictionary(g => g.Key, g => g
            .Select(m => m.TryGetProperty("family_name", out var n) ? n.GetString() : null).FirstOrDefault(n => !string.IsNullOrEmpty(n))
            ?? (g.Key == "gpt" ? "GPT" : g.Key == "claude" ? "Claude" : g.Key));
        var results = report.Candidates.Select(c =>
        {
            var profile = profiles[c.Model];
            var familyProbability = report.Families[c.Family];
            return new { model = c.Model, display_name = c.DisplayName, probability = c.Probability,
                profile_similarity = JsSimilarity(counts, profile.Model.GetProperty("counts").EnumerateArray().Select(x => x.GetInt32()).ToArray()),
                score = c.Score, nuisance_score = marginal[profile.Index], family = c.Family, family_name = familyNames[c.Family],
                conditional_probability = familyProbability > 0 ? c.Probability / familyProbability : 0 };
        }).ToArray();
        var winner = report.Candidates[0];
        var familyWinner = report.Families.MaxBy(f => f.Value);
        var queries = Math.Min(report.UsedOutputs, 3).ToString();
        double? cvAccuracy = banks.Length == 1 && calibration.GetProperty(queries).TryGetProperty("cv_accuracy", out var accuracy) &&
            accuracy.ValueKind == JsonValueKind.Number && accuracy.TryGetDouble(out var cv) && double.IsFinite(cv) ? cv : null;
        return new()
        {
            ["prediction"] = winner.Model, ["prediction_name"] = winner.DisplayName, ["probability"] = winner.Probability,
            ["used_outputs"] = report.UsedOutputs, ["results"] = results, ["diagnostics"] = diagnostics,
            ["calibration"] = new { queries, beta = report.Beta, cv_accuracy = cvAccuracy }, ["candidate_bank_count"] = banks.Length,
            ["family_prediction"] = familyWinner.Key, ["family_prediction_name"] = familyNames[familyWinner.Key], ["family_probability"] = familyWinner.Value,
            ["family_probabilities"] = report.Families.Select(f => new { family = f.Key, display_name = familyNames[f.Key], probability = f.Value }).ToArray(),
            ["method"] = "统一全局稳健数字指纹"
        };
    }
    private static double JsSimilarity(int[] left, int[] right)
    {
        var l = (double)left.Sum(); var r = right.Sum(n => (double)n) + .5 * 355;
        var divergence = 0d;
        for (var i = 0; i < left.Length; i++)
        {
            var p = left[i] / l; var q = (right[i] + .5) / r; var middle = (p + q) / 2;
            if (p > 0) divergence += p * Math.Log(p / middle) / 2;
            divergence += q * Math.Log(q / middle) / 2;
        }
        return 1 - Math.Sqrt(Math.Max(0, divergence) / Math.Log(2));
    }
}
public sealed record ProbeOutput(string Text, int ExpectedCount);
public sealed record Candidate(string Model, string DisplayName, string Family, double Probability, double Score);
public sealed record AttributionReport(int UsedOutputs, double Beta, Candidate[] Candidates, Dictionary<string, double> Families);
