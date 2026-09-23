using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrueModel;

public static partial class Attribution
{
    public static string CreateEmptyBank(string templateJson)
    {
        Validate(templateJson);
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
    public static string Enroll(string json, string model, string displayName, string family, IReadOnlyList<ProbeOutput> outputs)
    {
        Validate(json, allowIncomplete: true);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(family))
            throw new ArgumentException("模型标识、显示名称和家族不能为空");
        var samples = outputs.Select(o => ParseNumbers(o.Text)).ToArray();
        if (samples.Length < 3 || samples.Where((n, i) => n.Length < Math.Max(80, Math.Ceiling(outputs[i].ExpectedCount * .55))).Any())
            throw new InvalidOperationException("至少需要 3 份有效回答，且所有采集回答均须达到有效数字阈值；未保存指纹");
        var bank = JsonNode.Parse(json)!;
        var models = bank["models"]!.AsArray();
        var index = models.Select((m, i) => (m, i)).Where(x => x.m!["id"]!.GetValue<string>() == model).Select(x => x.i).DefaultIfEmpty(-1).Single();
        if (index < 0 && models.Count >= 1000) throw new InvalidOperationException("指纹库最多支持 1000 个模型");
        var counts = new int[355];
        foreach (var n in samples.SelectMany(n => n)) counts[n - 1]++;
        var entry = JsonSerializer.SerializeToNode(new { id = model, display_name = displayName, family, family_name = family,
            response_count = samples.Length, valid_number_count = counts.Sum(), counts,
            enrollment = new { collected_at = DateTime.UtcNow, method = "fixed-feature-space", calibration_refitted = false } });
        if (index < 0) { models.Add(entry); bank["robust"]!["model_order"]!.AsArray().Add(model); }
        else models[index] = entry;
        void SetRow(JsonArray rows, double[] row)
        {
            var node = JsonSerializer.SerializeToNode(row);
            if (index < 0) rows.Add(node); else rows[index] = node;
        }
        double[] Centroid(double[][] rows) => Normalize(Enumerable.Range(0, rows[0].Length).Select(i => rows.Average(r => r[i])).ToArray());
        using var original = JsonDocument.Parse(json);
        foreach (var name in new[] { "hellinger", "ordered_blocks" })
        {
            var artifact = original.RootElement.GetProperty("robust").GetProperty(name);
            var features = samples.Select(numbers =>
            {
                if (name == "ordered_blocks") return OrderedFeature(numbers);
                var histogram = new int[355];
                foreach (var n in numbers) histogram[n - 1]++;
                return Feature(histogram);
            }).Select(f => Transform(f, artifact)).ToArray();
            var target = bank["robust"]![name]!;
            SetRow(target["centroids"]!.AsArray(), Centroid(features.Select(f => Project(f, artifact)).ToArray()));
            if (name == "ordered_blocks")
            {
                var centroid = Centroid(features.Select(Normalize).ToArray());
                foreach (var environment in target["environment_centroids"]!.AsArray()) SetRow(environment!.AsArray(), centroid);
            }
        }
        bank["built_at"] = DateTime.UtcNow.ToString("O");
        bank["calibration_refitted"] = false;
        var result = bank.ToJsonString();
        Validate(result, allowIncomplete: true);
        return result;
    }
}

public sealed record EnrollmentInput(int BankId, string Model, string DisplayName, string Family, int ChallengeCount = 6);

public static class FingerprintEnrollment
{
    public static async Task<ProbeOutput[]> Collect(ModelTraceClient client, string url, string key, string model, int count, CancellationToken token)
    {
        var outputs = new List<ProbeOutput>();
        foreach (var challenge in Challenges.Generate(count))
        {
            token.ThrowIfCancellationRequested();
            var text = await client.Completion(url, key, model, challenge.Prompt, token);
            if (Attribution.ParseNumbers(text).Length < Math.Max(80, Math.Ceiling(challenge.ExpectedCount * .55)))
                throw new InvalidOperationException("模型回答未达到有效数字阈值；未保存指纹");
            outputs.Add(new(text, challenge.ExpectedCount));
        }
        return outputs.ToArray();
    }
}
