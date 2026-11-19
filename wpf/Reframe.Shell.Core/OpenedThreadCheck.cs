using System.Text;
using System.Text.Json;

namespace Reframe.Shell.Core;

public enum OpenedThreadState { Waiting, Working, Failed }

/// <summary>Whether a thread another session opened (open_thread) got going: its transcript's first answer is either work
/// (text or a tool call) or an API error such as "400 Unknown model", after which nothing it was asked has run.</summary>
public static class OpenedThreadCheck
{
    public static (OpenedThreadState State, string? Error) Check(IEnumerable<string> transcriptLines)
    {
        foreach (var line in transcriptLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "assistant") continue;
                var text = new StringBuilder();
                var tool = false;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
                    else if (content.ValueKind == JsonValueKind.Array)
                        foreach (var block in content.EnumerateArray())
                        {
                            if (Str(block, "type") == "text") text.Append(Str(block, "text"));
                            else if (Str(block, "type") == "tool_use") tool = true;
                        }
                }
                var body = text.ToString().Trim();
                var apiError = root.TryGetProperty("isApiErrorMessage", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (apiError || body.StartsWith("API Error", StringComparison.Ordinal)) return (OpenedThreadState.Failed, body);
                if (tool || body.Length > 0) return (OpenedThreadState.Working, null);
            }
            catch (JsonException)
            {
                // a line still being written
            }
        }
        return (OpenedThreadState.Waiting, null);
    }

    /// <summary>What the opener is told: the thread, the error and what to change.</summary>
    public static string Report(string threadName, string? model, string error)
    {
        var shown = error.Length > 1500 ? error[..1500] + "…" : error;
        var advice = error.Contains("Unknown model", StringComparison.OrdinalIgnoreCase)
            ? $"The model{(model is { Length: > 0 } ? $" \"{model}\"" : "")} is not one the proxy knows (the error lists the ones it does). Close the thread and open it again with `model` set to one of those; for openai pass `model` explicitly rather than relying on the saved default."
            : "Nothing you asked it has run. Close the thread and open it again once the cause is fixed, or tell the user.";
        return $"[Reframe] The thread \"{threadName}\" you opened stopped at its first request, so it never started your task:\n{shown}\n{advice}";
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
