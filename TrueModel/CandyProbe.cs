using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TrueModel;

public static class CandyProbe
{
    public static bool ValidReasoningEffort(string? value) => value is "default" or "none" or "minimal" or "low" or "medium" or "high" or "xhigh";
    // Reference: haowang02/codex-candy-eval, codex_candy_eval.py @ 29127fa5a12f.
    public const string Prompt = """
        外部ツールを一切使わずに、次の問題に答えてください。

        黒い袋の中に、3種類の味のキャンディーが入っています。それぞれの味には丸形と五芒星形の2種類の形があり、形は手触りで区別できます。味と形ごとの個数は下の表のとおりです。参加者は、キャンディーを取り出す個数を事前に決める必要があります。形の異なるリンゴ味とモモ味のキャンディーを同時に手元に持っていることを確実にするには、最少で何個取り出せばよいでしょうか？（丸形のリンゴ味と五芒星形のモモ味の組み合わせ、または丸形のモモ味と五芒星形のリンゴ味の組み合わせの、どちらでも条件を満たします。）

                 リンゴ味  モモ味  スイカ味
        丸形         7       9       8
        五芒星形     7       6       4
        """;
    public static string CreateSpaceObfuscatedPrompt() => Keywords.Replace(Prompt,
        match => match.Value.Insert(Random.Shared.Next(1, match.Value.Length), " "));

    // Vary every new candy question, including the first attempt. Keep numbers,
    // existing whitespace, and surrogate pairs intact; retries reuse the result.
    public static string RandomizeSpacing(string prompt)
    {
        var positions = Enumerable.Range(1, Math.Max(0, prompt.Length - 1))
            .Where(i => char.IsLetter(prompt[i - 1]) && char.IsLetter(prompt[i])).ToArray();
        RandomNumberGenerator.Shuffle(positions.AsSpan());
        var selected = positions.Take(12).ToHashSet();
        var result = new StringBuilder(prompt.Length + 36);
        for (var i = 0; i < prompt.Length; i++)
        {
            if (selected.Contains(i)) result.Append(' ', RandomNumberGenerator.GetInt32(1, 4));
            result.Append(prompt[i]);
        }
        return result.ToString();
    }

    // Split only words, preserving every non-whitespace character and all counts.
    private static readonly Regex Keywords = new("キャンディー|リンゴ味|モモ味|スイカ味|五芒星形|丸形|最少", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
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
