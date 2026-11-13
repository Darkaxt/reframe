namespace Reframe.Shell.Core;

// Terminal windows dragged onto reframe (src/main/foreign.ts, foreign-match.ts, takeover.ts and app.ts's
// takeOverWindow): which windows are terminals, which claudes run in them, the watcher that follows their drags and
// the takeover itself. Everything that touches Windows (window events, consoles, processes) comes in through
// interfaces, so the decisions are tested with fakes and never against a real terminal.

/// <summary>A point on the screen in DIPs (Electron's screen.getCursorScreenPoint).</summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>A process's console window and that window's owner (the terminal window hosting the tab), as decimal
/// window handles; null when the process has no console or attaching failed (consolehelper/protocol.ts).</summary>
public sealed record ConsoleOwner(int Pid, string? ConsoleWindow, string? Owner, string? RootOwner);

/// <summary>A process attached to a console: its executable file name, null when it could not be read.</summary>
public sealed record AttachedProcess(int Pid, string? Name);

/// <summary>A console helper's answer (ConsoleResponse).</summary>
public sealed record ConsoleResult(bool Ok, string? Error = null, IReadOnlyList<ConsoleOwner>? Owners = null,
    IReadOnlyList<AttachedProcess>? Processes = null)
{
    public static ConsoleResult Failed(string error) => new(false, error);
}

/// <summary>What needs attaching to another process's console (src/consolehelper): done in a short-lived process of
/// its own, so the shell never switches consoles.</summary>
public interface IConsoleHelper
{
    /// <summary>Types Ctrl+C into the console of <paramref name="pid"/> <paramref name="times"/> times, as if pressed there.</summary>
    Task<ConsoleResult> InterruptAsync(int pid, int times, int intervalMs);
    /// <summary>Types text into the console of <paramref name="pid"/> ("\r" is Enter).</summary>
    Task<ConsoleResult> TypeAsync(int pid, string text);
    /// <summary>For each pid: its console window and that window's owner.</summary>
    Task<ConsoleResult> OwnersAsync(IReadOnlyList<int> pids);
    /// <summary>The processes attached to the console of <paramref name="pid"/>.</summary>
    Task<ConsoleResult> ConsoleProcessesAsync(int pid);
}

/// <summary>The Win32 calls the watcher makes (win32.ts), windows as decimal handles.</summary>
public interface IForeignWin32
{
    int ProcessId(string hwnd);
    /// <summary>The executable file name of a process, e.g. "WindowsTerminal.exe".</summary>
    string? ImageName(int pid);
    /// <summary>The window's size in DIPs; null when it has none.</summary>
    (double Width, double Height)? Size(string hwnd);
    bool IsWindow(string hwnd);
    string Title(string hwnd);
    string? Foreground();
    ScreenPoint Cursor();
}

/// <summary>A claude running outside reframe (Claude Code's session registry) and the Windows process whose console is
/// its terminal (the claude itself on Windows).</summary>
public sealed record OutsideClaude(RunningClaude Entry, int ConsolePid);

/// <summary>A terminal window released over one of the shell's windows with the claudes found in it.</summary>
public sealed record ForeignDrop(string Hwnd, string Title, string WindowId, ScreenPoint Point, IReadOnlyList<OutsideClaude> Sessions);

/// <summary>A session the takeover dialog offers (ForeignCandidate).</summary>
public sealed record ForeignCandidate(string ClaudeSessionId, string Cwd, string? Name, bool Preselected);

/// <summary>Pure decisions for taking over a terminal window that was dragged onto reframe (foreign-match.ts).</summary>
public static class ForeignMatch
{
    /// <summary>Programs that host terminal windows; dragging any other window is ignored right away.</summary>
    static readonly HashSet<string> TerminalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wezterm-gui.exe",
        "alacritty.exe", "mintty.exe", "ConEmu64.exe", "ConEmu.exe", "Tabby.exe", "Hyper.exe",
    };

    /// <summary>Shells (and console hosts) that may be left in a terminal tab once claude has gone.</summary>
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh.exe", "powershell.exe", "cmd.exe", "bash.exe", "sh.exe", "zsh.exe", "fish.exe", "nu.exe", "wsl.exe", "conhost.exe", "OpenConsole.exe",
    };

    public static bool IsTerminalHost(string? imageName) => !string.IsNullOrEmpty(imageName) && TerminalHosts.Contains(imageName);

    public static bool IsShell(string? imageName) => !string.IsNullOrEmpty(imageName) && Shells.Contains(imageName);

    /// <summary>Pids whose console lives in window <paramref name="hwnd"/>. Windows Terminal makes the window that hosts a
    /// tab the owner of the tab's (hidden) pseudo-console window; a classic console window is the console window itself.</summary>
    public static List<int> PidsInWindow(string hwnd, IEnumerable<ConsoleOwner> owners) =>
        owners.Where(o => o.ConsoleWindow == hwnd || o.Owner == hwnd || o.RootOwner == hwnd).Select(o => o.Pid).ToList();

    /// <summary>Pids of the claudes in the dragged window. A tab torn out of a Windows Terminal window lands in a new
    /// window, while its console may still name the old window for a moment, which is gone if that was its last tab: a
    /// live claude whose window is gone has moved into the window being dragged.</summary>
    public static List<int> PidsForDrop(string hwnd, IReadOnlyList<ConsoleOwner> owners, Func<string, bool> isWindow)
    {
        var direct = PidsInWindow(hwnd, owners);
        if (direct.Count > 0) return direct;
        return owners.Where(o => (o.Owner ?? o.ConsoleWindow) is { } window && !isWindow(window)).Select(o => o.Pid).ToList();
    }

    /// <summary>Whether nothing but shells runs in a console (an empty list means the console is already gone).</summary>
    public static bool OnlyShells(IEnumerable<AttachedProcess> processes) => processes.All(p => IsShell(p.Name));

    /// <summary>The session the window shows right now, judged by its title (Windows Terminal shows the active tab's).</summary>
    public static T? ActiveByTitle<T>(string title, IEnumerable<T> candidates, Func<T, string?> name) where T : class =>
        candidates.Where(c => name(c) is { Length: > 0 } n && title.Contains(n, StringComparison.Ordinal))
            .OrderByDescending(c => name(c)!.Length).FirstOrDefault();

    /// <summary>What the dialog offers for a drop: each claude by its name and folder; preselected the only one, or the one
    /// the window's title shows.</summary>
    public static List<ForeignCandidate> Candidates(ForeignDrop drop)
    {
        var active = ActiveByTitle(drop.Title, drop.Sessions, s => s.Entry.Name);
        return drop.Sessions.Select(s => new ForeignCandidate(s.Entry.SessionId, s.Entry.Cwd ?? "", s.Entry.Name,
            drop.Sessions.Count == 1 || ReferenceEquals(s, active))).ToList();
    }

    /// <summary>The folder name a candidate without a name shows (TakeoverDialog.tsx folderName).</summary>
    public static string FolderName(string cwd) =>
        cwd.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? cwd;
}

/// <summary>Ending a claude in another terminal and closing its tab afterwards (takeover.ts, app.ts closeIdleConsole).</summary>
public sealed class Takeover(IConsoleHelper console, Func<int, bool> isAlive, Func<int, Task> killTree, Func<int, Task> delay, Action<string> log)
{
    /// <summary>Ends a claude that runs outside reframe the way its user would: Ctrl+C twice in its terminal. Only when
    /// that does not work is its process tree killed. "exited", "killed" or "failed".</summary>
    public async Task<string> StopExternalClaudeAsync(int pid)
    {
        if (!isAlive(pid)) return "exited";
        var typed = await console.InterruptAsync(pid, 2, 700);
        if (!typed.Ok) log($"could not type Ctrl+C into the other terminal (pid {pid}): {typed.Error}");
        if (await ExitsWithinAsync(pid, 6000)) return "exited";
        log($"claude in the other terminal did not exit on Ctrl+C, killing it (pid {pid})");
        await killTree(pid);
        return await ExitsWithinAsync(pid, 3000) ? "killed" : "failed";
    }

    async Task<bool> ExitsWithinAsync(int pid, int ms)
    {
        for (var waited = 0; waited < ms; waited += 200)
        {
            if (!isAlive(pid)) return true;
            await delay(200);
        }
        return !isAlive(pid);
    }

    /// <summary>A shell in the console of <paramref name="consolePid"/>, to ask afterwards what is left there.</summary>
    public async Task<int?> ShellOfAsync(int consolePid)
    {
        var before = await console.ConsoleProcessesAsync(consolePid);
        log(before.Ok ? $"running in that terminal tab: {string.Join(", ", (before.Processes ?? []).Select(p => p.Name))}" : $"running in that terminal tab: {before.Error}");
        return before.Ok ? before.Processes?.FirstOrDefault(p => ForeignMatch.IsShell(p.Name))?.Pid : null;
    }

    /// <summary>Closes a terminal tab (console) that has nothing but shells left, by typing "exit" as its user would. Only
    /// that tab goes; other tabs of the same terminal window are never touched. Nested shells get one "exit" each.</summary>
    public async Task CloseIdleConsoleAsync(int shellPid)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await delay(500);
            var left = await console.ConsoleProcessesAsync(shellPid);
            if (!left.Ok)
            {
                log("terminal tab closed");
                return;
            }
            var processes = left.Processes ?? [];
            if (!ForeignMatch.OnlyShells(processes))
            {
                log($"terminal tab stays open, something else runs there: {string.Join(", ", processes.Select(p => p.Name))}");
                return;
            }
            // Any shell of the tab will do: they share the console input, and the one in front reads the "exit".
            var shell = processes.FirstOrDefault(p => ForeignMatch.IsShell(p.Name)
                && !string.Equals(p.Name, "conhost.exe", StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Name, "OpenConsole.exe", StringComparison.OrdinalIgnoreCase));
            if (shell is null) return;
            log($"only a shell is left in the terminal tab, typing exit ({shell.Name})");
            await console.TypeAsync(shell.Pid, "exit\r");
        }
    }

    /// <summary>The record for a session taken over (app.ts adopted): open, last used when its transcript was last
    /// written (opening it here is not using it), unused without a transcript (it then starts fresh under the same id).</summary>
    public static SessionRecord Adopted(string claudeSessionId, string cwd, string? name, double? transcriptModifiedAt, double now) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ClaudeSessionId = claudeSessionId,
        Cwd = cwd,
        Name = name,
        Lifecycle = "open",
        CreatedAt = now,
        LastActiveAt = transcriptModifiedAt is > 0 ? transcriptModifiedAt.Value : now,
        Used = transcriptModifiedAt is not null,
    };
}

/// <summary>
/// Follows title-bar drags of other apps' windows (foreign.ts). When a terminal window that hosts a claude running
/// outside reframe is dragged over one of the shell's windows, it reports the pointer (<see cref="Hover"/>, so the
/// window can show where it would land) and, on release, the drop with the sessions found in that window
/// (<see cref="Dropped"/>). Dragging a tab out of Windows Terminal is no window move: a terminal window that appears over
/// one of the shell's windows holding a claude that ran before it existed counts as such a drop too, and while such a
/// tab is dragged over a window, the window reports the pointer itself (<see cref="NotePointer"/>). Runs on one thread
/// (the UI's: window events and its continuations).
/// </summary>
public sealed class ForeignWindowWatcher(IForeignWin32 win32, IConsoleHelper console, Func<ScreenPoint, string?> windowAt,
    Func<Task<List<OutsideClaude>>> externalSessions, Action<string> log, Func<double> now, Func<int, Task> delay)
{
    public const int TrackMs = 50, ReprobeMs = 700, NewWindowMs = 10_000, PointerMs = 3_000, TabDragIdleMs = 500;
    /// <summary>When to look into a new terminal window: the moved tab's console may name its window only after a moment.</summary>
    static readonly int[] AppearedProbesMs = [0, 300, 800, 1500, 2500];

    /// <summary>The pointer moved over one of the shell's windows during such a drag; null when it left it again.</summary>
    public event Action<string, ScreenPoint?>? Hover;
    public event Action<ForeignDrop>? Dropped;

    /// <summary>Test hook: the window a simulated tab drag comes from (instead of the window in front).</summary>
    public string? TabDragSource { get; set; }

    sealed class Drag(string hwnd)
    {
        public string Hwnd { get; } = hwnd;
        /// <summary>Sessions found in the window so far; null until the first lookup is done.</summary>
        public List<OutsideClaude>? Sessions { get; set; }
        public Task<List<OutsideClaude>>? Probing { get; set; }
        public double LastProbe { get; set; }
        public bool Tracking { get; set; }
        public string? Hovered { get; set; }
    }

    sealed class TabDrag(string windowId)
    {
        public string WindowId { get; set; } = windowId;
        /// <summary>Whether the terminal window the tab comes from holds a claude; null while that is being looked up.</summary>
        public bool? HoldsClaude { get; set; }
        public int Idle { get; set; }
    }

    Drag? drag;
    TabDrag? tabDrag;
    (ScreenPoint Point, double At)? pointer;
    readonly Dictionary<string, double> created = [];
    readonly Dictionary<int, string?> hosts = [];

    /// <summary>EVENT_SYSTEM_MOVESIZESTART / END of a window.</summary>
    public void MoveSize(bool start, string hwnd)
    {
        if (start) Begin(hwnd);
        else _ = EndAsync(hwnd);
    }

    /// <summary>EVENT_OBJECT_CREATE / SHOW of a top-level window: one shown soon after its creation is a new window.</summary>
    public void TopLevel(bool createdNow, string hwnd)
    {
        var at = now();
        if (createdNow)
        {
            foreach (var (window, time) in created.ToList())
                if (at - time > NewWindowMs) created.Remove(window);
            created[hwnd] = at;
            return;
        }
        if (created.Remove(hwnd, out var createdAt) && at - createdAt <= NewWindowMs) Appeared(hwnd);
    }

    /// <summary>A window of the shell's reports the pointer during a drag it does not know (null when the drag left it).
    /// Windows Terminal's tab drags offer no data; they come from the window in front.</summary>
    public void NotePointer(string windowId, ScreenPoint? point, bool hasData)
    {
        if (point is not { } at || hasData)
        {
            EndTabDrag();
            return;
        }
        pointer = (at, now());
        var current = tabDrag;
        if (current is not null && current.WindowId != windowId)
        {
            if (current.HoldsClaude == true) Hover?.Invoke(current.WindowId, null);
            current.WindowId = windowId;
        }
        if (current is null)
        {
            var started = current = tabDrag = new TabDrag(windowId);
            var source = TabDragSource ?? win32.Foreground();
            var host = source is null ? null : HostOf(source);
            if (source is null || !ForeignMatch.IsTerminalHost(host)) started.HoldsClaude = false;
            else _ = LookIntoTabSourceAsync(started, source);
            log($"something from another app is dragged over reframe (from {host ?? "unknown"})");
        }
        _ = EndTabDragWhenIdleAsync(current, ++current.Idle);
        if (current.HoldsClaude == true) Hover?.Invoke(windowId, at);
    }

    async Task LookIntoTabSourceAsync(TabDrag started, string source)
    {
        var sessions = await SessionsInAsync(source);
        if (tabDrag != started) return;
        started.HoldsClaude = sessions.Count > 0;
        if (started.HoldsClaude == true && pointer is { } p) Hover?.Invoke(started.WindowId, p.Point);
    }

    async Task EndTabDragWhenIdleAsync(TabDrag current, int idle)
    {
        await delay(TabDragIdleMs);
        if (tabDrag == current && current.Idle == idle) EndTabDrag();
    }

    void EndTabDrag()
    {
        var current = tabDrag;
        tabDrag = null;
        if (current?.HoldsClaude == true) Hover?.Invoke(current.WindowId, null);
    }

    /// <summary>A new terminal window was shown: when it holds a claude that ran before and appeared over one of the
    /// shell's windows, it is a tab torn out over reframe. <paramref name="point"/> (tests) stands in for the pointer.</summary>
    public void Appeared(string hwnd, ScreenPoint? point = null)
    {
        var host = HostOf(hwnd);
        if (!ForeignMatch.IsTerminalHost(host)) return;
        if (win32.Size(hwnd) is not { Width: > 0, Height: > 0 }) return; // helper windows of shells, never seen
        var shownAt = now();
        var recent = pointer is { } p && shownAt - p.At <= PointerMs ? p.Point : (ScreenPoint?)null;
        var at = point ?? recent ?? win32.Cursor();
        _ = AppearedAsync(hwnd, host!, at, windowAt(at), shownAt);
    }

    async Task AppearedAsync(string hwnd, string host, ScreenPoint at, string? windowId, double shownAt)
    {
        // Only claudes that already ran before the window appeared: one started in a new window was not dropped here.
        var external = (await externalSessions()).Where(c => c.Entry.StartedAt is not { } started || started < shownAt).ToList();
        if (external.Count == 0) return;
        log($"new terminal window appeared ({host}, over reframe: {windowId is not null})");
        if (windowId is null) return;
        var sessions = await ProbeAppearedAsync(hwnd, external);
        log($"claudes in the new terminal window: {sessions.Count}");
        if (sessions.Count > 0) Dropped?.Invoke(new ForeignDrop(hwnd, win32.Title(hwnd), windowId, at, sessions));
    }

    async Task<List<OutsideClaude>> ProbeAppearedAsync(string hwnd, List<OutsideClaude> external)
    {
        var waited = 0;
        foreach (var at in AppearedProbesMs)
        {
            if (at > waited) await delay(at - waited);
            waited = at;
            if (!win32.IsWindow(hwnd)) return [];
            var sessions = await SessionsInAsync(hwnd, external);
            if (sessions.Count > 0) return sessions;
        }
        return [];
    }

    string? HostOf(string hwnd)
    {
        var pid = win32.ProcessId(hwnd);
        if (!hosts.TryGetValue(pid, out var name))
        {
            if (hosts.Count > 500) hosts.Clear();
            hosts[pid] = name = win32.ImageName(pid);
        }
        return name;
    }

    /// <summary>Claude sessions whose console lives in terminal window <paramref name="hwnd"/>.</summary>
    public async Task<List<OutsideClaude>> SessionsInAsync(string hwnd, List<OutsideClaude>? known = null)
    {
        var external = known ?? await externalSessions();
        if (external.Count == 0) return [];
        var result = await console.OwnersAsync(external.Select(c => c.ConsolePid).Distinct().ToList());
        if (!result.Ok)
        {
            log($"could not map consoles to terminal windows: {result.Error}");
            return [];
        }
        var owners = result.Owners ?? [];
        var pids = ForeignMatch.PidsForDrop(hwnd, owners, win32.IsWindow);
        if (pids.Count > 0 && ForeignMatch.PidsInWindow(hwnd, owners).Count == 0) log("matched a torn-out terminal tab by its vanished window");
        return external.Where(c => pids.Contains(c.ConsolePid)).ToList();
    }

    void Begin(string hwnd)
    {
        Cancel();
        // Moving windows is frequent: only terminals, and only while some claude runs outside reframe, cost more.
        var host = HostOf(hwnd);
        if (!ForeignMatch.IsTerminalHost(host)) return;
        var started = drag = new Drag(hwnd);
        _ = BeginAsync(started, host!);
    }

    async Task BeginAsync(Drag started, string host)
    {
        var external = await externalSessions();
        if (drag != started) return; // released (or another drag began) meanwhile: EndAsync looked for itself
        if (external.Count == 0)
        {
            drag = null;
            return;
        }
        log($"terminal window drag started ({host}, claudes elsewhere: {external.Count})");
        started.Tracking = true;
        _ = ProbeAsync(started, external);
        while (drag == started)
        {
            await delay(TrackMs);
            if (drag != started) break;
            Track(started);
        }
    }

    Task<List<OutsideClaude>> ProbeAsync(Drag current, List<OutsideClaude>? external = null)
    {
        if (current.Probing is null)
        {
            current.LastProbe = now();
            current.Probing = Run();
        }
        return current.Probing;

        async Task<List<OutsideClaude>> Run()
        {
            var sessions = await SessionsInAsync(current.Hwnd, external);
            current.Sessions = sessions;
            current.Probing = null;
            return sessions;
        }
    }

    void Track(Drag current)
    {
        var point = win32.Cursor();
        var over = windowAt(point);
        if (current.Sessions is not { Count: > 0 })
        {
            // Windows Terminal may move a torn-out tab's console to its new window only after a moment: ask again.
            if (over is not null && current.Sessions is not null && now() - current.LastProbe > ReprobeMs) _ = ProbeAsync(current);
            return;
        }
        if (current.Hovered is not null && current.Hovered != over) Hover?.Invoke(current.Hovered, null);
        current.Hovered = over;
        if (over is not null) Hover?.Invoke(over, point);
    }

    async Task EndAsync(string hwnd)
    {
        var current = drag;
        if (current is null || current.Hwnd != hwnd) return;
        Cancel();
        var point = win32.Cursor();
        var windowId = windowAt(point);
        var sessions = current.Sessions ?? await ProbeAsync(current);
        // Dropped on the shell with nothing found yet: one more look, now that the window has settled.
        if (windowId is not null && sessions.Count == 0) sessions = await SessionsInAsync(hwnd);
        if (current.Tracking || windowId is not null) log($"terminal window drag ended (over reframe: {windowId is not null}, sessions: {sessions.Count})");
        if (windowId is null || sessions.Count == 0) return;
        Dropped?.Invoke(new ForeignDrop(hwnd, win32.Title(hwnd), windowId, point, sessions));
    }

    void Cancel()
    {
        var current = drag;
        drag = null;
        if (current?.Hovered is { } hovered) Hover?.Invoke(hovered, null);
    }
}
