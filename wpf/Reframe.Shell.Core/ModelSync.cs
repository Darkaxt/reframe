namespace Reframe.Shell.Core;

/// <summary>A thread's model as it runs: a /model typed in its terminal (or the chat chip) changes only the session, so Reframe reads
/// the model its transcript last used and keeps it in the thread's record, where the next start takes it from.</summary>
public static class ModelSync
{
    static readonly string[] Families = ["opus", "sonnet", "haiku"];

    /// <summary>The model to store when a session ran <paramref name="seen"/> while its record says <paramref name="stored"/>; null
    /// when they are the same model (an alias such as "opus" stands for any version of it, and a "[1m]" suffix is ignored) or
    /// nothing usable was seen. A record without a model takes the one seen.</summary>
    public static string? Changed(string? stored, string? seen)
    {
        if (string.IsNullOrWhiteSpace(seen) || seen.StartsWith('<')) return null;
        seen = seen.Trim();
        var plain = (stored ?? "").Trim();
        var bracket = plain.IndexOf('[');
        if (bracket > 0) plain = plain[..bracket];
        if (plain.Length == 0 || plain.Equals("default", StringComparison.OrdinalIgnoreCase)) return seen;
        if (Families.Contains(plain, StringComparer.OrdinalIgnoreCase))
            return seen.Contains(plain, StringComparison.OrdinalIgnoreCase) ? null : seen;
        return plain.Equals(seen, StringComparison.OrdinalIgnoreCase) ? null : seen;
    }
}
