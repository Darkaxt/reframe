using System.Text.RegularExpressions;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Terminal windows dragged onto reframe and their takeover (S12): foreign-match.test.ts ported, and the watcher
/// and the takeover against fakes (no real window, console or process is ever touched).</summary>
public class ForeignTests
{
    // ---------- foreign-match.test.ts ----------

    [Fact]
    public void Recognizes_terminal_hosts_regardless_of_case()
    {
        Assert.True(ForeignMatch.IsTerminalHost("WindowsTerminal.exe"));
        Assert.True(ForeignMatch.IsTerminalHost("windowsterminal.EXE"));
        Assert.False(ForeignMatch.IsTerminalHost("chrome.exe"));
        Assert.False(ForeignMatch.IsTerminalHost(null));
    }

    [Fact]
    public void Maps_consoles_to_the_window_hosting_them()
    {
        ConsoleOwner[] owners =
        [
            new(1, "100", "500", "500"), // tab in Windows Terminal window 500
            new(2, "200", "600", "600"), // tab in another window
            new(3, "700", null, "700"), // classic console window 700
            new(4, null, null, null), // could not attach
        ];
        Assert.Equal([1], ForeignMatch.PidsInWindow("500", owners));
        Assert.Equal([3], ForeignMatch.PidsInWindow("700", owners));
        Assert.Empty(ForeignMatch.PidsInWindow("999", owners));
    }

    [Fact]
    public void Finds_a_torn_out_tab_by_the_window_its_console_still_names_which_is_gone()
    {
        ConsoleOwner[] owners = [new(1, "100", "500", "500"), new(2, "200", "600", "600")];
        bool Exists(string hwnd) => hwnd != "600";
        Assert.Equal([2], ForeignMatch.PidsForDrop("900", owners, Exists));
        Assert.Equal([1], ForeignMatch.PidsForDrop("500", owners, Exists)); // a direct match wins
        Assert.Empty(ForeignMatch.PidsForDrop("900", owners, _ => true));
    }

    [Fact]
    public void Closes_only_consoles_with_nothing_but_shells_left()
    {
        Assert.True(ForeignMatch.OnlyShells([new(1, "pwsh.exe"), new(2, "OpenConsole.exe")]));
        Assert.False(ForeignMatch.OnlyShells([new(1, "pwsh.exe"), new(2, "node.exe")]));
        Assert.False(ForeignMatch.OnlyShells([new(1, null)]));
        Assert.True(ForeignMatch.OnlyShells([]));
    }

    [Fact]
    public void Picks_the_session_whose_name_the_window_title_shows()
    {
        string?[] candidates = ["api", "api-docs", null];
        Assert.Equal("api-docs", ForeignMatch.ActiveByTitle("✳ api-docs", candidates.Select(n => new Named(n)), c => c.Name)?.Name);
        Assert.Null(ForeignMatch.ActiveByTitle("✳ Claude Code", candidates.Select(n => new Named(n)), c => c.Name));
    }

    sealed record Named(string? Name);

    // ---------- the dialog's candidates and the record ----------

    static OutsideClaude Outside(int pid, string id, string? name, string cwd = @"C:\src\api", double? startedAt = null) =>
        new(new RunningClaude(pid, id, cwd, name, startedAt), pid);

    [Fact]
    public void A_single_session_is_preselected_of_several_the_one_the_title_shows()
    {
        var one = ForeignMatch.Candidates(new ForeignDrop("1", "pwsh", "w", default, [Outside(10, "a", null)]));
        Assert.True(one.Single().Preselected);
        var two = ForeignMatch.Candidates(new ForeignDrop("1", "✳ docs", "w", default, [Outside(10, "a", "api"), Outside(11, "b", "docs")]));
        Assert.Equal([false, true], two.Select(c => c.Preselected));
        Assert.Equal("api", ForeignMatch.FolderName(@"C:\src\api\"));
    }

    [Fact]
    public void A_session_taken_over_is_open_and_last_used_when_its_transcript_was_written()
    {
        var used = Takeover.Adopted("c", @"C:\src", null, 1000, 5000);
        Assert.Equal(("open", 1000.0, true, 5000.0), (used.Lifecycle, used.LastActiveAt, used.Used, used.CreatedAt));
        var fresh = Takeover.Adopted("c", @"C:\src", "named", null, 5000);
        Assert.Equal((5000.0, false, "named"), (fresh.LastActiveAt, fresh.Used, fresh.Name));
        Assert.NotEqual(used.Id, fresh.Id);
    }

    // ---------- takeover.ts and closeIdleConsole, against fakes ----------

    sealed class FakeConsole : IConsoleHelper
    {
        public List<string> Calls { get; } = [];
        public Func<int, ConsoleResult> Processes { get; set; } = _ => ConsoleResult.Failed("gone");
        public Func<IReadOnlyList<int>, ConsoleResult> Owners { get; set; } = _ => new ConsoleResult(true, Owners: []);
        public Action<int>? Interrupted { get; set; }

        public Task<ConsoleResult> InterruptAsync(int pid, int times, int intervalMs)
        {
            Calls.Add($"interrupt {pid} x{times}");
            Interrupted?.Invoke(pid);
            return Task.FromResult(new ConsoleResult(true));
        }

        public Task<ConsoleResult> TypeAsync(int pid, string text)
        {
            Calls.Add($"type {pid} {text.Replace("\r", "\\r")}");
            return Task.FromResult(new ConsoleResult(true));
        }

        public Task<ConsoleResult> OwnersAsync(IReadOnlyList<int> pids)
        {
            Calls.Add($"owners {string.Join(",", pids)}");
            return Task.FromResult(Owners(pids));
        }

        public Task<ConsoleResult> ConsoleProcessesAsync(int pid)
        {
            Calls.Add($"processes {pid}");
            return Task.FromResult(Processes(pid));
        }
    }

    static Takeover Taking(FakeConsole console, HashSet<int> alive, List<int> killed) =>
        new(console, alive.Contains, pid =>
        {
            killed.Add(pid);
            alive.Remove(pid);
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, _ => { });

    [Fact]
    public async Task Claude_ends_on_Ctrl_C_twice_and_is_killed_only_when_it_does_not()
    {
        var console = new FakeConsole();
        var alive = new HashSet<int> { 10, 11 };
        var killed = new List<int>();
        console.Interrupted = pid =>
        {
            if (pid == 10) alive.Remove(pid);
        };
        var takeover = Taking(console, alive, killed);
        Assert.Equal("exited", await takeover.StopExternalClaudeAsync(10));
        Assert.Equal("killed", await takeover.StopExternalClaudeAsync(11));
        Assert.Equal("exited", await takeover.StopExternalClaudeAsync(12));
        Assert.Equal(["interrupt 10 x2", "interrupt 11 x2"], console.Calls);
        Assert.Equal([11], killed);
    }

    [Fact]
    public async Task A_tab_left_with_only_shells_gets_exit_typed_one_with_anything_else_stays()
    {
        var console = new FakeConsole();
        var typed = 0;
        console.Processes = pid => typed switch
        {
            0 => new ConsoleResult(true, Processes: [new(5, "cmd.exe"), new(6, "pwsh.exe"), new(7, "OpenConsole.exe")]),
            1 => new ConsoleResult(true, Processes: [new(5, "cmd.exe"), new(7, "OpenConsole.exe")]),
            _ => ConsoleResult.Failed("gone"),
        };
        var takeover = new Takeover(new CountingConsole(console, () => typed++), _ => true, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { });
        await takeover.CloseIdleConsoleAsync(6);
        Assert.Equal(["processes 6", "type 5 exit\\r", "processes 6", "type 5 exit\\r", "processes 6"], console.Calls);

        var busy = new FakeConsole { Processes = _ => new ConsoleResult(true, Processes: [new(5, "pwsh.exe"), new(8, "node.exe")]) };
        await Taking(busy, [], []).CloseIdleConsoleAsync(5);
        Assert.Equal(["processes 5"], busy.Calls);
        Assert.Equal(5, await Taking(busy, [], []).ShellOfAsync(5));
    }

    sealed class CountingConsole(FakeConsole inner, Action typed) : IConsoleHelper
    {
        public Task<ConsoleResult> InterruptAsync(int pid, int times, int intervalMs) => inner.InterruptAsync(pid, times, intervalMs);
        public Task<ConsoleResult> TypeAsync(int pid, string text)
        {
            typed();
            return inner.TypeAsync(pid, text);
        }
        public Task<ConsoleResult> OwnersAsync(IReadOnlyList<int> pids) => inner.OwnersAsync(pids);
        public Task<ConsoleResult> ConsoleProcessesAsync(int pid) => inner.ConsoleProcessesAsync(pid);
    }

    // ---------- the watcher (foreign.ts), against fakes ----------

    sealed class FakeWin32 : IForeignWin32
    {
        public Dictionary<string, (int Pid, string Title)> Windows { get; } = [];
        public Dictionary<int, string> Images { get; } = [];
        public ScreenPoint Pointer { get; set; }
        public int ProcessId(string hwnd) => Windows.TryGetValue(hwnd, out var w) ? w.Pid : 0;
        public string? ImageName(int pid) => Images.GetValueOrDefault(pid);
        public (double Width, double Height)? Size(string hwnd) => Windows.ContainsKey(hwnd) ? (800, 600) : null;
        public bool IsWindow(string hwnd) => Windows.ContainsKey(hwnd);
        public string Title(string hwnd) => Windows.TryGetValue(hwnd, out var w) ? w.Title : "";
        public string? Foreground() => null;
        public ScreenPoint Cursor() => Pointer;
    }

    static (ForeignWindowWatcher Watcher, FakeWin32 Win32, FakeConsole Console, List<ForeignDrop> Drops, List<OutsideClaude> Outside) Watching(double now = 1000)
    {
        var win32 = new FakeWin32();
        win32.Windows["500"] = (40, "✳ docs");
        win32.Windows["501"] = (41, "Chrome");
        win32.Images[40] = "WindowsTerminal.exe";
        win32.Images[41] = "chrome.exe";
        var console = new FakeConsole();
        var outside = new List<OutsideClaude>();
        var watcher = new ForeignWindowWatcher(win32, console, p => p.X < 100 ? "reframe-window" : null, () => Task.FromResult(outside.ToList()),
            _ => { }, () => now, _ => Task.Delay(1));
        var drops = new List<ForeignDrop>();
        watcher.Dropped += drops.Add;
        return (watcher, win32, console, drops, outside);
    }

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(5);
    }

    [Fact]
    public async Task A_terminal_window_released_over_a_window_is_a_drop_with_its_claudes()
    {
        var (watcher, win32, console, drops, outside) = Watching();
        outside.AddRange([Outside(10, "a", "api"), Outside(11, "b", "docs")]);
        console.Owners = _ => new ConsoleResult(true, Owners: [new(10, "100", "500", "500"), new(11, "200", "900", "900")]);
        win32.Pointer = new ScreenPoint(50, 50);
        watcher.MoveSize(true, "500");
        await Task.Delay(30);
        watcher.MoveSize(false, "500");
        await Until(() => drops.Count > 0);
        var drop = Assert.Single(drops);
        Assert.Equal(("500", "reframe-window", "✳ docs"), (drop.Hwnd, drop.WindowId, drop.Title));
        Assert.Equal(["a"], drop.Sessions.Select(s => s.Entry.SessionId));
    }

    [Fact]
    public async Task Other_windows_and_drops_beside_reframe_are_no_drop()
    {
        var (watcher, win32, console, drops, outside) = Watching();
        outside.Add(Outside(10, "a", "api"));
        console.Owners = _ => new ConsoleResult(true, Owners: [new(10, "100", "501", "501")]);
        win32.Pointer = new ScreenPoint(50, 50);
        watcher.MoveSize(true, "501"); // a browser
        watcher.MoveSize(false, "501");
        win32.Pointer = new ScreenPoint(500, 50); // released beside the shell's windows
        watcher.MoveSize(true, "500");
        watcher.MoveSize(false, "500");
        await Task.Delay(50);
        Assert.Empty(drops);
    }

    [Fact]
    public async Task A_new_terminal_window_over_reframe_holding_a_claude_that_ran_before_is_a_torn_out_tab()
    {
        var (watcher, win32, console, drops, outside) = Watching(now: 1000);
        outside.AddRange([Outside(10, "a", "api", startedAt: 500), Outside(11, "b", "docs", startedAt: 2000)]);
        console.Owners = _ => new ConsoleResult(true, Owners: [new(10, "100", "500", "500"), new(11, "200", "500", "500")]);
        watcher.TopLevel(true, "500");
        watcher.TopLevel(false, "500");
        win32.Pointer = new ScreenPoint(50, 50);
        await Until(() => drops.Count > 0);
        // Its claude started after the window appeared was not dropped here.
        Assert.Equal(["a"], Assert.Single(drops).Sessions.Select(s => s.Entry.SessionId));
        Assert.Contains("owners 10", console.Calls);
    }

    [Fact]
    public async Task Nothing_is_looked_up_while_no_claude_runs_outside_reframe()
    {
        var (watcher, win32, console, drops, _) = Watching();
        win32.Pointer = new ScreenPoint(50, 50);
        watcher.MoveSize(true, "500");
        watcher.MoveSize(false, "500");
        watcher.Appeared("500", new ScreenPoint(50, 50));
        await Task.Delay(50);
        Assert.Empty(drops);
        Assert.Empty(console.Calls);
    }

    // ---------- where it lands, and the texts ----------

    [Fact]
    public void A_terminal_window_lands_beside_the_tile_under_it_or_at_the_end()
    {
        var group = new GroupNode { Id = "g", Child = new TileNode { SessionId = "x" } };
        var places = new LayoutPlaces(
            [new TilePlace("a", new Box(0, 0, 400, 400), null), new TilePlace("x", new Box(500, 30, 300, 300), group)],
            [new GroupPlace(group, new Box(496, 0, 308, 340))], []);
        var area = new Box(0, 0, 900, 400);
        Assert.Equal(new DropTarget("tile:a", "right"), LayoutGeometry.ForeignTargetAt(places, area, 390, 200, out var preview));
        Assert.Equal(new Box(200, 0, 200, 400), preview);
        Assert.Equal(new DropTarget("tile:x", "right"), LayoutGeometry.ForeignTargetAt(places, area, 498, 10, out _));
        Assert.Null(LayoutGeometry.ForeignTargetAt(places, area, 850, 390, out preview));
        Assert.Null(preview);
        Assert.Null(LayoutGeometry.ForeignTargetAt(new LayoutPlaces([], [], []), area, 10, 10, out preview));
        Assert.Equal(area, preview);
    }

    [Fact]
    public void The_dialog_s_texts_are_i18n_ts_s_english_ones()
    {
        var source = File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "src", "shared", "i18n.ts"));
        var english = source[..source.IndexOf("'tray.window': 'Fenster", StringComparison.Ordinal)];
        foreach (var key in new[] { "foreign.title", "foreign.single", "foreign.multiple", "foreign.detail", "takeover.confirm", "takeover.failed" })
        {
            var match = Regex.Match(english, $@"'{Regex.Escape(key)}':\s*(['""])(.*?)\1,");
            Assert.True(match.Success, key);
            Assert.Equal(match.Groups[2].Value.Replace("\\'", "'"), Strings.En[key]);
        }
    }
}
