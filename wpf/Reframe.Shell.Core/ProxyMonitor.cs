using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

public enum ProxyState { Unknown, Up, Down }

/// <summary>What a check changed: nothing, only what the sidebar shows, or something to tell the user.</summary>
public enum ProxyChange { None, Display, Down, Recovered }

/// <summary>
/// Whether the local proxy the ChatGPT threads talk to answers. The address is the loopback one in the user's Claude
/// settings (<c>env.ANTHROPIC_BASE_URL</c>, the only key read); a check that fails twice in a row means down, so a
/// single missed check does not raise it.
/// </summary>
public sealed class ProxyWatch
{
    const int FailuresToDown = 2;
    int failures;

    public ProxyState State { get; private set; }

    /// <summary>The health address of the proxy named in the settings file's text, or null when it names none on this machine.</summary>
    public static Uri? HealthUrl(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            if (JsonNode.Parse(settingsJson) is not JsonObject root || root["env"] is not JsonObject env) return null;
            if (env["ANTHROPIC_BASE_URL"] is not JsonValue value || !value.TryGetValue(out string? text)) return null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !url.IsLoopback) return null;
            return new Uri(url, "/healthz");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Takes one check's outcome.</summary>
    public ProxyChange Observe(bool answered)
    {
        var was = State;
        if (answered)
        {
            failures = 0;
            State = ProxyState.Up;
            return was == ProxyState.Down ? ProxyChange.Recovered : was == ProxyState.Up ? ProxyChange.None : ProxyChange.Display;
        }
        if (++failures < FailuresToDown) return ProxyChange.None;
        State = ProxyState.Down;
        return was == ProxyState.Down ? ProxyChange.None : ProxyChange.Down;
    }

    /// <summary>The proxy is no longer watched (the settings name no address): nothing to show.</summary>
    public void Reset()
    {
        failures = 0;
        State = ProxyState.Unknown;
    }
}
