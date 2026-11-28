using System.Text;

namespace Reframe.Shell.Core;

/// <summary>read_thread's answer: the last turns of a thread's conversation as plain text (what was asked, what it
/// answered, the tools it ran with a short result), for another thread checking on it.</summary>
public static class ThreadDigest
{
    public const int DefaultTurns = 10;
    public const int MaxTurns = 100;

    /// <summary>The longest the digest gets: the oldest turns are left out beyond it.</summary>
    public const int MaxChars = 60_000;

    const int TextLimit = 4_000;
    const int ResultLimit = 300;

    /// <summary>The last <paramref name="turns"/> turns of <paramref name="items"/> (a turn starts at a user message),
    /// oldest first, within <see cref="MaxChars"/>.</summary>
    public static string Render(IReadOnlyList<ChatItem> items, int turns)
    {
        var blocks = new List<StringBuilder>();
        foreach (var item in items)
        {
            if (item is UserMessageItem || blocks.Count == 0) blocks.Add(new StringBuilder());
            Write(blocks[^1], item);
        }
        var kept = new List<string>();
        var total = 0;
        for (var i = blocks.Count - 1; i >= 0 && kept.Count < Math.Clamp(turns, 1, MaxTurns); i--)
        {
            var text = blocks[i].ToString().TrimEnd();
            if (text.Length == 0) continue;
            if (total + text.Length > MaxChars && kept.Count > 0) break;
            kept.Add(text);
            total += text.Length;
        }
        kept.Reverse();
        return string.Join("\n\n", kept);
    }

    static void Write(StringBuilder to, ChatItem item)
    {
        switch (item)
        {
            case UserMessageItem u:
                to.Append("### User").Append(At(u)).Append('\n').Append(Cut(u.Text, TextLimit)).Append("\n\n");
                break;
            case AssistantTextItem a:
                to.Append("### Assistant").Append(At(a)).Append('\n').Append(Cut(a.Text, TextLimit)).Append("\n\n");
                break;
            case ToolGroupItem g:
                foreach (var call in g.Calls) Write(to, call);
                break;
            case ToolCallItem c:
                to.Append("- ").Append(c.Name);
                if (c.Summary.Length > 0) to.Append(": ").Append(Cut(c.Summary, ResultLimit));
                to.Append(c.IsPending ? " (running)" : c.IsError ? " (failed)" : "");
                if (c.Result is { Length: > 0 } result) to.Append("\n  → ").Append(Cut(result, ResultLimit).Replace("\n", "\n    "));
                to.Append('\n');
                break;
            case CompactionItem:
                to.Append("(the conversation was compacted here)\n\n");
                break;
            case SystemNoticeItem s:
                to.Append("(").Append(Cut(s.Text, ResultLimit)).Append(")\n\n");
                break;
        }
    }

    static string At(ChatItem item) =>
        DateTimeOffset.TryParse(item.Timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? $" ({t.ToLocalTime():yyyy-MM-dd HH:mm})" : "";

    static string Cut(string text, int limit)
    {
        text = text.Trim();
        return text.Length <= limit ? text : text[..limit] + $"… ({text.Length - limit} more characters)";
    }
}
