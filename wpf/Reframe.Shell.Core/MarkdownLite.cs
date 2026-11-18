namespace Reframe.Shell.Core;

/// <summary>A run of inline text: plain, **bold** or `code`.</summary>
public sealed record MdSpan(string Text, bool Bold = false, bool Code = false);

public enum MdBlockKind { Paragraph, Heading, Bullet, Code }

/// <summary>One block: a paragraph, a heading (Level 1-3), a list item (Marker is "•" or "1."), or a fenced code block (Code text in Raw).</summary>
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdSpan> Spans, int Level = 0, string Marker = "", string Raw = "");

/// <summary>The small part of Markdown the chat view shows: headings, bullet and numbered items, fenced code, bold and inline code.
/// Anything else stays plain text, so an unknown construct is never lost.</summary>
public static class MarkdownLite
{
    public static List<MdBlock> Parse(string text)
    {
        var blocks = new List<MdBlock>();
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, Spans(string.Join("\n", paragraph))));
            paragraph.Clear();
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                blocks.Add(new MdBlock(MdBlockKind.Code, [], Raw: string.Join("\n", code)));
            }
            else if (string.IsNullOrWhiteSpace(line)) Flush();
            else if (Heading(trimmed) is { } level)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Heading, Spans(trimmed[(level + 1)..].Trim()), Math.Min(level, 3)));
            }
            else if (Item(trimmed) is { } item)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, Spans(item.Text), Marker: item.Marker));
            }
            else paragraph.Add(line);
        }
        Flush();
        return blocks;
    }

    static int? Heading(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == '#') n++;
        return n is >= 1 and <= 6 && line.Length > n && line[n] == ' ' ? n : null;
    }

    static (string Marker, string Text)? Item(string line)
    {
        if (line.Length > 2 && line[0] is '-' or '*' or '+' && line[1] == ' ') return ("•", line[2..]);
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits])) digits++;
        return digits is > 0 and < 4 && line.Length > digits + 1 && line[digits] == '.' && line[digits + 1] == ' ' ? (line[..(digits + 1)], line[(digits + 2)..]) : null;
    }

    public static List<MdSpan> Spans(string text)
    {
        var spans = new List<MdSpan>();
        var plain = new System.Text.StringBuilder();
        void Plain()
        {
            if (plain.Length == 0) return;
            spans.Add(new MdSpan(plain.ToString()));
            plain.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Plain();
                spans.Add(new MdSpan(text[(i + 1)..end], Code: true));
                i = end;
            }
            else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var close and > 0)
            {
                Plain();
                spans.Add(new MdSpan(text[(i + 2)..close], Bold: true));
                i = close + 1;
            }
            else plain.Append(text[i]);
        }
        Plain();
        return spans;
    }
}
