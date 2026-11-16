using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ScheduledTasksTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    static DateTimeOffset At(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    static DateTimeOffset? Next(string rule, DateTimeOffset made, DateTimeOffset after) => Recurrence.Parse(rule, Utc)!.Next(made, after);

    [Fact]
    public void ReadsAndWritesCodexFiles()
    {
        const string text = "version = 1\nid = \"watch-longshot\"\nkind = \"heartbeat\"\nname = \"Watch \\\"it\\\"\"\nprompt = \"a\\nb \\u00e9\"\nstatus = \"ACTIVE\"\nrrule = \"FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=15;BYSECOND=0\"\ntarget_thread_id = \"01a\"\ncreated_at = 1794783674280\nupdated_at = 1794783674280\nextra = true\n";
        var fields = ScheduledTasks.ParseToml(text)!;
        var task = ScheduledTasks.FromFields(fields)!;
        Assert.Equal(("watch-longshot", "Watch \"it\"", "a\nb é", "01a", 1794783674280L), (task.Id, task.Name, task.Prompt, task.TargetThreadId, task.CreatedAt));
        Assert.True(task.Active);
        Assert.Equal("important", task.NotificationPolicy);
        Assert.Equal(fields, ScheduledTasks.ParseToml(ScheduledTasks.WriteToml(fields)));
        Assert.StartsWith("version = 1\nid = \"watch-longshot\"\n", ScheduledTasks.WriteToml(fields));
        Assert.Null(ScheduledTasks.ParseToml("[table]"));
    }

    [Fact]
    public void SavesLoadsAndKeepsRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reframe-tasks-{Guid.NewGuid():N}");
        try
        {
            ScheduledTasks.Save(dir, new Dictionary<string, object>
            {
                ["version"] = 1L, ["id"] = "daily", ["name"] = "Daily", ["prompt"] = "p", ["rrule"] = "FREQ=DAILY", ["target_thread_id"] = "s", ["created_at"] = 5L,
            });
            var task = Assert.Single(ScheduledTasks.Load(dir));
            ScheduledTasks.Update(dir, task, new Dictionary<string, object> { ["status"] = "PAUSED" }, 9);
            Assert.False(Assert.Single(ScheduledTasks.Load(dir)).Active);
            ScheduledTasks.WriteRuns(dir, "daily", new TaskRuns(7, 2, "delivered", 8));
            Assert.Equal(new TaskRuns(7, 2, "delivered", 8), ScheduledTasks.ReadRuns(dir, "daily"));
            ScheduledTasks.Delete(dir, "daily");
            Assert.Empty(ScheduledTasks.Load(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FollowsTheRules()
    {
        var made = At(2026, 11, 16, 10, 27, 7); // a Monday
        Assert.Equal(At(2026, 11, 23, 9, 15), Next("FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=15;BYSECOND=0", made, made));
        Assert.Equal(At(2026, 11, 16, 17, 30), Next("FREQ=WEEKLY;BYDAY=MO,TH;BYHOUR=17;BYMINUTE=30;BYSECOND=0", made, made));
        Assert.Equal(At(2026, 11, 17, 9, 0), Next("FREQ=DAILY;BYHOUR=9;BYMINUTE=0;BYSECOND=0", made, made));
        Assert.Equal(At(2026, 11, 16, 16, 30, 7), Next("FREQ=DAILY;BYHOUR=16;BYMINUTE=30", made, made));
        Assert.Equal(At(2026, 11, 16, 16, 27, 7), Next("FREQ=HOURLY;INTERVAL=6", made, made));
        Assert.Equal(At(2026, 11, 16, 10, 32, 7), Next("FREQ=MINUTELY;INTERVAL=5", made, made));
        Assert.Equal(At(2026, 11, 19, 9, 0), Next("FREQ=DAILY;INTERVAL=3;BYHOUR=9;BYMINUTE=0;BYSECOND=0", made, made));
        Assert.Equal(At(2026, 12, 1, 8, 0), Next("FREQ=MONTHLY;BYMONTHDAY=1;BYHOUR=8;BYMINUTE=0;BYSECOND=0", made, made));
        // COUNT: the eighth run is the last.
        const string eight = "FREQ=DAILY;BYHOUR=10;BYMINUTE=0;BYSECOND=0;COUNT=8";
        Assert.Equal(At(2026, 11, 24, 10, 0), Next(eight, made, At(2026, 11, 23, 12)));
        Assert.Null(Next(eight, made, At(2026, 11, 24, 12)));
        Assert.Null(Next("FREQ=DAILY;BYHOUR=9;UNTIL=20261118", made, At(2026, 11, 18, 10)));
        // DTSTART with a zone: 17:00 in Bucharest is 15:00 UTC in March.
        var bucharest = Recurrence.Parse("DTSTART;TZID=Europe/Bucharest:20270301T170000\nRRULE:FREQ=WEEKLY;BYDAY=MO;BYHOUR=17;BYMINUTE=0;BYSECOND=0")!;
        Assert.Equal(At(2027, 3, 1, 15, 0), bucharest.Next(made, made)!.Value.ToUniversalTime());
        foreach (var bad in new[] { "BYHOUR=9", "FREQ=YEARLY", "FREQ=MINUTELY", "FREQ=DAILY;BYSETPOS=1", "FREQ=WEEKLY;BYDAY=1MO" })
            Assert.Null(Recurrence.Parse(bad, Utc));
    }

    [Fact]
    public void AMissedRunIsDueOnceAndTheScheduleGoesOn()
    {
        var created = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 1, 8, 0, 0)));
        var task = new ScheduledTask("t", "heartbeat", "T", "p", "ACTIVE", "FREQ=DAILY;BYHOUR=9;BYMINUTE=0;BYSECOND=0", "s", "important",
            created.ToUnixTimeMilliseconds(), 0, new Dictionary<string, object>());
        var jitter = ScheduledTasks.JitterSeconds("t");
        var first = ScheduledTasks.NextDue(task, new TaskRuns(null, 0, null, null))!.Value;
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, jitter), first.LocalDateTime);
        // Last run days ago: due once (the first missed time), then from the run on.
        var ranAt = new DateTimeOffset(new DateTime(2026, 11, 10, 12, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 10, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 11, 9, 0, jitter), ScheduledTasks.NextDue(task, new TaskRuns(ranAt.ToUnixTimeMilliseconds(), 3, null, null))!.Value.LocalDateTime);
        Assert.Equal("[Reframe scheduled task: T (t)]\np", ScheduledTasks.RunText(task));
    }

    [Fact]
    public void DescribesCommonRules()
    {
        Assert.Equal("Mon at 09:15", ScheduledTasks.Describe("FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=15;BYSECOND=0"));
        Assert.Equal("Daily at 10:00 (8 times)", ScheduledTasks.Describe("FREQ=DAILY;BYHOUR=10;BYMINUTE=0;BYSECOND=0;COUNT=8"));
        Assert.Equal("Every 6 hours", ScheduledTasks.Describe("FREQ=HOURLY;INTERVAL=6"));
        Assert.Equal("nonsense", ScheduledTasks.Describe("nonsense"));
    }

    [Fact]
    public void FindsCodexTasksForThreadsOfTheSameName()
    {
        var names = ScheduledTasks.CodexThreadNames([
            """{"id":"c1","thread_name":"Old name","updated_at":"x"}""",
            """{"id":"c1","thread_name":"Longshot mirror","updated_at":"y"}""",
            """{"id":"c2","thread_name":"Elsewhere"}""",
            "not json",
        ]);
        ScheduledTask Codex(string id, string thread) => new(id, "heartbeat", id, "p", "ACTIVE", "FREQ=DAILY", thread, "important", 1, 1,
            new Dictionary<string, object> { ["id"] = id, ["target_thread_id"] = thread, ["prompt"] = "p", ["rrule"] = "FREQ=DAILY" });
        var codex = new[] { Codex("watch", "c1"), Codex("other", "c2"), Codex("done", "c1") };
        var own = new[] { Codex("done", "r1") with { Fields = new Dictionary<string, object> { ["imported_from"] = "codex:done" } } };
        var found = ScheduledTasks.CodexCandidates(codex, names, [("r1", "longshot mirror "), ("r2", "Other")], own);
        var only = Assert.Single(found);
        Assert.Equal(("watch", "Longshot mirror", "r1"), (only.Task.Id, only.ThreadName, only.SessionId));
        var fields = ScheduledTasks.Imported(only, new HashSet<string> { "watch" }, 42);
        Assert.Equal(("watch-2", "r1", "codex:watch", 42L), ((string)fields["id"], (string)fields["target_thread_id"], (string)fields["imported_from"], (long)fields["updated_at"]));
    }

    [Fact]
    public void TheWakeUpTaskStartsReframeInTheTrayOnce()
    {
        var xml = ScheduledTasks.WakeTaskXml(@"D:\Tools\Reframe\releases\x\reframe.exe", new DateTime(2026, 11, 17, 9, 0, 30), @"PC\me");
        Assert.Contains("<StartBoundary>2026-11-17T09:00:30</StartBoundary>", xml);
        Assert.Contains("<Arguments>--autostart</Arguments>", xml);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);
        Assert.Contains("<UserId>PC\\me</UserId>", xml);
        System.Xml.Linq.XDocument.Parse(xml[xml.IndexOf("<Task", StringComparison.Ordinal)..]);
        Assert.StartsWith("Reframe wake-up ", ScheduledTasks.WakeTaskName(@"D:\Tools\Reframe\profiles\ClaudeGPT"));
    }
}
