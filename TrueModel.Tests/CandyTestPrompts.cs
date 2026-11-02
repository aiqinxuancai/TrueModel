using TrueModel;

namespace TrueModel.Tests;

internal static class CandyTestPrompts
{
    public static string WithoutWhitespace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    public static bool IsCandy(string? prompt) => prompt is not null && new[] { "zh", "en", "ja", "ko", "fr", "es", "de", "pt" }.Any(language => WithoutWhitespace(prompt) == WithoutWhitespace(PromptLanguages.Get(language).Candy));
}
