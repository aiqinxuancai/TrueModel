using System.Text.Json;
using System.Text.Json.Nodes;
using TrueModel;

namespace TrueModel.Tests;

public class AttributionTests
{
    private static string Bank => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "unified_bank.json"));
    [Fact]
    public void ParsesLongestNumericRunAndRejectsOutOfRange()
    {
        Assert.Equal(new[] { 1, 2, 355 }, Attribution.ParseNumbers("12 words 1, 2, 355, 356, 0"));
    }
    [Fact]
    public void DefaultBankProducesNormalizedCandidateAndFamilyProbabilities()
    {
        Attribution.Validate(Bank);
        var report = Attribution.Analyze([new(string.Join(',', Enumerable.Range(1, 310)), 310)], Bank);
        Assert.Equal(16, report.Candidates.Length);
        Assert.Contains(report.Candidates, c => c.Model == "gpt-6-sol");
        Assert.Contains(report.Candidates, c => c.Model == "gpt-6-luna");
        Assert.Contains(report.Candidates, c => c.Model == "claude-opus-5-5");
        Assert.Equal(1, report.Candidates.Sum(c => c.Probability), 10);
        Assert.Equal(1, report.Families.Values.Sum(), 10);
        Assert.Equal(1, report.UsedOutputs);
        Assert.All(report.Candidates, c => Assert.True(double.IsFinite(c.Score)));
    }
    [Fact]
    public void RefusalAndShortOutputNeverProduceIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => Attribution.Analyze([new("I cannot comply", 310)], Bank));
        Assert.Throws<InvalidOperationException>(() => Attribution.Analyze([new(string.Join(',', Enumerable.Range(1, 100)), 310)], Bank));
    }
    [Fact]
    public void CalibrationUsesAcceptedOutputCount()
    {
        var good = new ProbeOutput(string.Join(',', Enumerable.Range(1, 310)), 310);
        var report = Attribution.Analyze([good, good, new("refused", 310)], Bank);
        using var document = JsonDocument.Parse(Bank);
        Assert.Equal(2, report.UsedOutputs);
        Assert.Equal(document.RootElement.GetProperty("calibration").GetProperty("2").GetProperty("beta").GetDouble(), report.Beta);
    }

    [Fact]
    public void PoolsBanksInPriorityOrderAndUsesTheSelectedCalibration()
    {
        var bank = Bank;
        var sample = new ProbeOutput(string.Join(',', Enumerable.Range(1, 310)), 310);
        var empty = Attribution.CreateEmptyBank(bank);
        var firstTemplate = JsonNode.Parse(empty)!;
        firstTemplate["calibration"]!["1"]!["beta"] = 0.25;
        var first = Attribution.Enroll(firstTemplate.ToJsonString(), "supplement-a", "First", "custom", [sample, sample, sample]);
        var shifted = JsonNode.Parse(empty)!;
        shifted["robust"]!["hellinger"]!["feature_mean"]![0] = 20;
        shifted["robust"]!["ordered_blocks"]!["feature_mean"]![0] = 20;
        var second = Attribution.Enroll(shifted.ToJsonString(), "supplement-b", "Second", "custom", [sample, sample, sample]);
        var overlapsDefault = Attribution.Enroll(second, "gpt-6-sol", "Local GPT", "custom", [sample, sample, sample]);
        var overlapsFirst = Attribution.Enroll(overlapsDefault, "supplement-a", "Other Duplicate", "custom", [sample, sample, sample]);
        var result = JsonSerializer.SerializeToElement(Attribution.AnalyzeGlobal([sample], [first, overlapsFirst, bank], calibrationBankIndex: 2));
        var candidates = result.GetProperty("results").EnumerateArray().ToDictionary(c => c.GetProperty("model").GetString()!);
        Assert.Equal(18, candidates.Count);
        Assert.Equal(3, result.GetProperty("candidate_bank_count").GetInt32());
        Assert.Equal("Local GPT", candidates["gpt-6-sol"].GetProperty("display_name").GetString());
        Assert.Equal("First", candidates["supplement-a"].GetProperty("display_name").GetString());
        Assert.Equal(candidates["supplement-a"].GetProperty("score").GetDouble(), candidates["supplement-b"].GetProperty("score").GetDouble(), 10);
        Assert.Equal(1, candidates.Values.Sum(c => c.GetProperty("probability").GetDouble()), 10);
        Assert.Equal(Attribution.Analyze([sample], bank).Beta, result.GetProperty("calibration").GetProperty("beta").GetDouble());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("calibration").GetProperty("cv_accuracy").ValueKind);

        var fullOverride = Attribution.Enroll(bank, "gpt-6-sol", "Local GPT", "custom", [sample, sample, sample]);
        var overridden = JsonSerializer.SerializeToElement(Attribution.AnalyzeGlobal([sample], [fullOverride, bank], calibrationBankIndex: 1));
        Assert.Equal(1, overridden.GetProperty("candidate_bank_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, overridden.GetProperty("calibration").GetProperty("cv_accuracy").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccumulatesFrequenciesAndUpdatesCentroidsInAnExportedBank(bool legacy)
    {
        var empty = Attribution.CreateEmptyBank(Bank);
        static ProbeOutput Sample(IEnumerable<int> numbers) => new(string.Join(',', numbers), 310);
        ProbeOutput[] first = [Sample(Enumerable.Range(1, 310)), Sample(Enumerable.Repeat(17, 310)), Sample(Enumerable.Repeat(304, 310))];
        ProbeOutput[] second = [Sample(Enumerable.Range(22, 310).Reverse()), Sample(Enumerable.Repeat(277, 310)), Sample(Enumerable.Repeat(34, 310))];
        var enrolled = JsonNode.Parse(Attribution.Enroll(empty, "local", "Original", "gpt", first))!;
        if (legacy) enrolled["models"]![0]!["enrollment"]!.AsObject().Remove("centroid_sum_norms");
        var added = JsonNode.Parse(Attribution.Enroll(enrolled.ToJsonString(), "local", "Different name", "custom", second, accumulate: true))!;
        var combined = JsonNode.Parse(Attribution.Enroll(empty, "local", "Original", "gpt", [.. first, .. second]))!;
        var model = Assert.Single(added["models"]!.AsArray())!;
        Assert.Equal(6, model["response_count"]!.GetValue<int>());
        Assert.Equal(1860, model["valid_number_count"]!.GetValue<int>());
        Assert.Equal("Original", model["display_name"]!.GetValue<string>());
        Assert.Equal("gpt", model["family"]!.GetValue<string>());
        Assert.Equal(combined["models"]![0]!["counts"]!.Deserialize<int[]>(), model["counts"]!.Deserialize<int[]>());

        static IEnumerable<double> Centroids(JsonNode bank)
        {
            var robust = bank["robust"]!;
            var ordered = robust["ordered_blocks"]!;
            return robust["hellinger"]!["centroids"]![0]!.AsArray().Concat(ordered["centroids"]![0]!.AsArray())
                .Concat(ordered["environment_centroids"]!.AsArray().SelectMany(env => env![0]!.AsArray()))
                .Select(v => v!.GetValue<double>());
        }
        var actual = Centroids(added).ToArray();
        Assert.All(actual, v => Assert.True(double.IsFinite(v)));
        Assert.False(Centroids(enrolled).SequenceEqual(actual));
        if (!legacy)
            Assert.All(Centroids(combined).Zip(actual), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1e-12));
    }

    [Fact]
    public void PasswordHashIsSaltedAndVerifies()
    {
        var first = Passwords.Hash("test-password-123");
        Assert.NotEqual(first, Passwords.Hash("test-password-123"));
        Assert.True(Passwords.Verify("test-password-123", first));
        Assert.False(Passwords.Verify("incorrect", first));
    }
}
