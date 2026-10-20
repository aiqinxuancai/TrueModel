using TrueModel;

namespace TrueModel.Tests;

internal static class CandyTestPrompts
{
    public static string WithoutWhitespace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    public static bool IsCandy(string? prompt) => prompt is not null && (WithoutWhitespace(prompt) == WithoutWhitespace(CandyProbe.Prompt) || WithoutWhitespace(prompt) == WithoutWhitespace(CandyProbe.EnglishPrompt));
}
