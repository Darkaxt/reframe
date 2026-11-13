using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class OverviewTests
{
    static OverviewSummary Summary(string json) => OverviewSummary.Parse(JsonNode.Parse(json), new DateOnly(2026, 11, 13));

    // 2025-11-14 (a Friday) to 2026-11-13 (a Friday): the host's "last year" range.
    const string LastYear = """
        { "from": "2025-11-14", "to": "2026-11-13",
          "days": [ { "date": "2025-11-14", "prompts": 2, "turns": 5, "sessions": 1, "activeMinutes": 10 },
                    { "date": "2026-11-12", "prompts": 8, "turns": 20, "sessions": 2, "activeMinutes": 61 },
                    { "date": "2026-11-13", "prompts": 1234, "turns": 3000, "sessions": 1, "activeMinutes": 125 } ],
          "totals": { "prompts": 1244, "turns": 3025, "sessions": 4, "activeMinutes": 196 },
          "streaks": { "current": 2, "longest": 17 }, "years": ["2026", "2025"] }
        """;

    [Fact]
    public void Heatmap_has_a_column_per_week_from_the_sunday_before_the_range()
    {
        var map = Overview.BuildHeatmap(Summary(LastYear), OverviewMetric.Prompts, null);
        // Sunday 2025-11-09 .. Saturday 2026-11-14: 53 weeks.
        Assert.Equal(53, map.Weeks.Count);
        var first = map.Weeks[0].Days;
        Assert.All(first.Take(5), d => Assert.Null(d)); // Sun..Thu before the range
        Assert.Equal(new DateOnly(2025, 11, 14), first[5]!.Date);
        var last = map.Weeks[^1].Days;
        Assert.Equal(new DateOnly(2026, 11, 13), last[5]!.Date);
        Assert.Null(last[6]); // Saturday after today
        Assert.Equal(365, map.Weeks.Sum(w => w.Days.Count(d => d is not null)));
    }

    [Fact]
    public void Levels_are_quarters_of_the_busiest_day()
    {
        Assert.Equal(0, Overview.Level(0, 100));
        Assert.Equal(1, Overview.Level(1, 100));
        Assert.Equal(1, Overview.Level(25, 100));
        Assert.Equal(2, Overview.Level(26, 100));
        Assert.Equal(3, Overview.Level(75, 100));
        Assert.Equal(4, Overview.Level(100, 100));
        Assert.Equal(0, Overview.Level(5, 0));

        var map = Overview.BuildHeatmap(Summary(LastYear), OverviewMetric.Prompts, null);
        var cells = map.Weeks.SelectMany(w => w.Days).OfType<HeatCell>().ToDictionary(c => c.Date);
        Assert.Equal(4, cells[new DateOnly(2026, 11, 13)].Level);
        Assert.Equal(1, cells[new DateOnly(2026, 11, 12)].Level);
        Assert.Equal(0, cells[new DateOnly(2026, 6, 1)].Level);
        Assert.Equal("1,234 prompts on Nov 13, 2026", cells[new DateOnly(2026, 11, 13)].Tooltip);
        Assert.Equal("No prompts on Jun 1, 2026", cells[new DateOnly(2026, 6, 1)].Tooltip);

        // Another metric, other levels: active minutes 61 of 125.
        var minutes = Overview.BuildHeatmap(Summary(LastYear), OverviewMetric.ActiveTime, null)
            .Weeks.SelectMany(w => w.Days).OfType<HeatCell>().ToDictionary(c => c.Date);
        Assert.Equal(2, minutes[new DateOnly(2026, 11, 12)].Level);
        Assert.Equal("1 hour on Nov 12, 2026", minutes[new DateOnly(2026, 11, 12)].Tooltip);
    }

    [Fact]
    public void Month_labels_start_their_column_and_are_not_crowded()
    {
        var map = Overview.BuildHeatmap(Summary(LastYear), OverviewMetric.Prompts, null);
        // The first column starts on Nov 14: "Nov" there; "Dec" over the week of Sunday Dec 7 (column 4).
        Assert.Equal(new HeatMonthLabel(0, "Nov"), map.Months[0]);
        Assert.Equal(new HeatMonthLabel(4, "Dec"), map.Months[1]);
        Assert.Equal(13, map.Months.Count); // Nov .. Nov
        Assert.All(map.Months.Zip(map.Months.Skip(1)), p => Assert.True(p.Second.Week - p.First.Week >= 3));

        // A range starting late in a month leaves that month unnamed rather than crowd the next.
        var late = Overview.BuildHeatmap(Summary("""{ "from": "2025-11-28", "to": "2026-11-27", "days": [] }"""), OverviewMetric.Prompts, null);
        Assert.Equal("Dec", late.Months[0].Text);
    }

    [Fact]
    public void A_year_covers_its_calendar_year()
    {
        var map = Overview.BuildHeatmap(Summary("""{ "from": "2025-01-01", "to": "2025-12-31", "days": [], "totals": {} }"""), OverviewMetric.Sessions, 2025);
        Assert.Equal(53, map.Weeks.Count); // Sunday 2024-12-29 .. Saturday 2026-01-03
        Assert.Equal("Jan", map.Months[0].Text);
        Assert.Equal("0 sessions in 2025", map.Total);
    }

    [Fact]
    public void Totals_and_streaks_read_as_people_say_them()
    {
        var summary = Summary(LastYear);
        Assert.Equal("1,244 prompts in the last year", Overview.TotalLine(OverviewMetric.Prompts, summary.Totals, null));
        Assert.Equal("4 sessions in 2026", Overview.TotalLine(OverviewMetric.Sessions, summary.Totals, 2026));
        Assert.Equal("3 hours of activity in the last year", Overview.TotalLine(OverviewMetric.ActiveTime, summary.Totals, null));
        Assert.Equal("1 session in the last year", Overview.TotalLine(OverviewMetric.Sessions, OverviewCounts.Zero with { Sessions = 1 }, null));
        Assert.Equal("45 minutes", Overview.Duration(45));
        Assert.Equal((2, 17), (summary.CurrentStreak, summary.LongestStreak));
        Assert.Equal(new[] { 2026, 2025 }, summary.Years);
        Assert.Equal("1 day", Overview.Streak(1));
        Assert.Equal("0 days", Overview.Streak(0));
        Assert.Equal("17 days", Overview.Streak(17));
    }

    [Fact]
    public void Status_line_shows_indexing_progress()
    {
        var indexing = OverviewStatus.Parse(JsonNode.Parse("""{ "state": "indexing", "firstPassComplete": false, "filesDone": 120, "filesTotal": 1460 }"""));
        Assert.True(indexing.Working);
        Assert.Equal("Analysing… 120 of 1,460 files", Overview.StatusLine(indexing));
        Assert.StartsWith("Analysing paused", Overview.StatusLine(indexing with { State = "paused" }));
        var idle = indexing with { State = "idle", FirstPassComplete = true };
        Assert.False(idle.Working);
        Assert.Null(Overview.StatusLine(idle));
        Assert.Equal("(last error: disk)", Overview.StatusLine(idle with { LastError = "disk" }));
    }

    [Fact]
    public void Breakdown_bars_are_shares_largest_first()
    {
        var bars = Overview.Bars(new Dictionary<string, long> { ["a"] = 1, ["b"] = 3, ["c"] = 0, ["d"] = 4, ["e"] = 2 }, k => k.ToUpperInvariant(), top: 2);
        Assert.Equal(["D", "B", "Other"], bars.Select(b => b.Label));
        Assert.Equal(0.4, bars[0].Share, 3);
        Assert.Equal(3, bars[2].Count);
        var tools = Overview.ToolBars(new Dictionary<string, long> { ["bash"] = 3, ["read"] = 1 });
        Assert.Equal(["Edit", "Bash", "Read", "Web", "Other"], tools.Select(b => b.Label));
        Assert.Equal(0.75, tools[1].Share, 3);
        Assert.Equal("Claude Code", Overview.ProviderLabel("claude"));
    }

    [Fact]
    public void Threads_and_months_show_titles_folders_and_counts_only()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var thread = OverviewThread.Parse(JsonNode.Parse("""
            { "sessionId": "s", "provider": "claude", "title": null, "folder": "C:\\src\\reframe\\", "turns": 1,
              "prompts": 1, "activeMinutes": 1, "lastActive": 1763035200000, "gone": true }
            """)!);
        Assert.Equal("Untitled thread", Overview.ThreadTitle(thread));
        Assert.Equal("reframe", Overview.FolderName(thread.Folder));
        Assert.Equal("No folder", Overview.FolderName(null));
        Assert.StartsWith("1 turn · ", Overview.ThreadMeta(thread, now));
        Assert.EndsWith("transcript removed", Overview.ThreadMeta(thread, now));

        var month = OverviewMonth.Parse(JsonNode.Parse("""{ "month": "2026-11", "sessions": 14, "folderCount": 5, "folders": [] }""")!);
        Assert.Equal("November 2026", Overview.MonthTitle(month.Month));
        Assert.Equal("Started 14 threads in 5 folders", Overview.MonthLine(month, 0));
        Assert.Equal("Started 14 threads in 5 folders; 2 still open", Overview.MonthLine(month, 2));
        Assert.Equal("Started 1 thread in 1 folder", Overview.MonthLine(month with { Sessions = 1, FolderCount = 1 }, 0));
        var inNovember = new DateTimeOffset(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        var inOctober = new DateTimeOffset(new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        Assert.Equal(1, Overview.StillOpen("2026-11", [inNovember, inOctober, 0]));
    }

    [Fact]
    public void Configure_names_the_profile_cache_and_the_claude_home()
    {
        var parameters = Overview.ConfigureParameters(@"D:\profile", @"C:\Users\x\.claude");
        Assert.Equal(@"D:\profile\overview.json", (string?)parameters["cache"]);
        var home = Assert.Single(parameters["homes"]!.AsArray())!;
        Assert.Equal("claude", (string?)home["provider"]);
        Assert.Equal(@"C:\Users\x\.claude", (string?)home["home"]);
    }

    [Fact]
    public async Task The_capture_fixture_gives_answers_of_the_host_shape()
    {
        var file = Path.Combine(TokenTestsRoot.Root, "tools", "visual", "fixtures", "overview.json");
        var today = new DateOnly(2026, 11, 13);
        var source = new FixtureOverviewSource(JsonNode.Parse(File.ReadAllText(file))!["overview"]!.AsObject(), today);
        var summary = OverviewSummary.Parse(await source.CallAsync("summary"), today);
        Assert.Equal(today.AddDays(-364), summary.From);
        Assert.Equal(summary.Days.Values.Sum(d => d.Prompts), summary.Totals.Prompts);
        Assert.Equal(9, summary.CurrentStreak);
        Assert.False(OverviewStatus.Parse(await source.CallAsync("status")).Working);
        source.Indexing = true;
        Assert.Equal("Analysing… 412 of 1,460 files", Overview.StatusLine(OverviewStatus.Parse(await source.CallAsync("status"))));
        var threads = (await source.CallAsync("threads"))!["threads"]!.AsArray();
        Assert.Equal(6, threads.Count);
        Assert.All(threads, t => Assert.NotNull((double?)t!["lastActive"]));
        Assert.Equal("2026-11", (string?)(await source.CallAsync("timeline"))!["months"]![0]!["month"]);
    }
}
