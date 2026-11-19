using System.Text.Json;

namespace Reframe.Shell.Core;

/// <summary>Forking a thread at a point of the conversation: a new session whose history is the old transcript up to that point.
/// The old transcript is only read; the prefix is written as the new session's own transcript.</summary>
public static class TranscriptFork
{
    /// <summary>The lines up to the message with <paramref name="uuid"/> (before it, or including it), with the session id
    /// replaced, and without a last answer that asks for a tool result the prefix does not have. Null when the message is not found.</summary>
    public static List<string>? Cut(IReadOnlyList<string> lines, string uuid, bool before, string oldSessionId, string newSessionId)
    {
        var at = -1;
        for (var i = 0; i < lines.Count && at < 0; i++)
            if (Field(lines[i], "uuid") == uuid) at = i;
        if (at < 0) return null;
        var kept = lines.Take(before ? at : at + 1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        // A tool call without its result cannot be resumed: drop trailing assistant lines that still wait for one.
        var results = new HashSet<string>();
        foreach (var l in kept) foreach (var id in ToolIds(l, "tool_result", "tool_use_id")) results.Add(id);
        while (kept.Count > 0)
        {
            var last = kept[^1];
            var open = Field(last, "type") == "assistant" && ToolIds(last, "tool_use", "id").Any(id => !results.Contains(id));
            if (!open) break;
            kept.RemoveAt(kept.Count - 1);
        }
        return [.. kept.Select(l => l.Replace(oldSessionId, newSessionId, StringComparison.Ordinal))];
    }

    static string? Field(string line, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static IEnumerable<string> ToolIds(string line, string blockType, string idField)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var block in c.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.GetString() == blockType
                        && block.TryGetProperty(idField, out var id) && id.GetString() is { } s) ids.Add(s);
        }
        catch (JsonException) { }
        return ids;
    }
}
