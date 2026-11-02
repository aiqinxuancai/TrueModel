using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueModel;

public sealed record PromptTranslation(string Attribution, string Hlwy, string Candy, string[] Keywords);

public static class PromptLanguages
{
    private static readonly Dictionary<string, PromptTranslation> Translations = JsonSerializer.Deserialize<Dictionary<string, PromptTranslation>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "prompt_languages.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    public static bool IsValid(string? language) => language is not null && Translations.ContainsKey(language);
    public static PromptTranslation Get(string language) => Translations.TryGetValue(language, out var value)
        ? value : throw new ArgumentException("不支持的题目语言", nameof(language));

    // Add spaces and blank lines without deleting text, splitting numbers or
    // touching surrogate pairs. Generate once per question, before any retries.
    public static string RandomizeWhitespace(string prompt)
    {
        var positions = Enumerable.Range(1, Math.Max(0, prompt.Length - 1))
            .Where(i => char.IsLetter(prompt[i - 1]) && char.IsLetter(prompt[i])).ToArray();
        RandomNumberGenerator.Shuffle(positions.AsSpan());
        var spaces = positions.Take(12).ToHashSet();
        var lines = positions.Skip(12).Take(2).ToHashSet();
        // Short prompts still get both kinds of variation.
        if (lines.Count == 0) lines.UnionWith(positions.Take(1));
        var result = new StringBuilder(prompt.Length + 48);
        for (var i = 0; i < prompt.Length; i++)
        {
            if (spaces.Contains(i)) result.Append(' ', RandomNumberGenerator.GetInt32(1, 4));
            if (lines.Contains(i)) result.Append('\n', RandomNumberGenerator.GetInt32(2, 4));
            result.Append(prompt[i]);
        }
        return result.ToString();
    }
}
