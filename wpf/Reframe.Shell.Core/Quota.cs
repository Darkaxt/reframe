using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

// Subscription quota windows as their sources report them (a port of src/shared/quota.ts): LLM Coordinator's usage
// snapshot for Codex and, from Coordinators that observe it too (`providers`), for Claude. The shell reads them through
// the host's `quota.codex` (docs/ptyhost-protocol.md); it never reads Claude Code's credentials or asks a usage API.

/// <summary>A window: <c>Id</c> stable per source ("five_hour", "codex:10080"); <c>Minutes</c> its length when known;
/// <c>UsedPercent</c> 0..100; <c>ResetsAt</c> ms since the epoch, when known.</summary>
public sealed record QuotaWindow(string Id, double? Minutes, double UsedPercent, double? ResetsAt);

/// <summary>One subscription's windows ("claude" or "codex") and when the source last reported them (ms).</summary>
public sealed record ProviderQuota(string Source, IReadOnlyList<QuotaWindow> Windows, double At);

public sealed record ObservedQuota(IReadOnlyList<QuotaWindow> Windows, double At);

public static class Quota
{
    static double? Finite(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.GetValue<double>() is var d && double.IsFinite(d) ? d : null;

    static double ClampPercent(double value) => Math.Min(100, Math.Max(0, value));

    /// <summary>Seconds or milliseconds since the epoch, or an ISO string, as ms.</summary>
    static double? Time(JsonNode? node)
    {
        if (Finite(node) is { } n) return n < 1e12 ? n * 1000 : n;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUnixTimeMilliseconds();
        return null;
    }

    static readonly Dictionary<string, double> ClaudeMinutes = new() { ["five_hour"] = 300, ["seven_day"] = 10080 };

    static List<QuotaWindow> Shortest(IEnumerable<QuotaWindow> windows) =>
        windows.OrderBy(w => w.Minutes ?? double.PositiveInfinity).ToList();

    /// <summary>parseClaudeRateLimits: <c>rate_limits</c> of Claude Code's status line input.</summary>
    public static List<QuotaWindow> ParseClaudeRateLimits(JsonNode? raw)
    {
        if (raw is not JsonObject o) return [];
        var windows = new List<QuotaWindow>();
        foreach (var (id, entry) in o)
        {
            if (entry is not JsonObject e || Finite(e["used_percentage"]) is not { } used) continue;
            windows.Add(new QuotaWindow(id, ClaudeMinutes.TryGetValue(id, out var m) ? m : null, ClampPercent(used), Time(e["resets_at"])));
        }
        return Shortest(windows);
    }

    static JsonNode? Usage(JsonNode? raw) => raw is JsonObject o && o.ContainsKey("usage") && o["usage"] is not null ? o["usage"] : raw;

    /// <summary>parseCoordinatorProviders: the quota per provider (codex, claude) of a <c>usage.snapshot</c> answer, each
    /// window from its last point.</summary>
    public static Dictionary<string, ObservedQuota> ParseCoordinatorProviders(JsonNode? raw)
    {
        var quotas = new Dictionary<string, ObservedQuota>();
        if ((Usage(raw) as JsonObject)?["providers"] is not JsonArray providers) return quotas;
        foreach (var provider in providers.OfType<JsonObject>())
        {
            var source = provider["id"] is JsonValue idValue && idValue.GetValueKind() == JsonValueKind.String ? idValue.GetValue<string>() : null;
            if (source is not ("codex" or "claude") || provider["windows"] is not JsonArray list) continue;
            var at = Finite(provider["observedAt"]);
            var windows = new List<QuotaWindow>();
            foreach (var window in list.OfType<JsonObject>())
            {
                var last = (window["points"] as JsonArray)?.LastOrDefault() as JsonObject;
                if ((Finite(last?["remaining"]) ?? Finite(window["remaining"])) is not { } remaining) continue;
                if (Finite(last?["t"]) is { } t && (at is null || t > at)) at = t;
                var minutes = Finite(window["windowMinutes"]);
                var name = window["id"] is JsonValue w && w.GetValueKind() == JsonValueKind.String ? w.GetValue<string>() : "";
                windows.Add(new QuotaWindow($"{source}:{(minutes is { } m ? Number(m) : name)}", minutes, ClampPercent(100 - remaining), Time(window["resetAt"])));
            }
            if (windows.Count > 0 && at is { } observed) quotas[source] = new ObservedQuota(Shortest(windows), observed);
        }
        return quotas;
    }

    /// <summary>coordinatorQuotaIsNewer: the Coordinator's Claude quota replaces the shown one only when newer.</summary>
    public static bool CoordinatorQuotaIsNewer(ProviderQuota? shown, ObservedQuota observed) => shown is null || observed.At > shown.At;

    /// <summary>parseCoordinatorUsage: the Codex windows (its provider, else an older Coordinator's buckets, the one
    /// observed last per window).</summary>
    public static List<QuotaWindow> ParseCoordinatorUsage(JsonNode? raw)
    {
        if (ParseCoordinatorProviders(raw).TryGetValue("codex", out var codex)) return [.. codex.Windows];
        if ((Usage(raw) as JsonObject)?["buckets"] is not JsonArray buckets) return [];
        var latest = new Dictionary<string, (double At, QuotaWindow Window)>();
        foreach (var bucket in buckets.OfType<JsonObject>())
        {
            var last = (bucket["points"] as JsonArray)?.LastOrDefault() as JsonObject;
            if (Finite(last?["remaining"]) is not { } remaining || Finite(last?["t"]) is not { } at) continue;
            var minutes = Finite(bucket["windowMinutes"]);
            var name = bucket["id"] is JsonValue b && b.GetValueKind() == JsonValueKind.String ? b.GetValue<string>() : "codex";
            var id = $"{name}:{(minutes is { } m ? Number(m) : "")}";
            if (latest.TryGetValue(id, out var prior) && prior.At >= at) continue;
            latest[id] = (at, new QuotaWindow(id, minutes, ClampPercent(100 - remaining), Time(bucket["resetAt"])));
        }
        return Shortest(latest.Values.Select(e => e.Window));
    }

    /// <summary>A number as JavaScript writes it in a string (10080, 1.5).</summary>
    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>windowLabel: "5h", "7d", "90m" ("" without a length).</summary>
    public static string WindowLabel(double? minutes)
    {
        if (minutes is not { } m || m == 0) return "";
        if (m % 1440 == 0) return $"{Number(m / 1440)}d";
        if (m % 60 == 0) return $"{Number(m / 60)}h";
        return $"{Number(m)}m";
    }

    /// <summary>quotaLevel: "ok", "warn" from 70 %, "danger" from 90 %.</summary>
    public static string Level(double usedPercent) => usedPercent >= 90 ? "danger" : usedPercent >= 70 ? "warn" : "ok";

    /// <summary>untilLabel: "3d 4h", "2h 10m", "12m" ("&lt;1m" when due).</summary>
    public static string UntilLabel(double resetsAt, double now)
    {
        var minutes = (long)Math.Max(0, Math.Floor((resetsAt - now) / 60_000));
        if (minutes < 1) return "<1m";
        long days = minutes / 1440, hours = minutes % 1440 / 60, mins = minutes % 60;
        if (days > 0) return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0) return mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
        return $"{mins}m";
    }

    /// <summary>Math.round for a percentage (half up, as JavaScript rounds).</summary>
    public static int Percent(double usedPercent) => (int)Math.Floor(usedPercent + 0.5);
}

/// <summary>
/// The quota the session list shows (app.ts setQuota / watchCoordinatorQuota): per source, the latest; the Codex
/// windows from each Coordinator read, its Claude ones only when newer than what is shown. <see cref="Set"/> says
/// whether the shown windows changed.
/// </summary>
public sealed class QuotaBoard
{
    readonly Dictionary<string, ProviderQuota> quotas = [];

    public IReadOnlyList<ProviderQuota> Shown => [.. quotas.Values.OrderBy(q => q.Source, StringComparer.Ordinal).Where(q => q.Windows.Count > 0)];

    public bool Set(ProviderQuota next)
    {
        var before = quotas.GetValueOrDefault(next.Source);
        quotas[next.Source] = next;
        return before is null || !before.Windows.SequenceEqual(next.Windows);
    }

    /// <summary>A <c>quota.codex</c> answer's usage, read at <paramref name="now"/>: whether the shown windows changed.</summary>
    public bool ApplyCoordinator(JsonNode? usage, double now)
    {
        var changed = false;
        List<QuotaWindow> codex;
        try
        {
            codex = Quota.ParseCoordinatorUsage(usage);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            codex = [];
        }
        if (codex.Count > 0) changed |= Set(new ProviderQuota("codex", codex, now));
        if (Quota.ParseCoordinatorProviders(usage).TryGetValue("claude", out var claude)
            && Quota.CoordinatorQuotaIsNewer(quotas.GetValueOrDefault("claude"), claude))
            changed |= Set(new ProviderQuota("claude", claude.Windows, claude.At));
        return changed;
    }
}
