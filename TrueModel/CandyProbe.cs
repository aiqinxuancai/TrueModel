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
    private static readonly Regex AnswerPattern = new(@"(?<!\d)21(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static bool Passes(string response) => AnswerPattern.IsMatch(response);
}

public sealed record CandyProbeResult(string CandyStatus, string CandyPrompt, string? CandyResponse, string? CandyError,
    string CandyReasoningEffort, long? CandyReasoningTokens);
