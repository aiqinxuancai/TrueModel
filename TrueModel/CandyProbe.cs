using System.Text.RegularExpressions;

namespace TrueModel;

public static class CandyProbe
{
    public static bool ValidReasoningEffort(string? value) => value is "default" or "none" or "minimal" or "low" or "medium" or "high" or "xhigh";
    // Reference: haowang02/codex-candy-eval, codex_candy_eval.py @ 29127fa5a12f.
    public const string Prompt = """
        不使用任何外部工具回答以下问题：

        在一个黑色的袋子里放有三种口味的糖果，每种糖果有两种不同的形状（圆形和五角星形，不同的形状靠手感可以分辨）。现已知不同口味的糖和不同形状的数量统计如下表。参赛者需要在活动前决定摸出的糖果数目，那么，最少取出多少个糖果才能保证手中同时拥有不同形状的苹果味和桃子味的糖？（同时手中有圆形苹果味匹配五角星桃子味糖果，或者有圆形桃子味匹配五角星苹果味糖果都满足要求）

                苹果味  桃子味  西瓜味
        圆形       7      9      8
        五角星形   7      6      4
        """;
    public const string EnglishPrompt = """
        Answer the following question without using any external tools:

        A black bag contains candies in three flavors, with each flavor available in two shapes: round and five-pointed star. The shapes can be distinguished by touch. The counts for each flavor and shape are shown in the table below. A contestant must decide in advance how many candies to draw. What is the minimum number of candies that must be drawn to guarantee having both an apple-flavored candy and a peach-flavored candy with different shapes? Either a round apple-flavored candy paired with a five-pointed star peach-flavored candy, or a round peach-flavored candy paired with a five-pointed star apple-flavored candy, satisfies the requirement.

        Shape                Apple  Peach  Watermelon
        Round                  7      9       8
        Five-pointed star      7      6       4
        """;
    private static readonly Regex EnglishKeywords = new(@"\b(candies|candy|apple|peach|watermelon|round|star|minimum|shapes?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Split near the start so a plural ending cannot leave the whole keyword intact.
    public static string CreateEnglishObfuscatedPrompt() => EnglishKeywords.Replace(EnglishPrompt,
        match => match.Value.Insert(Random.Shared.Next(1, Math.Min(4, match.Value.Length)), " "));

    // Split only words, preserving every non-whitespace character and all counts.
    private static readonly Regex Keywords = new("糖果|苹果味|桃子味|西瓜味|五角星形|五角星|圆形|最少取出", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static string CreateObfuscatedPrompt() => Keywords.Replace(Prompt, match =>
    {
        var separators = new[] { " ", "\t", "\n" };
        var offset = Random.Shared.Next(separators.Length);
        return string.Concat(match.Value.Select((character, index) =>
            (index == 0 ? "" : separators[(offset + index - 1) % separators.Length]) + character));
    });
    private static readonly Regex AnswerPattern = new(@"(?<!\d)21(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static bool Passes(string response) => AnswerPattern.IsMatch(response);
}

public sealed record CandyProbeResult(string CandyStatus, string CandyPrompt, string? CandyResponse, string? CandyError,
    string CandyReasoningEffort, long? CandyReasoningTokens, long? CandyLatencyMs = null,
    string? Candy2Status = null, string? Candy2Prompt = null, string? Candy2Response = null, string? Candy2Error = null, long? Candy2ReasoningTokens = null, long? Candy2LatencyMs = null,
    string? Candy3Status = null, string? Candy3Prompt = null, string? Candy3Response = null, string? Candy3Error = null, long? Candy3ReasoningTokens = null, long? Candy3LatencyMs = null);
