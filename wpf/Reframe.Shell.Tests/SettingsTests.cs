using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>src/shared/quota.test.ts, ported.</summary>
public class QuotaTests
{
    static JsonNode J(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void ClaudeRateLimitsReadEveryWindowShortestFirst()
    {
        Assert.Equal(
            [new QuotaWindow("five_hour", 300, 100, 1_799_990_000_000), new QuotaWindow("seven_day", 10080, 41.2, 1_800_000_000_000)],
            Quota.ParseClaudeRateLimits(J("""{"seven_day":{"used_percentage":41.2,"resets_at":1800000000},"five_hour":{"used_percentage":120,"resets_at":1799990000}}""")));
        Assert.Equal([new QuotaWindow("seven_day", 10080, 3, null)],
            Quota.ParseClaudeRateLimits(J("""{"seven_day":{"used_percentage":3},"five_hour":null,"x":"y"}""")));
        Assert.Empty(Quota.ParseClaudeRateLimits(null));
    }

    [Fact]
    public void CoordinatorUsageTakesTheLatestBucketPerWindow()
    {
        var response = J("""
            {"ok":true,"usage":{"buckets":[
              {"id":"codex","windowMinutes":10080,"resetAt":2e12,"points":[{"t":10,"remaining":32},{"t":30,"remaining":4}]},
              {"id":"codex","windowMinutes":10080,"resetAt":1.9e12,"points":[{"t":20,"remaining":50}]},
              {"id":"codex","windowMinutes":300,"resetAt":2e12,"points":[]}]}}
            """);
        Assert.Equal([new QuotaWindow("codex:10080", 10080, 96, 2e12)], Quota.ParseCoordinatorUsage(response));
        Assert.Empty(Quota.ParseCoordinatorUsage(J("""{"ok":false,"error":"x"}""")));
    }

    const string Snapshot = """
        {"ok":true,"usage":{
          "buckets":[{"id":"codex","windowMinutes":10080,"resetAt":1794758099000,"points":[{"t":1,"remaining":50}]}],
          "providers":[
            {"id":"codex","label":"Codex","state":"available","observedAt":1794479110531.8745,"windows":[
              {"id":"weekly","windowMinutes":10080,"resetAt":1794758099000,"remaining":1,
               "points":[{"remaining":3,"t":1794470000000},{"remaining":1,"t":1794479110531.8745}]}]},
            {"id":"claude","label":"Claude","state":"available","observedAt":1794479106306.2878,"windows":[
              {"id":"weekly","windowMinutes":10080,"resetAt":1794729600000,"remaining":83,"points":[{"remaining":83,"t":1794479106306.2878}]},
              {"id":"five-hour","windowMinutes":300,"resetAt":1794484800000,"remaining":98,"points":[{"remaining":98,"t":1794479095544.2231}]}]},
            {"id":"other","windows":[{"windowMinutes":60,"remaining":10}]}]}}
        """;

    [Fact]
    public void ProvidersAreReadFromTheirLastPointShortestFirst()
    {
        var quotas = Quota.ParseCoordinatorProviders(J(Snapshot));
        Assert.Equal(["claude", "codex"], quotas.Keys.Order());
        Assert.Equal(1794479106306.2878, quotas["claude"].At);
        Assert.Equal([new QuotaWindow("claude:300", 300, 2, 1794484800000), new QuotaWindow("claude:10080", 10080, 17, 1794729600000)], quotas["claude"].Windows);
        Assert.Equal([new QuotaWindow("codex:10080", 10080, 99, 1794758099000)], quotas["codex"].Windows);
    }

    [Fact]
    public void CodexComesFromTheProvidersElseAnOlderCoordinatorsBuckets()
    {
        Assert.Equal(Quota.ParseCoordinatorProviders(J(Snapshot))["codex"].Windows, Quota.ParseCoordinatorUsage(J(Snapshot)));
        var older = new JsonObject { ["ok"] = true, ["usage"] = new JsonObject { ["buckets"] = J(Snapshot)["usage"]!["buckets"]!.DeepClone() } };
        Assert.Empty(Quota.ParseCoordinatorProviders(older));
        Assert.Equal([new QuotaWindow("codex:10080", 10080, 50, 1794758099000)], Quota.ParseCoordinatorUsage(older));
    }

    [Fact]
    public void CoordinatorClaudeQuotaReplacesOnlyWhenNewer()
    {
        var observed = Quota.ParseCoordinatorProviders(J(Snapshot))["claude"];
        Assert.True(Quota.CoordinatorQuotaIsNewer(null, observed));
        var captured = new ProviderQuota("claude", [], observed.At + 1);
        Assert.False(Quota.CoordinatorQuotaIsNewer(captured, observed));
        Assert.True(Quota.CoordinatorQuotaIsNewer(captured with { At = observed.At - 60_000 }, observed));
        Assert.False(Quota.CoordinatorQuotaIsNewer(captured with { At = observed.At }, observed));
    }

    [Fact]
    public void Labels()
    {
        Assert.Equal(["5h", "7d", "90m", ""], new[] { Quota.WindowLabel(300), Quota.WindowLabel(10080), Quota.WindowLabel(90), Quota.WindowLabel(null) });
        Assert.Equal(["ok", "warn", "danger"], new[] { Quota.Level(10), Quota.Level(75), Quota.Level(95) });
        Assert.Equal("<1m", Quota.UntilLabel(30_000, 0));
        Assert.Equal("12m", Quota.UntilLabel(12 * 60_000, 0));
        Assert.Equal("2h 10m", Quota.UntilLabel(130 * 60_000, 0));
        Assert.Equal("3d 4h", Quota.UntilLabel((3 * 1440 + 240) * 60_000, 0));
        Assert.Equal(43, Quota.Percent(42.5));
    }

    [Fact]
    public void TheBoardShowsCodexAndANewerClaudeSortedBySource()
    {
        var board = new QuotaBoard();
        Assert.True(board.ApplyCoordinator(J(Snapshot), 5));
        Assert.Equal(["claude", "codex"], board.Shown.Select(q => q.Source));
        Assert.False(board.ApplyCoordinator(J(Snapshot), 6));
        // A newer status line quota is kept over the Coordinator's older one.
        board.Set(new ProviderQuota("claude", [new QuotaWindow("five_hour", 300, 7, null)], 1794479106306.2878 + 1000));
        board.ApplyCoordinator(J(Snapshot), 7);
        Assert.Equal(7, board.Shown[0].Windows[0].UsedPercent);
        Assert.Empty(new QuotaBoard().Shown);
    }
}

/// <summary>Settings as app.ts's set-preferences takes them.</summary>
public class SettingsTests
{
    [Fact]
    public void ValidValuesAreTakenAndInvalidOnesIgnored()
    {
        var p = new Preferences();
        Assert.True(Settings.Apply(p, new PreferencesPatch { Language = "de", FontSize = 15, CloseWindow = "keep", PtyHost = "rust", SnapToGrid = false }));
        Assert.Equal(("de", 15, "keep", "rust", false), (p.Language, p.FontSize, p.CloseWindow, p.PtyHost, p.SnapToGrid));
        Assert.False(Settings.Apply(p, new PreferencesPatch { Language = "fr", FontSize = 15.5, CloseWindow = "never", PtyHost = "go" }));
        Assert.False(Settings.Apply(p, new PreferencesPatch { FontSize = StateValues.FontSizeMax + 1 }));
        Assert.False(Settings.Apply(p, new PreferencesPatch { FontSize = StateValues.FontSizeMin - 1 }));
        Assert.Equal(("de", 15, "keep", "rust"), (p.Language, p.FontSize, p.CloseWindow, p.PtyHost));
        Assert.False(Settings.Apply(p, new PreferencesPatch()));
    }

    [Fact]
    public void BooleansAndTheEffortAndPermissionModeLists()
    {
        var p = new Preferences();
        Settings.Apply(p, new PreferencesPatch { GpuAcceleration = true, LookInWsl = true, CheckForUpdates = false, NameFromFolder = true });
        Assert.True(p.GpuAcceleration && p.LookInWsl && !p.CheckForUpdates && p.NameFromFolder);
        Settings.Apply(p, new PreferencesPatch { DefaultEffort = "xhigh", DefaultPermissionMode = "plan" });
        Assert.Equal(("xhigh", "plan"), (p.DefaultEffort, p.DefaultPermissionMode));
        Assert.False(Settings.Apply(p, new PreferencesPatch { DefaultEffort = "extreme", DefaultPermissionMode = "yolo" }));
        Settings.Apply(p, new PreferencesPatch { DefaultEffort = "", DefaultPermissionMode = "" });
        Assert.Null(p.DefaultEffort);
        Assert.Null(p.DefaultPermissionMode);
    }

    [Fact]
    public void TextIsTrimmedClippedAndEmptyTakesItBack()
    {
        var p = new Preferences();
        Settings.Apply(p, new PreferencesPatch { DefaultModel = "  opus  ", ClaudeBin = @" D:\bin\claude.exe " });
        Assert.Equal(("opus", @"D:\bin\claude.exe"), (p.DefaultModel, p.ClaudeBin));
        Settings.Apply(p, new PreferencesPatch { DefaultModel = new string('m', 150) });
        Assert.Equal(100, p.DefaultModel!.Length);
        Settings.Apply(p, new PreferencesPatch { DefaultModel = "   ", ClaudeBin = "" });
        Assert.Null(p.DefaultModel);
        Assert.Null(p.ClaudeBin);
    }

    [Fact]
    public void TheSidebarWidthIsClamped()
    {
        var p = new Preferences();
        Settings.Apply(p, new PreferencesPatch { SidebarWidth = 5000 });
        Assert.Equal(StateValues.ClampSidebarWidth(5000), p.SidebarWidth);
    }

    [Theory]
    [InlineData(@"D:\Archives", true)]
    [InlineData(@"E:/chat zips", true)]
    [InlineData(@"\\server\share\zips", true)]
    [InlineData(@"C:\Archives", false)]
    [InlineData(@"c:/x", false)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"Archives", false)]
    [InlineData("", false)]
    public void ArchiveRootsMustBeAbsoluteAndOffDriveC(string root, bool ok)
    {
        Assert.Equal(ok, Settings.IsArchiveRoot(root));
        var p = new Preferences();
        Settings.Apply(p, new PreferencesPatch { ArchiveRoot = root });
        Assert.Equal(ok ? root : null, p.ArchiveRoot);
    }

    [Fact]
    public void FontSizeSteps()
    {
        Assert.Equal(14, Settings.FontSizeAfter(13, "bigger"));
        Assert.Equal(12, Settings.FontSizeAfter(13, "smaller"));
        Assert.Equal(StateValues.FontSizeNormal, Settings.FontSizeAfter(20, "reset"));
        Assert.Equal(StateValues.FontSizeMax, Settings.FontSizeAfter(StateValues.FontSizeMax, "bigger"));
        Assert.Equal(StateValues.FontSizeMin, Settings.FontSizeAfter(StateValues.FontSizeMin, "smaller"));
    }

    [Fact]
    public void FindClaudeTakesTheChosenOneElseThePath()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\tools\claude.cmd", @"D:\mine\claude.exe" };
        Assert.Equal(@"D:\mine\claude.exe", Settings.FindClaude(@"D:\mine\claude.exe", @"D:\tools", ".EXE;.CMD", files.Contains));
        Assert.Null(Settings.FindClaude(@"D:\gone\claude.exe", @"D:\tools", ".EXE;.CMD", files.Contains));
        Assert.Equal(@"D:\tools\claude.cmd", Settings.FindClaude(null, @"D:\empty;""D:\tools""", ".EXE;.CMD", files.Contains), StringComparer.OrdinalIgnoreCase);
        Assert.Null(Settings.FindClaude(null, @"D:\empty", null, files.Contains));
    }

    [Fact]
    public void StringsFillTheirValues()
    {
        Assert.Equal("Version 1.2", Strings.T("about.version", ("version", "1.2")));
        Assert.Equal("42% used", Strings.T("quota.used", ("percent", 42)));
        Assert.Equal("no.such.key", Strings.T("no.such.key"));
    }
}

/// <summary>Start with the system touches only the shell's own value of its profile.</summary>
public class AutostartTests
{
    sealed class FakeRunKeys : IRunKeys
    {
        public readonly Dictionary<string, string> Run = [];
        public readonly Dictionary<string, byte[]> Approved = [];
        public string? GetRun(string name) => Run.GetValueOrDefault(name);
        public void SetRun(string name, string command) => Run[name] = command;
        public void DeleteRun(string name) => Run.Remove(name);
        public byte[]? GetApproved(string name) => Approved.GetValueOrDefault(name);
        public void SetApproved(string name, byte[] value) => Approved[name] = value;
        public void DeleteApproved(string name) => Approved.Remove(name);
    }

    [Fact]
    public void OnlyItsOwnValueIsWrittenAndRemoved()
    {
        var keys = new FakeRunKeys();
        keys.Run["com.reframe.app"] = @"""C:\reframe\Reframe.exe"" --autostart";
        keys.Run["Other"] = "other.exe";
        var autostart = new Autostart(keys, @"D:\Profiles\Test", @"D:\shell\Reframe.Shell.exe");
        Assert.False(autostart.Enabled);
        autostart.Set(true);
        Assert.True(autostart.Enabled);
        Assert.Equal(@"""D:\shell\Reframe.Shell.exe"" --profile ""D:\Profiles\Test"" --autostart", keys.Run[autostart.Name]);
        Assert.Equal(3, keys.Run.Count);
        autostart.Set(false);
        Assert.False(autostart.Enabled);
        Assert.Equal(["Other", "com.reframe.app"], keys.Run.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(keys.Approved);
    }

    [Fact]
    public void TheNameIsPerProfileAndNeverReframes()
    {
        var a = Autostart.ValueName(@"D:\Profiles\Test");
        Assert.Equal(a, Autostart.ValueName(@"d:\profiles\test\"));
        Assert.NotEqual(a, Autostart.ValueName(@"D:\Profiles\Other"));
        Assert.StartsWith("Reframe Shell ", a);
        Assert.DoesNotContain("reframe.app", a);
    }

    [Fact]
    public void DisabledInTaskManagerReadsAsOff()
    {
        var keys = new FakeRunKeys();
        var autostart = new Autostart(keys, @"D:\P", @"D:\s.exe");
        autostart.Set(true);
        keys.Approved[autostart.Name] = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(autostart.Enabled);
    }
}

public class SettingsFixtureTests
{
    [Fact]
    public void QuotaAndHiddenThreadsAreRead()
    {
        const string json = """
            {"sessions":[],"quota":[{"source":"codex","windows":[{"id":"codex:300","minutes":300,"usedPercent":96,"resetsInHours":1}]}],
             "hidden":[{"id":"h1","title":"An experiment","project":"webshop","hoursAgo":1}]}
            """;
        var fixture = VisualFixture.Parse(json, @"D:\home", 1_000_000_000);
        var codex = Assert.Single(fixture.Quota);
        Assert.Equal(new QuotaWindow("codex:300", 300, 96, 1_000_000_000 + 3_600_000), Assert.Single(codex.Windows));
        var hidden = Assert.Single(fixture.State.HiddenThreads);
        Assert.Equal(("h1", "An experiment", Path.Combine(@"D:\home", "projects", "webshop"), 1_000_000_000 - 3_600_000.0),
            (hidden.ClaudeSessionId, hidden.Label, hidden.Cwd, hidden.HiddenAt));
    }
}
