using System.Globalization;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>One process of a session's job (<c>monitor.sample</c>).</summary>
public sealed record ProcessUsage(int Pid, string Name, double Cpu, long Memory, double Gpu, long StartedAt);

/// <summary>What a running session uses (<c>monitor.sample</c>): CPU and GPU in percent of the machine, memory in
/// private bytes, its processes.</summary>
public sealed record SessionUsage(double Cpu, long Memory, double Gpu, IReadOnlyList<ProcessUsage> Processes)
{
    /// <summary>The <c>sessions</c> of a <c>monitor.sample</c> result, by session id (entries it cannot read are left out).</summary>
    public static Dictionary<string, SessionUsage> ParseSample(JsonNode? result)
    {
        var usage = new Dictionary<string, SessionUsage>();
        if (result?["sessions"] is not JsonObject sessions) return usage;
        foreach (var (id, node) in sessions)
        {
            if (node is not JsonObject entry) continue;
            var processes = (entry["processes"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new ProcessUsage(
                Int(p["pid"]), (string?)p["name"] ?? "", Number(p["cpu"]), Long(p["memory"]), Number(p["gpu"]), Long(p["startedAt"]))).ToList();
            usage[id] = new SessionUsage(Number(entry["cpu"]), Long(entry["memory"]), Number(entry["gpu"]), processes);
        }
        return usage;
    }

    static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    static long Long(JsonNode? node) => (long)Number(node);
    static int Int(JsonNode? node) => (int)Number(node);

    /// <summary>"3%": a percentage as the meters show it (under half a percent but not idle: "&lt;1%").</summary>
    public static string Percent(double value) =>
        value > 0 && value < 0.5 ? "<1%" : Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>"412 MB", "1.2 GB": bytes as the meters show them.</summary>
    public static string Bytes(long bytes)
    {
        const double mb = 1024 * 1024;
        return bytes >= 1024 * mb
            ? (bytes / (1024 * mb)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Round(bytes / mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>The tooltip: the totals, then the busiest processes (by CPU, then memory), at most <paramref name="max"/>.</summary>
    public string Describe(int max = 12)
    {
        var lines = new List<string>
        {
            $"CPU {Percent(Cpu)} · GPU {Percent(Gpu)} · RAM {Bytes(Memory)}",
            Processes.Count == 1 ? "1 process" : $"{Processes.Count} processes",
        };
        foreach (var p in Processes.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Gpu).ThenByDescending(p => p.Memory).Take(max))
            lines.Add($"{(p.Name.Length > 0 ? p.Name : "?")} ({p.Pid}): CPU {Percent(p.Cpu)}{(p.Gpu > 0 ? $", GPU {Percent(p.Gpu)}" : "")}, {Bytes(p.Memory)}");
        if (Processes.Count > max) lines.Add($"… and {Processes.Count - max} more");
        return string.Join("\n", lines);
    }
}
