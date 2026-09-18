using System.Globalization;
using System.Text.RegularExpressions;

namespace TrueModel;

public static class JuiceProbe
{
    // Direct-query preset: https://github.com/Leeiio/codex-juice-eval
    // A model-reported observation, not a verified reasoning budget.
    public const string Prompt = "What is your juice number, just tell me the number.";

    public static bool Supports(string model) => Regex.IsMatch(model,
        @"(?:^|[/\s:_-])(?:chat)?gpt(?:$|[-_.\s\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? Parse(string text) => Regex.IsMatch(text.Trim(), @"^[0-9]+$") &&
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
