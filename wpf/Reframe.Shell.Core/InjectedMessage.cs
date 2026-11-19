namespace Reframe.Shell.Core;

/// <summary>A "user" line in the transcript that the user did not type: another session's message or a scheduled task's
/// prompt. The chat view shows these as a card of their own, not as the user's bubble.</summary>
public sealed record InjectedMessage(string Title, string Body)
{
    const string CrossStart = "<cross-session-message";
    const string CrossEnd = "</cross-session-message>";

    public static InjectedMessage? Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("Another Claude session sent a message", StringComparison.Ordinal) && trimmed.IndexOf(CrossStart, StringComparison.Ordinal) is var open and >= 0)
        {
            var tagEnd = trimmed.IndexOf('>', open);
            if (tagEnd < 0) return null;
            var tag = trimmed[open..tagEnd];
            var end = trimmed.IndexOf(CrossEnd, tagEnd, StringComparison.Ordinal);
            var body = (end < 0 ? trimmed[(tagEnd + 1)..] : trimmed[(tagEnd + 1)..end]).Trim();
            var name = Attribute(tag, "from-name");
            return new InjectedMessage(name is { Length: > 0 } ? $"Message from {name}" : "Message from another session", body);
        }
        if (trimmed.StartsWith("[Reframe scheduled task:", StringComparison.Ordinal) && trimmed.IndexOf("]\n", StringComparison.Ordinal) is var close and > 0)
        {
            var head = trimmed[1..close];
            var title = head["Reframe scheduled task:".Length..].Trim();
            var paren = title.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && title.EndsWith(')')) title = title[..paren];
            return new InjectedMessage($"Scheduled task: {title}", trimmed[(close + 2)..].Trim());
        }
        return null;
    }

    static string? Attribute(string tag, string name)
    {
        var key = name + "=\"";
        var at = tag.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var from = at + key.Length;
        var to = tag.IndexOf('"', from);
        return to < 0 ? null : tag[from..to];
    }
}
