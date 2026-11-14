using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>A warning of the host's <c>monitor</c> service (version 2): a process a session started that seems to run
/// away (<c>cpu</c>, <c>scan</c>) or processes left after it ended (<c>leftover</c>). Times are ms since 1970.</summary>
public sealed record MonitorWarning(string Id, string SessionId, string Kind, int Pid, string Name, string CommandLine,
    string Message, long Since, long At, int Processes)
{
    public static MonitorWarning? From(JsonNode? node)
    {
        if (node?["id"] is not { } id || node["sessionId"] is not { } session) return null;
        return new MonitorWarning((string)id!, (string)session!, (string?)node["kind"] ?? "", (int?)node["pid"] ?? 0,
            (string?)node["name"] ?? "", (string?)node["commandLine"] ?? "", (string?)node["message"] ?? "",
            (long?)node["since"] ?? 0, (long?)node["at"] ?? 0, (int?)node["processes"] ?? 0);
    }

    public static List<MonitorWarning> ParseList(JsonNode? result) =>
        (result?["warnings"]?.AsArray() ?? []).Select(From).OfType<MonitorWarning>().ToList();

    /// <summary>"High CPU", "Whole-disk search", "Left running".</summary>
    public string Title => Kind switch
    {
        "cpu" => "High CPU",
        "scan" => "Whole-disk search",
        "leftover" => "Left running",
        _ => "Warning",
    };

    /// <summary>What is typed into the thread for its agent: one line, then Enter.</summary>
    public string Note()
    {
        var command = CommandLine.Length == 0 ? "" : $" Command: {(CommandLine.Length > 300 ? CommandLine[..300] + "…" : CommandLine)}.";
        return $"[Reframe] Warning: {Message}.{command} Please check whether it is still needed and stop it if not.";
    }

    /// <summary>"12 min ago", "just now".</summary>
    public static string Ago(long ms, long now)
    {
        var minutes = (now - ms) / 60000;
        return minutes < 1 ? "just now" : minutes < 60 ? $"{minutes} min ago" : $"{minutes / 60} h {minutes % 60} min ago";
    }
}
