using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>The model and reasoning effort a thread runs with, as its tile header shows them ("Opus 5.5 · high").</summary>
public sealed record ModelInUse(string? Model, string? Effort);

public static class ModelBadge
{
    /// <summary>What Claude Code's status line input tells: model.display_name (or model.id) and effort.level
    /// (absent for a model without effort levels). Null when the payload names neither.</summary>
    public static ModelInUse? FromStatusLine(JsonNode? payload)
    {
        if (payload is not JsonObject o) return null;
        var model = Text(o["model"]?["display_name"]) ?? Text(o["model"]?["id"]);
        var effort = Text(o["effort"]?["level"]);
        return model is null && effort is null ? null : new ModelInUse(model, effort);
    }

    /// <summary>The header's text and tooltip: what the running session reported last, else what it was launched
    /// with. Null when nothing is known.</summary>
    public static (string Text, string Tooltip, bool Low)? Describe(ModelInUse? live, string? launchModel, string? launchEffort)
    {
        var model = live?.Model ?? launchModel;
        // Claude Code leaves the effort out for a model it knows no levels of (a proxied one): the launch choice then.
        var effort = live?.Effort ?? launchEffort;
        if (model is null && effort is null) return null;
        var text = string.Join(" · ", new[] { model ?? "default model", effort is null ? null : $"{effort} effort" }.OfType<string>());
        var source = live is null ? "as launched (the session has not reported yet)"
            : live.Effort is null && effort is not null ? "the model as the session reports it now, the effort as launched"
            : "as the running session reports them (its last turn)";
        return (text, $"Model and reasoning effort, {source}", effort is "low" or "minimal");
    }

    /// <summary>The effort (and model) of the last assistant turn in a Claude Code transcript's tail: each assistant entry
    /// carries the "effort" its request ran with. Null when the tail has none.</summary>
    public static ModelInUse? FromTranscriptTail(string tail)
    {
        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"effort\"", StringComparison.Ordinal) || !line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
            JsonNode? entry;
            try { entry = JsonNode.Parse(line); }
            catch (System.Text.Json.JsonException) { continue; }
            if (entry is not JsonObject o || Text(o["type"]) != "assistant" || Text(o["effort"]) is not { } effort) continue;
            return new ModelInUse(Text(o["message"]?["model"]), effort);
        }
        return null;
    }

    /// <summary>The last <paramref name="bytes"/> of a file, read while Claude Code keeps writing it; null if unreadable.</summary>
    public static string? ReadTail(string path, int bytes = 256 * 1024)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, file.Length - bytes);
            file.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[file.Length - start];
            var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The status line's model with the transcript's effort (what the last turn ran with) when it has one.</summary>
    public static ModelInUse? Combine(ModelInUse? statusLine, ModelInUse? transcript) =>
        statusLine is null && transcript is null ? null
            : new ModelInUse(statusLine?.Model ?? transcript?.Model, transcript?.Effort ?? statusLine?.Effort);

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
}
