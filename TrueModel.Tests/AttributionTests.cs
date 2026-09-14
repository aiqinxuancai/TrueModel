using System.Text.Json;
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
        Assert.Equal(13, report.Candidates.Length);
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
    public void PasswordHashIsSaltedAndVerifies()
    {
        var first = Passwords.Hash("test-password-123");
        Assert.NotEqual(first, Passwords.Hash("test-password-123"));
        Assert.True(Passwords.Verify("test-password-123", first));
        Assert.False(Passwords.Verify("incorrect", first));
    }
}
