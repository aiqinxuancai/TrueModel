using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrueModel;

public static partial class Attribution
{
    public static string CreateEmptyBank(string templateJson)
    {
        Validate(templateJson, allowIncomplete: true);
        var bank = JsonNode.Parse(templateJson)!;
        bank["models"]!.AsArray().Clear();
        bank["robust"]!["model_order"]!.AsArray().Clear();
        foreach (var name in new[] { "hellinger", "ordered_blocks" })
        {
            var artifact = bank["robust"]![name]!;
            artifact["centroids"]!.AsArray().Clear();
            if (name == "ordered_blocks")
                foreach (var environment in artifact["environment_centroids"]!.AsArray()) environment!.AsArray().Clear();
        }
        bank["built_at"] = DateTime.UtcNow.ToString("O");
        bank["calibration_refitted"] = false;
        var json = bank.ToJsonString();
        Validate(json, allowIncomplete: true);
        return json;
    }
    // Enroll in the existing feature space. Training transforms and calibration are not refitted.
    public static string Enroll(string json, string model, string displayName, string family, IReadOnlyList<ProbeOutput> outputs, bool accumulate = false)
    {
        Validate(json, allowIncomplete: true);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(family))
            throw new ArgumentException("模型标识、显示名称和家族不能为空");
        var samples = Samples(outputs);
        if (samples.Length < 3 || samples.Any(s => s.Numbers.Length < s.Minimum))
            throw new InvalidOperationException("至少需要 3 份有效回答，且所有采集回答均须达到有效数字阈值；未保存指纹");
        var bank = JsonNode.Parse(json)!;
        var models = bank["models"]!.AsArray();
        var index = models.Select((m, i) => (m, i)).Where(x => x.m!["id"]!.GetValue<string>() == model).Select(x => x.i).DefaultIfEmpty(-1).Single();
        if (index < 0 && models.Count >= 1000) throw new InvalidOperationException("指纹库最多支持 1000 个模型");
        var previous = accumulate && index >= 0 ? models[index]!.AsObject() : null;
        var previousResponses = previous?["response_count"] is JsonValue count && count.TryGetValue<int>(out var responses) ? Math.Max(0, responses) : 0;
        var counts = previous is null ? new int[355] : previous["counts"]!.Deserialize<int[]>()!;
        foreach (var n in samples.SelectMany(s => s.Numbers)) counts[n - 1] = checked(counts[n - 1] + 1);
        var responseCount = checked(previousResponses + samples.Length);
        var previousNorms = (previous?["enrollment"] as JsonObject)?["centroid_sum_norms"] as JsonObject;
        var sumNorms = new JsonObject();
        double SetRow(JsonArray rows, double[] added, JsonNode? previousNorm)
        {
            var sum = added;
            if (previous is not null)
            {
                // Earlier banks retain unit centroids. The response count estimates the missing norm of their sum.
                var scale = previousNorm is JsonValue value && value.TryGetValue<double>(out var norm) && double.IsFinite(norm) && norm > 0
                    ? norm : Math.Max(1, previousResponses);
                var row = rows[index]!.AsArray();
                sum = added.Select((v, i) => v + scale * row[i]!.GetValue<double>()).ToArray();
            }
            var sumNorm = Math.Max(Math.Sqrt(Dot(sum, sum)), 1e-12 * responseCount);
            var node = JsonSerializer.SerializeToNode(sum.Select(v => v / sumNorm).ToArray());
            if (index < 0) rows.Add(node); else rows[index] = node;
            return sumNorm;
        }
        static void Add(double[] sum, double[] values)
        {
            for (var i = 0; i < sum.Length; i++) sum[i] += values[i];
        }
        using var original = JsonDocument.Parse(json);
        foreach (var name in new[] { "hellinger", "ordered_blocks" })
        {
            var artifact = original.RootElement.GetProperty("robust").GetProperty(name);
            var ordered = name == "ordered_blocks";
            var sum = new double[artifact.GetProperty("feature_mean").GetArrayLength()];
            var environmentSum = ordered ? new double[sum.Length] : null;
            foreach (var sample in samples)
            {
                double[] feature;
                if (ordered) feature = OrderedFeature(sample.Numbers);
                else
                {
                    var histogram = new int[355];
                    foreach (var n in sample.Numbers) histogram[n - 1]++;
                    feature = Feature(histogram);
                }
                var transformed = Transform(feature, artifact);
                Add(sum, Project(transformed, artifact));
                if (environmentSum is not null) Add(environmentSum, Normalize(transformed));
            }
            var target = bank["robust"]![name]!;
            sumNorms[name] = SetRow(target["centroids"]!.AsArray(), sum, previousNorms?[name]);
            if (environmentSum is not null)
            {
                var environments = target["environment_centroids"]!.AsArray();
                var previousEnvironmentNorms = previousNorms?["environment_centroids"] as JsonArray;
                if (previousEnvironmentNorms?.Count != environments.Count) previousEnvironmentNorms = null;
                var environmentNorms = new JsonArray();
                for (var i = 0; i < environments.Count; i++)
                    environmentNorms.Add(SetRow(environments[i]!.AsArray(), environmentSum, previousEnvironmentNorms?[i]));
                sumNorms["environment_centroids"] = environmentNorms;
            }
        }
        var entry = previous ?? new JsonObject { ["id"] = model, ["display_name"] = displayName, ["family"] = family, ["family_name"] = family };
        entry["response_count"] = responseCount;
        entry["valid_number_count"] = counts.Sum();
        entry["counts"] = JsonSerializer.SerializeToNode(counts);
        entry["enrollment"] = JsonSerializer.SerializeToNode(new { collected_at = DateTime.UtcNow, method = "fixed-feature-space", calibration_refitted = false,
            centroid_sum_norms = sumNorms });
        if (index < 0) { models.Add(entry); bank["robust"]!["model_order"]!.AsArray().Add(model); }
        else if (previous is null) models[index] = entry;
        bank["built_at"] = DateTime.UtcNow.ToString("O");
        bank["calibration_refitted"] = false;
        var result = bank.ToJsonString();
        Validate(result, allowIncomplete: true);
        return result;
    }
}

public sealed record EnrollmentInput(int BankId, string Model, string DisplayName, string Family, int ChallengeCount = 10, bool Accumulate = false);

public static class FingerprintEnrollment
{
    public static async Task<ProbeOutput[]> Collect(ModelTraceClient client, string url, string key, string model, int count, CancellationToken token, string language = "zh",
        Func<int, CancellationToken, Task>? progress = null, string protocol = ApiProtocols.Auto)
    {
        var outputs = new List<ProbeOutput>();
        foreach (var challenge in Challenges.GenerateSequence(count, language))
        {
            token.ThrowIfCancellationRequested();
            var text = await client.Completion(url, key, model, challenge.Prompt, token, protocol: protocol);
            if (Attribution.ParseNumbers(text).Length < Math.Max(80, Math.Ceiling(challenge.ExpectedCount * .55)))
                throw new InvalidOperationException("模型回答未达到有效数字阈值；未保存指纹");
            outputs.Add(new(text, challenge.ExpectedCount));
            if (progress is not null) await progress(outputs.Count, token);
        }
        return outputs.ToArray();
    }
}
