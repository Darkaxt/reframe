using System.Globalization;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

// The Overview pane's data (backlog #14): the host's `overview` service (docs/ptyhost-protocol.md) parsed into records,
// and the pure mapping the pane draws from (heatmap weeks and levels, totals, streaks, breakdown bars, timeline lines).
// Built from thread activity (Claude Code transcripts), never from repositories; no prompt text, only titles.

/// <summary>Where the host's <c>overview</c> answers come from: the host (<see cref="HostServices.OverviewAsync"/>) or,
/// for <c>--diag-capture</c>, a fixture holding answers of the same shape.</summary>
public interface IOverviewSource
{
    Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null);
}

public enum OverviewMetric { Prompts, Sessions, ActiveTime }

public sealed record OverviewCounts(long Prompts, long Turns, long Sessions, long ActiveMinutes)
{
    public static readonly OverviewCounts Zero = new(0, 0, 0, 0);

    public static OverviewCounts Parse(JsonNode? n) => n is null ? Zero
        : new((long?)n["prompts"] ?? 0, (long?)n["turns"] ?? 0, (long?)n["sessions"] ?? 0, (long?)n["activeMinutes"] ?? 0);

    public long Of(OverviewMetric metric) => metric switch
    {
        OverviewMetric.Prompts => Prompts,
        OverviewMetric.Sessions => Sessions,
        _ => ActiveMinutes,
    };
}

public sealed record OverviewStatus(string State, bool FirstPassComplete, long FilesDone, long FilesTotal, string? LastPassDay, string? LastError)
{
    public static readonly OverviewStatus Unconfigured = new("unconfigured", false, 0, 0, null, null);

    public static OverviewStatus Parse(JsonNode? n) => n is null ? Unconfigured : new((string?)n["state"] ?? "unconfigured",
        (bool?)n["firstPassComplete"] ?? false, (long?)n["filesDone"] ?? 0, (long?)n["filesTotal"] ?? 0,
        (string?)n["lastPassDay"], (string?)n["lastError"]);

    /// <summary>Still reading (the first pass, or a day's catch-up): the pane refreshes now and then while shown.</summary>
    public bool Working => State is "indexing" or "paused";
}

public sealed record OverviewSummary(DateOnly From, DateOnly To, IReadOnlyDictionary<DateOnly, OverviewCounts> Days,
    OverviewCounts Totals, int CurrentStreak, int LongestStreak, IReadOnlyList<int> Years)
{
    public static OverviewSummary Parse(JsonNode? n, DateOnly today)
    {
        var days = new Dictionary<DateOnly, OverviewCounts>();
        foreach (var d in n?["days"]?.AsArray() ?? [])
            if (d is not null && Overview.ParseDay((string?)d["date"]) is { } date) days[date] = OverviewCounts.Parse(d);
        var years = (n?["years"]?.AsArray() ?? []).Select(y => int.TryParse(y?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList();
        return new(Overview.ParseDay((string?)n?["from"]) ?? today.AddDays(-364), Overview.ParseDay((string?)n?["to"]) ?? today,
            days, OverviewCounts.Parse(n?["totals"]), (int?)n?["streaks"]?["current"] ?? 0, (int?)n?["streaks"]?["longest"] ?? 0, years);
    }
}

public sealed record OverviewThread(string SessionId, string Provider, string? Title, string? Folder, long Turns, long Prompts,
    long ActiveMinutes, double? LastActive, bool Gone)
{
    public static OverviewThread Parse(JsonNode n) => new((string?)n["sessionId"] ?? "", (string?)n["provider"] ?? "",
        (string?)n["title"], (string?)n["folder"], (long?)n["turns"] ?? 0, (long?)n["prompts"] ?? 0,
        (long?)n["activeMinutes"] ?? 0, (double?)n["lastActive"], (bool?)n["gone"] ?? false);
}

public sealed record OverviewFolder(string? Folder, long Sessions, long Turns);

public sealed record OverviewMonth(string Month, long Sessions, long Prompts, long Turns, long ActiveMinutes, int FolderCount,
    IReadOnlyList<OverviewFolder> Folders)
{
    public static OverviewMonth Parse(JsonNode n) => new((string?)n["month"] ?? "", (long?)n["sessions"] ?? 0, (long?)n["prompts"] ?? 0,
        (long?)n["turns"] ?? 0, (long?)n["activeMinutes"] ?? 0, (int?)n["folderCount"] ?? 0,
        (n["folders"]?.AsArray() ?? []).Where(f => f is not null)
            .Select(f => new OverviewFolder((string?)f!["folder"], (long?)f["sessions"] ?? 0, (long?)f["turns"] ?? 0)).ToList());
}

public sealed record OverviewBreakdown(IReadOnlyDictionary<string, long> Providers, IReadOnlyDictionary<string, long> Models,
    IReadOnlyDictionary<string, long> Tools)
{
    static Dictionary<string, long> Map(JsonNode? n) => (n as JsonObject)?.Where(p => p.Value is not null)
        .ToDictionary(p => p.Key, p => (long?)p.Value ?? 0) ?? new Dictionary<string, long>();

    public static OverviewBreakdown Parse(JsonNode? n) => new(Map(n?["providers"]), Map(n?["models"]), Map(n?["tools"]));
}

/// <summary>One square of the heatmap: its day, the metric's value and its level (0 none, 1..4 more).</summary>
public sealed record HeatCell(DateOnly Date, long Value, int Level, string Tooltip);

/// <summary>A column of the heatmap: Sunday to Saturday, null for a day outside the range.</summary>
public sealed record HeatWeek(IReadOnlyList<HeatCell?> Days);

/// <summary>A month's name above the column it starts in.</summary>
public sealed record HeatMonthLabel(int Week, string Text);

public sealed record Heatmap(IReadOnlyList<HeatWeek> Weeks, IReadOnlyList<HeatMonthLabel> Months, string Total);

public sealed record BreakdownBar(string Label, long Count, double Share);

public static class Overview
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static DateOnly? ParseDay(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", En, DateTimeStyles.None, out var d) ? d : null;

    static string N(long n) => n.ToString("N0", En);

    static string Plural(long n, string one, string many) => $"{N(n)} {(n == 1 ? one : many)}";

    /// <summary>Active minutes as people read them: "45 minutes", "1 hour", "12 hours" (rounded down to hours).</summary>
    public static string Duration(long minutes) => minutes < 60 ? Plural(minutes, "minute", "minutes") : Plural(minutes / 60, "hour", "hours");

    public static string MetricName(OverviewMetric metric) => metric switch
    {
        OverviewMetric.Prompts => "Prompts",
        OverviewMetric.Sessions => "Sessions",
        _ => "Active time",
    };

    /// <summary>A value of the metric with its unit: "12 prompts", "1 session", "3 hours".</summary>
    public static string Amount(OverviewMetric metric, long value) => metric switch
    {
        OverviewMetric.Prompts => Plural(value, "prompt", "prompts"),
        OverviewMetric.Sessions => Plural(value, "session", "sessions"),
        _ => value == 0 ? "No activity" : Duration(value),
    };

    /// <summary>The line above the heatmap: "1,234 prompts in the last year", "56 sessions in 2025", "12 hours of
    /// activity in the last year".</summary>
    public static string TotalLine(OverviewMetric metric, OverviewCounts totals, int? year)
    {
        var range = year is { } y ? $"in {y}" : "in the last year";
        var value = totals.Of(metric);
        return metric == OverviewMetric.ActiveTime ? $"{Duration(value)} of activity {range}" : $"{Amount(metric, value)} {range}";
    }

    /// <summary>GitHub's levels: none for 0, else quarters of the busiest day (1..4).</summary>
    public static int Level(long value, long max) =>
        value <= 0 || max <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(4.0 * value / max), 1, 4);

    /// <summary>The contribution calendar: one column per week (Sunday first) from the week holding <c>From</c> to the
    /// one holding <c>To</c>; days outside the range are null. A month's name is over the first column whose Sunday is
    /// in it, left out when the previous name is less than three columns before (GitHub's crowding rule).</summary>
    public static Heatmap BuildHeatmap(OverviewSummary summary, OverviewMetric metric, int? year)
    {
        var from = summary.From;
        var to = summary.To < from ? from : summary.To;
        var start = from.AddDays(-(int)from.DayOfWeek);
        long max = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (summary.Days.TryGetValue(d, out var c)) max = Math.Max(max, c.Of(metric));
        var weeks = new List<HeatWeek>();
        var months = new List<HeatMonthLabel>();
        var lastLabel = -3;
        for (var sunday = start; sunday <= to; sunday = sunday.AddDays(7))
        {
            var column = weeks.Count;
            var cells = new HeatCell?[7];
            for (var i = 0; i < 7; i++)
            {
                var day = sunday.AddDays(i);
                if (day < from || day > to) continue;
                var value = summary.Days.TryGetValue(day, out var c) ? c.Of(metric) : 0;
                var when = day.ToString("MMM d, yyyy", En);
                cells[i] = new HeatCell(day, value, Level(value, max),
                    value == 0 ? $"No {MetricName(metric).ToLowerInvariant()} on {when}" : $"{Amount(metric, value)} on {when}");
            }
            weeks.Add(new HeatWeek(cells));
            // The month this column starts (its first day in range is the 1st..7th of a month, or the first column).
            var first = cells.First(c => c is not null)!.Date;
            var startsMonth = column == 0 ? first.Day <= 21 : first.Day <= 7;
            if (startsMonth && column - lastLabel >= 3)
            {
                months.Add(new HeatMonthLabel(column, first.ToString("MMM", En)));
                lastLabel = column;
            }
        }
        return new Heatmap(weeks, months, TotalLine(metric, summary.Totals, year));
    }

    public static string Streak(int days) => Plural(days, "day", "days");

    /// <summary>Shares of a count map, largest first; beyond <paramref name="top"/> the rest is one "Other" bar.</summary>
    public static IReadOnlyList<BreakdownBar> Bars(IReadOnlyDictionary<string, long> counts, Func<string, string> label, int top = 6)
    {
        var total = counts.Values.Where(v => v > 0).Sum();
        var sorted = counts.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var bars = sorted.Take(top).Select(p => new BreakdownBar(label(p.Key), p.Value, total == 0 ? 0 : (double)p.Value / total)).ToList();
        var rest = sorted.Skip(top).Sum(p => p.Value);
        if (rest > 0) bars.Add(new BreakdownBar("Other", rest, (double)rest / total));
        return bars;
    }

    /// <summary>Every tool kind (zeros included) as "Edit", "Bash", "Read", "Web" largest share first, as the other bars
    /// are, and "Other" always last. The host sends a map (no order of its own); ties keep edit, bash, read, web.</summary>
    public static IReadOnlyList<BreakdownBar> ToolBars(IReadOnlyDictionary<string, long> tools)
    {
        var total = tools.Values.Where(v => v > 0).Sum();
        string[] kinds = ["edit", "bash", "read", "web"];
        return kinds.Select((k, i) => (k, i, n: tools.GetValueOrDefault(k))).OrderByDescending(p => p.n).ThenBy(p => p.i)
            .Select(p => p.k).Append("other")
            .Select(k => (k, n: tools.GetValueOrDefault(k)))
            .Select(p => new BreakdownBar(char.ToUpperInvariant(p.k[0]) + p.k[1..], p.n, total == 0 ? 0 : (double)p.n / total)).ToList();
    }

    public static string ProviderLabel(string provider) => provider switch
    {
        "claude" => "Claude Code",
        "anthropic" => "Anthropic",
        "chatgpt" => "ChatGPT",
        _ => provider,
    };

    /// <summary>"November 2026" for "2026-11".</summary>
    public static string MonthTitle(string month) =>
        DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", En, DateTimeStyles.None, out var d) ? d.ToString("MMMM yyyy", En) : month;

    /// <summary>"Started 14 threads in 5 folders", with "; 2 still open" when the shell has open sessions from then.</summary>
    public static string MonthLine(OverviewMonth month, int stillOpen)
    {
        var line = $"Started {Plural(month.Sessions, "thread", "threads")} in {Plural(month.FolderCount, "folder", "folders")}";
        return stillOpen > 0 ? $"{line}; {N(stillOpen)} still open" : line;
    }

    /// <summary>How many of the shell's open sessions were created in <paramref name="month"/> ("yyyy-MM", local time).</summary>
    public static int StillOpen(string month, IEnumerable<double> openCreatedAt) =>
        openCreatedAt.Count(t => t > 0 && RelativeTime.Local(t).ToString("yyyy-MM", En) == month);

    public static string ThreadTitle(OverviewThread thread) => string.IsNullOrWhiteSpace(thread.Title) ? "Untitled thread" : thread.Title!;

    /// <summary>The folder's last part ("reframe" for C:\src\reframe), or "No folder".</summary>
    public static string FolderName(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return "No folder";
        var trimmed = folder.TrimEnd('\\', '/');
        var at = trimmed.LastIndexOfAny(['\\', '/']);
        return at >= 0 && at < trimmed.Length - 1 ? trimmed[(at + 1)..] : trimmed;
    }

    /// <summary>"312 turns · 2d" (last active, as the session list writes it), "transcript removed" for a gone one.</summary>
    public static string ThreadMeta(OverviewThread thread, double now)
    {
        var meta = Plural(thread.Turns, "turn", "turns");
        if (thread.LastActive is { } at) meta += $" · {RelativeTime.Short(at, now)}";
        return thread.Gone ? meta + " · transcript removed" : meta;
    }

    /// <summary>The indexing line: "Analysing… 120 of 1,460 files", or null once the index is complete and idle.</summary>
    public static string? StatusLine(OverviewStatus status)
    {
        var files = $"{N(status.FilesDone)} of {N(status.FilesTotal)} files";
        var line = status.State switch
        {
            "unconfigured" => "Activity is not indexed yet.",
            "indexing" => $"Analysing… {files}",
            "paused" => $"Analysing paused while a session works… {files}",
            _ => null,
        };
        return status.LastError is { Length: > 0 } error ? (line is null ? "" : line + " ") + $"(last error: {error})" : line;
    }

    /// <summary><c>overview.configure</c>'s parameters: the cache in the profile and Claude Code's folder. Reframe's
    /// providers share one Claude Code home, so it is one entry ("claude"); a provider with its own home would add one.</summary>
    public static JsonObject ConfigureParameters(string profileDir, string claudeHome) => new()
    {
        ["cache"] = Path.Combine(profileDir, "overview.json"),
        ["homes"] = new JsonArray(new JsonObject { ["provider"] = "claude", ["home"] = claudeHome }),
    };
}

/// <summary>For <c>--diag-capture</c>: answers of the host's shape, built from a fixture (tools/visual/fixtures/overview.json)
/// with dates relative to today, so no transcript (and never the user's) is read.</summary>
public sealed class FixtureOverviewSource(JsonObject fixture, DateOnly today) : IOverviewSource
{
    /// <summary>Which status the source answers: "indexing" (fixture.statusIndexing) or the complete one.</summary>
    public bool Indexing { get; set; }

    /// <summary>The answer as the connection would give it: parsed from its JSON text (numbers read as on the wire).</summary>
    public Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null) =>
        Task.FromResult(Answer(method) is { } answer ? JsonNode.Parse(answer.ToJsonString()) : null);

    JsonNode? Answer(string method) => method switch
    {
        "status" => Indexing ? fixture["statusIndexing"] : fixture["status"],
        "summary" => Summary(),
        "threads" => Threads(),
        "breakdown" => fixture["breakdown"],
        "timeline" => Timeline(),
        _ => null,
    };

    static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A year of days from a fixed seed (activity on weekdays mostly, quieter weekends, a recent streak), so the
    /// capture is the same every day but for its dates.</summary>
    JsonObject Summary()
    {
        var seed = (int?)fixture["seed"] ?? 7;
        var random = new Random(seed);
        var from = today.AddDays(-364);
        var days = new JsonArray();
        long prompts = 0, turns = 0, sessions = 0, minutes = 0;
        var streak = (int?)fixture["currentStreak"] ?? 0;
        for (var d = from; d <= today; d = d.AddDays(1))
        {
            var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var age = today.DayNumber - d.DayNumber;
            var busy = age < streak || random.NextDouble() < (weekend ? 0.2 : 0.7) * (age > 300 ? 0.4 : 1);
            if (!busy) continue;
            var p = 1 + random.Next(weekend ? 6 : 30);
            var day = new JsonObject
            {
                ["date"] = Day(d), ["prompts"] = p, ["turns"] = p * 3, ["sessions"] = 1 + p / 8, ["activeMinutes"] = p * 4,
            };
            prompts += p; turns += p * 3; sessions += 1 + p / 8; minutes += p * 4;
            days.Add(day);
        }
        return new JsonObject
        {
            ["from"] = Day(from), ["to"] = Day(today), ["days"] = days,
            ["totals"] = new JsonObject { ["prompts"] = prompts, ["turns"] = turns, ["sessions"] = sessions, ["activeMinutes"] = minutes },
            ["streaks"] = new JsonObject { ["current"] = streak, ["longest"] = (int?)fixture["longestStreak"] ?? streak },
            ["years"] = new JsonArray(today.Year.ToString(CultureInfo.InvariantCulture), (today.Year - 1).ToString(CultureInfo.InvariantCulture)),
        };
    }

    JsonObject Threads()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var list = new JsonArray();
        foreach (var t in fixture["threads"]?.AsArray() ?? [])
        {
            var item = t!.DeepClone().AsObject();
            item["lastActive"] = now - (long)(((double?)t["hoursAgo"] ?? 0) * 3_600_000);
            item.Remove("hoursAgo");
            list.Add(item);
        }
        return new JsonObject { ["threads"] = list };
    }

    JsonObject Timeline()
    {
        var months = new JsonArray();
        foreach (var m in fixture["months"]?.AsArray() ?? [])
        {
            var item = m!.DeepClone().AsObject();
            var month = today.AddMonths(-((int?)m["monthsAgo"] ?? 0));
            item["month"] = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            item.Remove("monthsAgo");
            months.Add(item);
        }
        return new JsonObject { ["months"] = months };
    }
}
