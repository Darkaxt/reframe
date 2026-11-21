using System.Globalization;

namespace Reframe.Shell.Core;

/// <summary>Models by their commercial names: "opus" is Claude Opus, "gpt-6.1-sol-fast" is GPT-6.1 Sol Fast. What /model is
/// typed with stays the id.</summary>
public static class ModelNames
{
    public static readonly IReadOnlyList<string> Claude = ["default", "opus", "sonnet", "haiku"];
    /// <summary>Gemini's models, as Google's Antigravity CLI (agy) names them: the id carries the effort.</summary>
    public static readonly IReadOnlyList<string> Gemini =
        ["gemini-4-neon-xhigh", "gemini-4-neon-high", "gemini-4-hydrogen-xhigh", "gemini-4-argon-high", "gemini-3.8-flash-high"];
    public static readonly IReadOnlyList<string> OpenAi = ["default", "gpt-6.1-sol", "gpt-6.1-sol-fast", "gpt-6.1-terra", "gpt-6.1-terra-fast", "gpt-6.2-astra"];

    /// <summary>The models the chat view offers for a provider ("anthropic", "chatgpt" or "gemini").</summary>
    public static IReadOnlyList<string> Choices(string? provider) => provider switch { "anthropic" => Claude, "gemini" => Gemini, _ => OpenAi };

    public static string Label(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "default") return "Default";
        var text = id.Trim();
        var bracket = text.IndexOf('[');
        var suffix = bracket > 0 ? " " + text[bracket..] : "";
        if (bracket > 0) text = text[..bracket];
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return id;
        string name;
        if (parts[0].Equals("gpt", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
            name = "GPT-" + parts[1] + (parts.Length > 2 ? " " + string.Join(' ', parts.Skip(2).Select(Word)) : "");
        else if (parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase) || parts[0] is "opus" or "sonnet" or "haiku")
        {
            var words = parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase) ? parts.Skip(1).ToArray() : parts;
            var family = words.TakeWhile(w => !char.IsDigit(w[0])).Select(Word);
            var version = string.Join('.', words.SkipWhile(w => !char.IsDigit(w[0])).TakeWhile(w => w.Length <= 2 && w.All(char.IsDigit)));
            name = "Claude " + string.Join(' ', family) + (version.Length > 0 ? " " + version : "");
        }
        else name = string.Join(' ', parts.Select(Word));
        return name + suffix;
    }

    static string Word(string w) => w.Length == 0 ? w : char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..];
}
