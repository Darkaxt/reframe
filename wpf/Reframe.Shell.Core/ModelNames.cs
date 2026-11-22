using System.Globalization;

namespace Reframe.Shell.Core;

/// <summary>Models by their commercial names: "opus" is Claude Opus, "gpt-6.1-sol-fast" is GPT-6.1 Sol Fast. What /model is
/// typed with stays the id.</summary>
public static class ModelNames
{
    public static readonly IReadOnlyList<string> Claude = ["default", "fable", "opus", "sonnet", "haiku"];
    /// <summary>Gemini's models (Google's Antigravity CLI, agy), by family: the thread's effort picks agy's variant
    /// (gemini-4-neon with effort high runs gemini-4-neon-high; spawn_plan's agy_model, AgyModel here).</summary>
    public static readonly IReadOnlyList<string> Gemini = ["gemini-4-neon", "gemini-4-hydrogen", "gemini-4-argon", "gemini-3.8-flash"];
    static readonly string[] AgyLevels = ["low", "medium", "high", "xhigh"];

    /// <summary>A Gemini model without the effort agy's id carries (gemini-4-neon-xhigh is gemini-4-neon).</summary>
    public static string GeminiFamily(string id) =>
        Gemini.FirstOrDefault(f => AgyLevels.Any(l => id.Equals(f + "-" + l, StringComparison.OrdinalIgnoreCase))) ?? id;

    /// <summary>agy's id for a Gemini family and an effort (as spawn_plan's agy_model): Neon and Hydrogen go up to xhigh,
    /// the others to high; no effort is high. Any other id is returned as it is.</summary>
    public static string AgyModel(string model, string? effort)
    {
        if (!Gemini.Contains(model)) return model;
        var top = model is "gemini-4-neon" or "gemini-4-hydrogen" ? "xhigh" : "high";
        return model + "-" + (effort switch { "low" => "low", "medium" => "medium", "xhigh" or "max" => top, _ => "high" });
    }
    public static readonly IReadOnlyList<string> OpenAi = ["default", "gpt-6.2-sol", "gpt-6.2-sol-fast", "gpt-6.2-astra", "gpt-6.2-astra-fast", "gpt-6.1-sol", "gpt-6.1-sol-fast", "gpt-6.1-terra", "gpt-6.1-terra-fast"];

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
        else if (parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase) || parts[0] is "fable" or "opus" or "sonnet" or "haiku")
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
