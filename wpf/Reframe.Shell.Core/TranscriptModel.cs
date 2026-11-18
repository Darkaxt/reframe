using System.Text;
using System.Text.Json;

namespace Reframe.Shell.Core;

public enum TranscriptItemKind { UserMessage, AssistantText, Thinking, ToolCall, ToolGroup, Compaction, SystemNotice, Unknown }

/// <summary>One row of the chat view.</summary>
public abstract class TranscriptItem
{
    public abstract TranscriptItemKind Kind { get; }
    public string? Uuid { get; init; }
    public string? Timestamp { get; init; }
}

public sealed class UserMessageItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.UserMessage;
    public string Text { get; init; } = "";
}

public sealed class AssistantTextItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.AssistantText;
    public string Text { get; init; } = "";
}

public sealed class ThinkingItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.Thinking;
    public string Text { get; init; } = "";
}

public sealed class CompactionItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.Compaction;
    public string? Text { get; init; }
}

public sealed class SystemNoticeItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.SystemNotice;
    public string Text { get; init; } = "";
}

/// <summary>A shape the model does not know: a generic row showing the raw type name.</summary>
public sealed class UnknownItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.Unknown;
    public string RawKind { get; init; } = "";
}

/// <summary>A tool call. Result stays null while the call is pending. Mutated in place when the result arrives.</summary>
public sealed class ToolCallItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.ToolCall;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Short text for the collapsed row (command, file path, pattern ...).</summary>
    public string Summary { get; init; } = "";
    public string? Result { get; internal set; }
    public bool IsError { get; internal set; }
    public bool IsPending => Result is null;
    /// <summary>Subagent (sidechain) items spawned by this call, in arrival order. Flat, not grouped.</summary>
    public List<TranscriptItem> Children { get; } = new();
}

/// <summary>Consecutive tool calls folded into one row.</summary>
public sealed class ToolGroupItem : TranscriptItem
{
    public override TranscriptItemKind Kind => TranscriptItemKind.ToolGroup;
    public string Label { get; init; } = "";
    public IReadOnlyList<ToolCallItem> Calls { get; init; } = Array.Empty<ToolCallItem>();
}

/// <summary>
/// Incremental model of a transcript. Feed it lines with <see cref="Append"/>; read <see cref="Items"/>. Not thread safe.
/// Sidechain choice: a sidechain line is nested as Children of the Task/Agent tool call that spawned it, found by
/// following parentUuid to the assistant line holding that call (and onward through earlier sidechain lines).
/// When no such call is found the items go to the flat <see cref="SidechainItems"/> list.
/// </summary>
public sealed class TranscriptModel
{
    readonly List<TranscriptItem> top = new();
    readonly List<TranscriptItem> sidechain = new();
    readonly Dictionary<string, ToolCallItem> calls = new();
    readonly Dictionary<string, (string Text, bool IsError)> orphanResults = new();
    // uuid of a line -> the Task/Agent call that owns it (assistant line with the call, or a sidechain line below it)
    readonly Dictionary<string, ToolCallItem> owners = new();

    public int MalformedLines { get; private set; }

    /// <summary>Sidechain items that could not be tied to a parent tool call.</summary>
    public IReadOnlyList<TranscriptItem> SidechainItems => sidechain.ToArray();

    /// <summary>Top-level items with consecutive tool calls folded into groups. A snapshot; later appends do not change the list (tool call objects do update).</summary>
    public IReadOnlyList<TranscriptItem> Items => BuildItems();

    public void Reset()
    {
        top.Clear();
        sidechain.Clear();
        calls.Clear();
        orphanResults.Clear();
        owners.Clear();
        MalformedLines = 0;
    }

    public void Append(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                AppendLine(line);
            }
            catch (Exception)
            {
                MalformedLines++;
            }
        }
    }

    void AppendLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            MalformedLines++;
            return;
        }

        string? type = Str(root, "type");
        string? uuid = Str(root, "uuid");
        string? parent = Str(root, "parentUuid");
        string? ts = Str(root, "timestamp");
        bool isSide = Bool(root, "isSidechain");

        var produced = new List<TranscriptItem>();
        switch (type)
        {
            case "summary":
                produced.Add(new CompactionItem { Uuid = uuid, Timestamp = ts, Text = Str(root, "summary") });
                break;
            case "system":
                if (Str(root, "subtype") == "compact_boundary")
                    produced.Add(new CompactionItem { Uuid = uuid, Timestamp = ts, Text = Str(root, "content") });
                else
                    produced.Add(new SystemNoticeItem { Uuid = uuid, Timestamp = ts, Text = Str(root, "content") ?? Str(root, "subtype") ?? "system" });
                break;
            case "user":
                if (Bool(root, "isCompactSummary"))
                    produced.Add(new CompactionItem { Uuid = uuid, Timestamp = ts, Text = MessageText(root) });
                else
                    ParseUser(root, uuid, ts, produced);
                break;
            case "assistant":
                ParseAssistant(root, uuid, ts, produced);
                break;
            default:
                produced.Add(new UnknownItem { Uuid = uuid, Timestamp = ts, RawKind = type ?? "(none)" });
                break;
        }

        if (produced.Count == 0 && !isSide) return;

        if (!isSide)
        {
            top.AddRange(produced);
            // Remember which Task/Agent call this line holds, so sidechain lines can find it.
            if (uuid is not null)
            {
                foreach (var item in produced)
                    if (item is ToolCallItem c && IsSubagentTool(c.Name)) owners[uuid] = c;
            }
            return;
        }

        ToolCallItem? owner = null;
        if (parent is not null) owners.TryGetValue(parent, out owner);
        if (owner is not null)
        {
            owner.Children.AddRange(produced);
            if (uuid is not null) owners[uuid] = owner;
        }
        else
        {
            sidechain.AddRange(produced);
        }
    }

    void ParseUser(JsonElement root, string? uuid, string? ts, List<TranscriptItem> produced)
    {
        if (!TryContent(root, out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            var s = content.GetString();
            if (!string.IsNullOrWhiteSpace(s)) produced.Add(new UserMessageItem { Uuid = uuid, Timestamp = ts, Text = s });
            return;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            produced.Add(new UnknownItem { Uuid = uuid, Timestamp = ts, RawKind = "user" });
            return;
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            switch (Str(block, "type"))
            {
                case "tool_result":
                    AttachResult(block);
                    break;
                case "text":
                    var t = Str(block, "text");
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(t);
                    }
                    break;
                case "image":
                    if (text.Length > 0) text.Append('\n');
                    text.Append("[image]");
                    break;
            }
        }
        if (text.Length > 0) produced.Add(new UserMessageItem { Uuid = uuid, Timestamp = ts, Text = text.ToString() });
    }

    void ParseAssistant(JsonElement root, string? uuid, string? ts, List<TranscriptItem> produced)
    {
        if (!TryContent(root, out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            var s = content.GetString();
            if (!string.IsNullOrWhiteSpace(s)) produced.Add(new AssistantTextItem { Uuid = uuid, Timestamp = ts, Text = s });
            return;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            produced.Add(new UnknownItem { Uuid = uuid, Timestamp = ts, RawKind = "assistant" });
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            string? bt = Str(block, "type");
            switch (bt)
            {
                case "text":
                    var t = Str(block, "text");
                    if (!string.IsNullOrWhiteSpace(t)) produced.Add(new AssistantTextItem { Uuid = uuid, Timestamp = ts, Text = t });
                    break;
                case "thinking":
                    var th = Str(block, "thinking");
                    if (!string.IsNullOrWhiteSpace(th)) produced.Add(new ThinkingItem { Uuid = uuid, Timestamp = ts, Text = th });
                    break;
                case "tool_use":
                    var id = Str(block, "id") ?? "";
                    var call = new ToolCallItem
                    {
                        Uuid = uuid,
                        Timestamp = ts,
                        Id = id,
                        Name = Str(block, "name") ?? "(tool)",
                        Summary = block.TryGetProperty("input", out var input) ? Summarize(input) : "",
                    };
                    if (id.Length > 0)
                    {
                        calls[id] = call;
                        if (orphanResults.Remove(id, out var early))
                        {
                            call.Result = early.Text;
                            call.IsError = early.IsError;
                        }
                    }
                    produced.Add(call);
                    break;
                default:
                    produced.Add(new UnknownItem { Uuid = uuid, Timestamp = ts, RawKind = bt ?? "(none)" });
                    break;
            }
        }
    }

    void AttachResult(JsonElement block)
    {
        string? id = Str(block, "tool_use_id");
        if (id is null) return;
        string text = block.TryGetProperty("content", out var c) ? ContentText(c) : "";
        bool isError = Bool(block, "is_error");
        if (calls.TryGetValue(id, out var call))
        {
            call.Result = text;
            call.IsError = isError;
        }
        else
        {
            orphanResults[id] = (text, isError);
        }
    }

    // ---- helpers ----

    static bool TryContent(JsonElement root, out JsonElement content)
    {
        content = default;
        return root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out content);
    }

    static string? MessageText(JsonElement root) =>
        TryContent(root, out var c) ? ContentText(c) : null;

    static string ContentText(JsonElement c)
    {
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(part.GetString());
            }
            else if (part.ValueKind == JsonValueKind.Object && Str(part, "type") == "text" && Str(part, "text") is { } t)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
        }
        return sb.ToString();
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static readonly string[] SummaryKeys = { "command", "file_path", "notebook_path", "path", "pattern", "url", "query", "description", "prompt" };

    static string Summarize(JsonElement input)
    {
        string text = "";
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in SummaryKeys)
            {
                var s = Str(input, key);
                if (!string.IsNullOrWhiteSpace(s)) { text = s; break; }
            }
            if (text.Length == 0) text = input.GetRawText();
        }
        else if (input.ValueKind == JsonValueKind.String)
        {
            text = input.GetString() ?? "";
        }
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > 200 ? text.Substring(0, 200) + "..." : text;
    }

    static bool IsSubagentTool(string name) => name is "Task" or "Agent";

    List<TranscriptItem> BuildItems()
    {
        var result = new List<TranscriptItem>();
        var run = new List<ToolCallItem>();
        foreach (var item in top)
        {
            if (item is ToolCallItem call)
            {
                run.Add(call);
                continue;
            }
            Flush(run, result);
            result.Add(item);
        }
        Flush(run, result);
        return result;
    }

    static void Flush(List<ToolCallItem> run, List<TranscriptItem> result)
    {
        if (run.Count == 0) return;
        result.Add(new ToolGroupItem { Uuid = run[0].Uuid, Timestamp = run[0].Timestamp, Calls = run.ToArray(), Label = Label(run) });
        run.Clear();
    }

    static string Label(List<ToolCallItem> run)
    {
        int n = run.Count;
        string Plural(string one, string many) => n == 1 ? one : many;
        if (run.All(c => c.Name == "Bash"))
            return $"Ran {n} " + Plural("command", "commands");
        if (run.All(c => c.Name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit"))
            return $"Edited {n} " + Plural("file", "files");
        if (run.All(c => c.Name == "Read"))
            return $"Read {n} " + Plural("file", "files");
        if (run.All(c => c.Name is "Grep" or "Glob"))
            return $"Searched {n} " + Plural("time", "times");
        return $"Used {n} " + Plural("tool", "tools");
    }
}
