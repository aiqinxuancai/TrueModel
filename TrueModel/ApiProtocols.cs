using System.Text.RegularExpressions;

namespace TrueModel;

public static class ApiProtocols
{
    public const string Auto = "auto";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Inherit = "inherit";

    private static readonly Regex LegacyAnthropicThinkingPattern = new(
        @"claude-(?:3[.-]7|(?:sonnet|opus|haiku)-4(?:[.-](?:0|1|5)|-2025\d{4}|$)|4(?:[.-](?:0|1|5)|-(?:sonnet|opus|haiku)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex GeminiBudgetThinkingPattern = new(
        @"gemini-2[.-]5",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Gemini3Pattern = new(
        @"gemini-3(?:$|[.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValid(string? value) => value is Auto or OpenAi or Anthropic or Gemini;

    public static bool IsValidModelOverride(string? value) => value is null or "" or Inherit or Auto or OpenAi or Anthropic or Gemini;

    public static string Normalize(string? value) => IsValid(value) ? value! : Auto;

    public static string? NormalizeModelOverride(string? value) =>
        value is null or "" or Inherit ? null : IsValid(value) ? value : null;

    public static string Effective(string? siteProtocol, string? modelProtocol) =>
        NormalizeModelOverride(modelProtocol) ?? Normalize(siteProtocol);

    public static string StripQueryAndFragment(string url)
    {
        var trimmed = url.Trim();
        var cut = trimmed.IndexOfAny(['?', '#']);
        return cut >= 0 ? trimmed[..cut] : trimmed;
    }

    public static string? InferFromUrl(string baseUrl)
    {
        var normalized = StripQueryAndFragment(baseUrl).TrimEnd('/');
        if (normalized.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) return Anthropic;
        if (normalized.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return OpenAi;
        if (normalized.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(":streamGenerateContent", StringComparison.OrdinalIgnoreCase)) return Gemini;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase)) return Anthropic;
            if (uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)) return OpenAi;
            if (uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.Contains("/codex", StringComparison.OrdinalIgnoreCase)) return OpenAi;
            if (uri.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsolutePath.Contains("/openai", StringComparison.OrdinalIgnoreCase) ? OpenAi : Gemini;
            if (uri.AbsolutePath.Contains("/openai", StringComparison.OrdinalIgnoreCase)) return OpenAi;
            if (uri.AbsolutePath.Contains("/v1beta", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Contains("/v1alpha", StringComparison.OrdinalIgnoreCase)) return Gemini;
        }
        return null;
    }

    public static string[] Candidates(string baseUrl, string? protocol)
    {
        var effective = Normalize(protocol);
        if (effective != Auto) return [effective];
        return InferFromUrl(baseUrl) switch
        {
            Anthropic => [Anthropic],
            Gemini => [Gemini],
            OpenAi => [OpenAi],
            _ => [OpenAi, Anthropic, Gemini]
        };
    }

    public static bool UsesLegacyAnthropicThinkingBudget(string model) => LegacyAnthropicThinkingPattern.IsMatch(model);

    public static bool UsesGeminiThinkingBudget(string model) => GeminiBudgetThinkingPattern.IsMatch(model);

    public static bool UsesGemini3ThinkingLevel(string model) => Gemini3Pattern.IsMatch(model);

    public static string StripGeminiModelPrefix(string model)
    {
        var clean = model.Trim();
        return clean.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? clean["models/".Length..] : clean;
    }
}
