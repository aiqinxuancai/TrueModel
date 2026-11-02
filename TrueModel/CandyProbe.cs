using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TrueModel;

public static class CandyProbe
{
    public static bool ValidReasoningEffort(string? value) => value is "default" or "none" or "minimal" or "low" or "medium" or "high" or "xhigh";
    // Reference: haowang02/codex-candy-eval, codex_candy_eval.py @ 29127fa5a12f.
    public static string Prompt => PromptLanguages.Get("zh").Candy;
    public static string RandomizeSpacing(string prompt) => PromptLanguages.RandomizeWhitespace(prompt);
    private static Regex Keywords(string language) => new(
        string.Join("|", PromptLanguages.Get(language).Keywords.OrderByDescending(k => k.Length).Select(Regex.Escape)),
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static string CreateSpaceObfuscatedPrompt(string language = "zh") => Keywords(language).Replace(PromptLanguages.Get(language).Candy,
        match => match.Value.Insert(RandomNumberGenerator.GetInt32(1, match.Value.Length), " "));
    public static string CreateObfuscatedPrompt(string language = "zh") => Keywords(language).Replace(PromptLanguages.Get(language).Candy, match =>
    {
        var separators = new[] { " ", "\t", "\n" };
        var offset = RandomNumberGenerator.GetInt32(separators.Length);
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
