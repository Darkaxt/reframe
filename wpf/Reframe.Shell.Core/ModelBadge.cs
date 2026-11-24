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
            : "as the session reports it now";
        return (text, $"Model and reasoning effort, {source}", effort is "low" or "minimal");
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
}
