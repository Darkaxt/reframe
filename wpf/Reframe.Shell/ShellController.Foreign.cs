using System.Diagnostics;
using System.Windows;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Terminal windows dragged onto the shell (app.ts watchForeignWindows, takeOverWindow): the watcher follows other
/// apps' window drags; a terminal window holding claudes that run outside reframe, released (or torn out) over one of
/// the shell's windows, is offered for takeover there. The claudes picked are ended in their terminal (Ctrl+C twice,
/// else their process tree), placed in the window as sessions of reframe's, and a tab left with nothing but a shell
/// is closed. Claudes in WSL are not looked for yet (the shell has no WSL sources).
/// </summary>
sealed partial class ShellController
{
    ForeignWin32? foreignHooks;
    ForeignWindowWatcher? foreign;
    readonly HashSet<string> takingOver = [];
    readonly IConsoleHelper consoles = new ConsoleHelperClient();
    Takeover? takeover;

    static void Log(string message) => Trace.WriteLine($"foreign: {message}");

    static double Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>Starts following window drags; only as the primary client that may change reframe's state.</summary>
    void WatchForeignWindows()
    {
        if (Fixture is not null || Store is not { ReadOnly: false }) return;
        takeover = new Takeover(consoles, ClaudeRegistry.Alive, KillTreeAsync, ms => Task.Delay(ms), Log);
        var hooks = new ForeignWin32();
        var watcher = foreign = new ForeignWindowWatcher(hooks, consoles, p => WindowAt(new Point(p.X, p.Y)), OutsideClaudesAsync, Log, Now, ms => Task.Delay(ms));
        watcher.Hover += (windowId, point) =>
        {
            if (windows.TryGetValue(windowId, out var window)) window.ForeignHover(point);
        };
        watcher.Dropped += drop => _ = TakeOverWindowAsync(drop);
        try
        {
            hooks.Start(watcher.MoveSize, watcher.TopLevel);
            foreignHooks = hooks;
        }
        catch (InvalidOperationException e)
        {
            hooks.Dispose();
            Log($"cannot follow window drags: {e.Message}");
        }
    }

    void StopWatchingForeignWindows()
    {
        foreignHooks?.Dispose();
        foreignHooks = null;
        foreign = null;
    }

    /// <summary>A window reports the pointer during a drag it does not know (a Windows Terminal tab), or null when it left.</summary>
    public void NoteForeignPointer(string windowId, Point? point, bool hasData) =>
        foreign?.NotePointer(windowId, point is { } p ? new ScreenPoint(p.X, p.Y) : null, hasData);

    static Task KillTreeAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Log($"could not kill {pid}: {e.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>Claudes on Windows that run outside reframe: the registry's live entries with a folder that are neither
    /// reframe's sessions nor hidden threads (outsideClaudes).</summary>
    Task<List<OutsideClaude>> OutsideClaudesAsync()
    {
        var own = State.Sessions.Values.Select(r => r.ClaudeSessionId).Concat(State.HiddenThreads.Select(h => h.ClaudeSessionId)).ToHashSet();
        var home = ClaudeRegistry.Home();
        return Task.Run(() => ClaudeRegistry.Running(home)
            .Where(e => !string.IsNullOrEmpty(e.Cwd) && !own.Contains(e.SessionId))
            .Select(e => new OutsideClaude(e, e.Pid)).ToList());
    }

    /// <summary>Asks (in the window it was dropped on) which sessions to take over, takes them over, closes the tab.</summary>
    async Task TakeOverWindowAsync(ForeignDrop drop)
    {
        // Some terminals report the end of a window move twice; one question per window is enough.
        if (!takingOver.Add(drop.Hwnd))
        {
            Log("repeated drop of the same terminal window ignored");
            return;
        }
        try
        {
            await TakeOverWindowOnceAsync(drop);
        }
        catch (Exception e)
        {
            Log($"taking over a terminal window failed: {e}");
        }
        finally
        {
            _ = Task.Delay(2000).ContinueWith(_ => Ui.InvokeAsync(() => takingOver.Remove(drop.Hwnd)));
        }
    }

    async Task TakeOverWindowOnceAsync(ForeignDrop drop)
    {
        Log($"terminal window dropped on reframe ({drop.Sessions.Count} sessions)");
        if (takeover is null || !windows.TryGetValue(drop.WindowId, out var window)) return;
        window.BringForward();
        var answer = await window.AskTakeoverAsync(ForeignMatch.Candidates(drop), new Point(drop.Point.X, drop.Point.Y));
        var chosen = drop.Sessions.Where(s => answer?.Ids.Contains(s.Entry.SessionId) == true).ToList();
        if (chosen.Count == 0)
        {
            Log("terminal window takeover cancelled");
            return;
        }
        Log($"terminal window takeover confirmed ({chosen.Count} sessions)");
        var target = answer!.Value.Target;
        var home = ClaudeRegistry.Home();
        var closeTabs = new List<Func<Task>>();
        foreach (var (entry, consolePid) in chosen)
        {
            // A shell in that console, to ask it afterwards what is left there.
            var shell = await takeover.ShellOfAsync(consolePid);
            var latest = await Task.Run(() => ClaudeRegistry.Entry(home, entry.Pid)) is { } now && now.SessionId == entry.SessionId ? now : entry;
            var stopped = await takeover.StopExternalClaudeAsync(entry.Pid);
            Log($"other terminal stopped: {entry.Pid} {stopped}");
            if (stopped == "failed") continue;
            var modified = await Task.Run(() => ClaudeRegistry.TranscriptModifiedAt(home, latest.SessionId));
            // A name the user gave it survives; one Claude made up is made up again.
            var record = Takeover.Adopted(latest.SessionId, latest.Cwd ?? entry.Cwd!, latest.NameSource == "user" ? latest.Name : null, modified, Now());
            State.Sessions[record.Id] = record;
            window.PlaceSession(record.Id, target);
            // Continued here: resumed (it was ended in its terminal just now).
            _ = StartOrTakeOverAsync(record.Id, "resume", drop.WindowId);
            target = new DropTarget(Layout.TileKey(record.Id), "right");
            // Without a shell, the tab closed on its own together with claude.
            if (shell is { } pid) closeTabs.Add(() => takeover.CloseIdleConsoleAsync(pid));
        }
        foreach (var close in closeTabs) await close();
    }
}
