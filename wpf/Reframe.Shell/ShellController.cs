using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Reframe.Shell.Core;
using WindowState = Reframe.Shell.Core.WindowState;

namespace Reframe.Shell;

/// <summary>
/// What src/main/app.ts and windows.ts do for all windows: one host connection and one state for the profile, a
/// MainWindow per entry of the state's <c>windows</c> (bounds, maximised, full screen, hidden), New window, tear-out and
/// moving a tile to another window, the close-window policy (preferences.closeWindow), and the tray (tray.ts) with
/// Terminate all sessions and Quit. Closing a window never quits: like reframe, the shell stays in the tray; Quit leaves
/// every session running in the host (never <c>shutdown</c>).
/// </summary>
sealed partial class ShellController
{
    public AppState State { get; set; } = AppState.CreateDefault();
    public StateStore? Store { get; private set; }
    public HostConnection? Connection { get; private set; }
    public HostServices? Services { get; private set; }
    public VisualFixture? Fixture { get; set; }
    /// <summary>Host sessions listed though reframe's state does not have them: never written to it.</summary>
    public HashSet<string> Synthetic { get; } = [];
    public Dictionary<string, HostSession> HostSessions { get; } = [];
    public Dictionary<string, SessionStatus> HostStatus { get; } = [];
    public string Status { get; private set; } = "";
    public bool Quitting { get; private set; }

    public event Action? StatusChanged;

    IDisposable? shellLock;
    readonly Dictionary<string, MainWindow> windows = [];
    readonly Dictionary<string, long> focusedAt = [];
    readonly HashSet<string> askingToClose = [];
    long focusCount;
    TrayIcon? tray;
    Toasts? toasts;
    readonly Dictionary<string, int> attention = [];
    DispatcherTimer? trayTimer;

    static Dispatcher Ui => Application.Current.Dispatcher;

    public void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    /// <summary>Whether the shell may change reframe's state: as the primary client with a writable store (a capture
    /// changes only its fixture).</summary>
    public bool Writable => Fixture is not null || Store is { ReadOnly: false };

    public string StatusOf(string sessionId) =>
        Fixture?.Statuses.GetValueOrDefault(sessionId)
        ?? (HostStatus.TryGetValue(sessionId, out var known) ? known.Status : HostSessions.ContainsKey(sessionId) ? "starting" : "ended");

    public bool Runs(string sessionId) =>
        Fixture is not null ? Fixture.Statuses.GetValueOrDefault(sessionId, "ended") != "ended" : HostSessions.ContainsKey(sessionId) && StatusOf(sessionId) != "ended";

    // ---------- chat history and WSL (shared by the windows) ----------

    /// <summary>Claude session ids with a history operation going on (historyBusy).</summary>
    public HashSet<string> HistoryBusy { get; } = [];
    /// <summary>A selected-chats batch runs (chatBatchRunning): one at a time.</summary>
    public bool ChatBatchRunning { get; set; }
    bool wslKept;
    (DateTime At, IReadOnlyList<WslSource> Sources, IReadOnlyList<string> Stopped)? wslScan;
    Task? wslScanning;

    /// <summary>How often the list asks WSL again what runs (asking keeps it from stopping when idle).</summary>
    static readonly TimeSpan WslMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>The WSL sources and stopped distributions as last seen (claudeSources); looked at again in the
    /// background when older than <see cref="WslMaxAge"/>, only with Settings' "look in WSL" on.</summary>
    public (IReadOnlyList<WslSource> Sources, IReadOnlyList<string> Stopped) Wsl(Action changed)
    {
        if (Fixture is not null) return (Fixture.Wsl, Fixture.WslStopped);
        if (!State.Preferences.LookInWsl || Services is not { } services || !services.Offers("wsl") || Connection is not { } connection) return ([], []);
        if (wslScanning is null && (wslScan is null || DateTime.UtcNow - wslScan.Value.At >= WslMaxAge))
            wslScanning = ScanWslAsync(services, connection).ContinueWith(_ => Ui.InvokeAsync(() =>
            {
                wslScanning = null;
                changed();
            }));
        return wslScan is { } scan ? (scan.Sources, scan.Stopped) : ([], []);
    }

    async Task ScanWslAsync(HostServices services, HostConnection connection)
    {
        try
        {
            var running = await services.WslRunningAsync();
            var share = Environment.GetEnvironmentVariable("REFRAME_WSL_SHARE") is { Length: > 0 } s ? s : @"\\wsl.localhost";
            var sources = new List<WslSource>();
            foreach (var distro in running)
            {
                if (await services.WslInfoAsync(distro) is not { Claude: not null, ClaudeHome: { } claudeHome }) continue;
                sources.Add(new WslSource(distro, new HostTranscriptSource(connection, WslSource.WindowsPath(distro, claudeHome, share)), share));
            }
            await Ui.InvokeAsync(() =>
            {
                // The distributions found with claude are remembered (WslDistros 'found'), so a stopped one is offered.
                var known = State.WslDistros ?? [];
                var found = sources.Select(w => w.Distro).Where(d => !known.Any(k => string.Equals(k, d, StringComparison.OrdinalIgnoreCase))).ToList();
                if (found.Count > 0 && Writable)
                {
                    State.WslDistros = [.. known, .. found];
                    Save();
                }
                var stopped = (State.WslDistros ?? []).Where(d => !running.Any(r => string.Equals(r, d, StringComparison.OrdinalIgnoreCase))).ToList();
                wslScan = (DateTime.UtcNow, sources, stopped);
            });
        }
        catch (HostServiceException e)
        {
            await Ui.InvokeAsync(() => SetStatus(Status + $" (wsl: {e.Message})"));
        }
    }

    /// <summary>The list's Start beside a stopped distribution (startWsl): kept running by the host, then looked at again.</summary>
    public async Task StartWslAsync(string distro)
    {
        if (Services is not { } services || !(State.WslDistros ?? []).Contains(distro)) return;
        try
        {
            wslKept = true;
            await services.WslKeepAsync(distro);
        }
        catch (HostServiceException e)
        {
            SetStatus(Status + $" (could not start WSL {distro}: {e.Message})");
        }
        wslScan = null;
        ListChanged();
    }

    /// <summary>Every window lists again.</summary>
    public void ListChanged()
    {
        foreach (var window in windows.Values) window.ListChanged();
    }

    // ---------- attaching ----------

    public async Task AttachAsync()
    {
        SetStatus($"Profile {App.ProfileDir}: attaching...");
        var result = await ShellAttach.AttachAsync(App.ProfileDir, App.TakeOver, start: App.Starter);
        SetStatus($"Profile {App.ProfileDir}: {result.Message}");
        if (result.Connection is null) return;
        var connection = Connection = result.Connection;
        shellLock = result.Lock;
        connection.Closed += replaced => Ui.InvokeAsync(() =>
            SetStatus(replaced ? "Another client took the profile over: detached." : "The pty host connection closed."));
        if (connection.IsObserver) SetStatus(Status + " Terminals are read-only (start with --take-over to type in them).");
        var services = Services = new HostServices(connection);
        FollowSpawns(connection);
        services.StatusChanged += changed => Ui.InvokeAsync(() =>
        {
            HostStatus[changed.SessionId] = changed;
            foreach (var window in windows.Values) window.HostStatusChanged(changed.SessionId);
            if (changed.Notify is "done" or "waiting") Notify(changed.SessionId, changed.Notify);
            RefreshTray();
        });
        foreach (var session in connection.Welcome.Sessions) HostSessions[session.SessionId] = session;
        try
        {
            foreach (var listed in await services.ListSessionsAsync()) HostStatus[listed.SessionId] = listed;
        }
        catch (HostServiceException e)
        {
            SetStatus(Status + $" (sessions: {e.Message})");
        }
        if (Fixture is not null) return;
        try
        {
            var file = Path.Combine(App.ProfileDir, "state.json");
            if (connection.IsObserver) State = (await StateLoader.LoadAsync(services, file)).State;
            else
            {
                Store = new StateStore(file, () => connection.IsOpen && services.Offers("state") ? new HostStateFiles(services) : null,
                    (level, message) => Ui.InvokeAsync(() => SetStatus(Status + $" (state {level}: {message})")));
                var loaded = await Store.LoadAsync();
                State = loaded.State;
                if (loaded.Rejected is not null) SetStatus(Status + $" (state.json set aside: {loaded.Rejected})");
            }
        }
        catch (Exception e) when (e is HostServiceException or IOException)
        {
            SetStatus(Status + $" (state: {e.Message})");
        }
        await ConfigureOverviewAsync(services, connection);
        // The host's sessions that reframe's state does not know (started by a test or another client) are listed too.
        foreach (var session in HostSessions.Values.Where(s => !State.Sessions.ContainsKey(s.SessionId)))
        {
            HostStatus.TryGetValue(session.SessionId, out var known);
            Synthetic.Add(session.SessionId);
            State.Sessions[session.SessionId] = new SessionRecord
            {
                Id = session.SessionId,
                ClaudeSessionId = session.ClaudeSessionId ?? known?.ClaudeSessionId ?? session.SessionId,
                Cwd = known?.Cwd ?? "",
                Name = known?.Name ?? session.SessionId,
                LastActiveAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            };
        }
    }

    /// <summary>overview.configure as the primary client (an observer only reads what the primary configured): the
    /// cache in the profile, Claude Code's home (the one folder reframe reads for every provider).</summary>
    async Task ConfigureOverviewAsync(HostServices services, HostConnection connection)
    {
        if (connection.IsObserver || services.Overview is null) return;
        try
        {
            await services.ConfigureOverviewAsync(Overview.ConfigureParameters(App.ProfileDir, ClaudeRegistry.Home()));
        }
        catch (HostServiceException e)
        {
            SetStatus(Status + $" (overview: {e.Message})");
        }
    }

    /// <summary>app.ts start: attaches, brings the windows back (empty ones are not, one stays) and opens them, shown
    /// unless started by the system (<c>--autostart</c>: in the tray, as reframe), then the tray.</summary>
    public async Task StartAsync(bool hidden)
    {
        await AttachAsync();
        if (State.Windows.Count == 0) State.Windows.Add(WindowState.Empty(Guid.NewGuid().ToString()));
        var restored = LayoutActions.WindowsToRestore(State.Windows);
        if (restored.Count != State.Windows.Count)
        {
            State.Windows.Clear();
            State.Windows.AddRange(restored);
            Save();
        }
        var shown = ShellWindows.WindowsToShow(State.Windows).Select(w => w.Id).ToHashSet();
        foreach (var w in State.Windows.ToList()) Open(w, !hidden && shown.Contains(w.Id));
        tray = new TrayIcon(Tooltip(), TrayMenu, RunTray, () => Show(State.Windows[0].Id));
        toasts = new Toasts(id => Ui.InvokeAsync(() => FocusFromNotification(id)));
        RefreshTray();
        // The tooltip follows the sessions needing the user (refreshTrayTooltip).
        trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        trayTimer.Tick += (_, _) => RefreshTray();
        trayTimer.Start();
        WatchForeignWindows();
    }

    // ---------- windows ----------

    MainWindow Open(WindowState state, bool show)
    {
        var window = new MainWindow(this, state.Id);
        windows[state.Id] = window;
        window.Restore(state, WorkAreas());
        window.Activated += (_, _) => focusedAt[state.Id] = ++focusCount;
        if (show) window.Show();
        return window;
    }

    public void Show(string windowId)
    {
        if (!windows.TryGetValue(windowId, out var window)) return;
        window.BringForward();
    }

    /// <summary>revealWindows: a second launch shows the windows a start shows (closed ones stay in the tray).</summary>
    public void Reveal()
    {
        foreach (var w in ShellWindows.WindowsToShow(State.Windows)) Show(w.Id);
    }

    /// <summary>Into the tray: the window and its sessions stay, out of sight.</summary>
    public void Hide(string windowId) => windows.GetValueOrDefault(windowId)?.Hide();

    /// <summary>The window was shown or hidden (windows.ts 'visibility'): saved; a secondary window hidden while empty is
    /// gone for good.</summary>
    public void VisibilityChanged(string windowId, bool hidden)
    {
        if (Quitting || State.Windows.FirstOrDefault(w => w.Id == windowId) is not { } win) return;
        if (win.Hidden != hidden)
        {
            win.Hidden = hidden;
            SaveSoon();
        }
        if (hidden) Ui.InvokeAsync(() =>
        {
            if (!Quitting && State.Windows.FirstOrDefault(w => w.Id == windowId) is { Hidden: true }) CloseIfEmpty(windowId);
        });
    }

    /// <summary>newWindow: an empty window a little offset from <paramref name="nearId"/>'s (or the first one's).</summary>
    public string NewWindow(string? nearId = null)
    {
        var near = (nearId is not null ? windows.GetValueOrDefault(nearId) : null) ?? windows.GetValueOrDefault(State.Windows[0].Id);
        var win = ShellWindows.NewWindow(Guid.NewGuid().ToString(), near?.CurrentBounds());
        State.Windows.Add(win);
        Save();
        Open(win, true);
        return win.Id;
    }

    /// <summary>The visible window under a screen point (DIPs), if any; where windows overlap, the one focused last.</summary>
    public string? WindowAt(Point point)
    {
        string? front = null;
        foreach (var (id, window) in windows)
        {
            if (!window.IsVisible || window.WindowState == System.Windows.WindowState.Minimized) continue;
            if (window.CurrentBounds() is not { } b || point.X < b.X || point.X >= b.X + b.Width || point.Y < b.Y || point.Y >= b.Y + b.Height) continue;
            if (front is null || focusedAt.GetValueOrDefault(id) > focusedAt.GetValueOrDefault(front)) front = id;
        }
        return front;
    }

    /// <summary>A tile or group dropped outside its window (dnd.ts dragend): over another of the shell's windows it moves
    /// there (to the end), elsewhere it gets a window of its own with its title bar under the pointer (tearOut).</summary>
    public bool DropOutside(string fromId, string key, Point point, Size? size)
    {
        if (!Writable || State.Windows.FirstOrDefault(w => w.Id == fromId) is not { } from) return false;
        var target = WindowAt(point);
        if (target == fromId) return false;
        var (rest, node) = LayoutActions.TakeNode(from, key);
        if (node is null) return false;
        var index = State.Windows.IndexOf(from);
        State.Windows[index] = rest;
        if (target is not null)
        {
            var to = State.Windows.First(w => w.Id == target);
            State.Windows[State.Windows.IndexOf(to)] = LayoutActions.PutNode(to, node, null);
            Save();
            windows[fromId].LayoutChanged();
            windows[target].LayoutChanged();
            CloseIfEmpty(fromId);
            windows[target].BringForward();
            windows[target].FocusTile(Layout.Leaves(node).FirstOrDefault());
            return true;
        }
        var win = ShellWindows.TearOutWindow(Guid.NewGuid().ToString(), node, point.X, point.Y, size?.Width, size?.Height);
        State.Windows.Add(win);
        Save();
        windows[fromId].LayoutChanged();
        Open(win, true);
        CloseIfEmpty(fromId);
        return true;
    }

    /// <summary>Secondary windows go away once nothing is left in them; the first window always stays.</summary>
    void CloseIfEmpty(string windowId)
    {
        if (!ShellWindows.ShouldDiscard(State.Windows, windowId)) return;
        State.Windows.RemoveAll(w => w.Id == windowId);
        Save();
        if (windows.Remove(windowId, out var window)) window.Discard();
        focusedAt.Remove(windowId);
        attention.Remove(windowId);
    }

    /// <summary>closeWindow: with sessions in it, asks (unless an answer was kept): end them, or keep them running with
    /// the window hidden in the tray. An empty window just hides (a secondary one then goes for good).</summary>
    public void CloseRequested(string windowId)
    {
        if (State.Windows.FirstOrDefault(w => w.Id == windowId) is not { } win || askingToClose.Contains(windowId)) return;
        var count = LayoutActions.WindowSessions(win).Count;
        var choice = ShellWindows.ClosePolicy(State.Preferences.CloseWindow, count, systemEnding: false);
        // A read-only shell cannot end sessions: it only hides.
        if (!Writable) choice = "keep";
        if (choice == "ask")
        {
            askingToClose.Add(windowId);
            try
            {
                var (answer, remember) = CloseWindowDialog.Ask(windows[windowId], count);
                choice = answer;
                if (answer is not null && remember)
                {
                    State.Preferences.CloseWindow = answer;
                    Save();
                }
            }
            finally
            {
                askingToClose.Remove(windowId);
            }
        }
        if (choice is null || State.Windows.FirstOrDefault(w => w.Id == windowId) is not { } now) return;
        if (choice == "end")
            foreach (var id in LayoutActions.WindowSessions(now)) EndSession(windowId, id, closeIfEmpty: false);
        Hide(windowId);
    }

    /// <summary>endSession: the session is ended in the host, kept in the list as ended, and leaves its window.</summary>
    public void EndSession(string windowId, string sessionId, bool closeIfEmpty = true)
    {
        if (!Writable || !State.Sessions.TryGetValue(sessionId, out var record)) return;
        if (HostSessions.Remove(sessionId, out var hosted) && Connection is { IsOpen: true } connection)
            _ = connection.DisposeSessionAsync(sessionId, hosted.Instance);
        record.Lifecycle = "ended";
        startErrors.Remove(sessionId);
        var index = State.Windows.FindIndex(w => w.Id == windowId);
        if (index >= 0) State.Windows[index] = LayoutActions.DropSession(State.Windows[index], sessionId);
        Save();
        windows.GetValueOrDefault(windowId)?.LayoutChanged();
        if (closeIfEmpty) CloseIfEmpty(windowId);
    }

    /// <summary>runningSessions: each window's sessions that run.</summary>
    List<(string Window, string Session)> RunningSessions() =>
        [.. State.Windows.SelectMany(w => LayoutActions.WindowSessions(w).Where(Runs).Select(id => (w.Id, id)))];

    /// <summary>The tray's "Terminate all sessions": each ends as its tile's × ends it; reframe stays open. No question
    /// is asked (tray.ts terminateAll), the item is greyed while nothing runs.</summary>
    public void TerminateAll()
    {
        foreach (var (window, session) in RunningSessions()) EndSession(window, session);
        RefreshTray();
    }

    /// <summary>Quit: saves, flushes and detaches; the sessions keep running in the host, the next start takes them over.</summary>
    public async Task QuitAsync()
    {
        if (Quitting) return;
        Quitting = true;
        trayTimer?.Stop();
        StopWatchingForeignWindows();
        if (Store is not null)
        {
            Store.Save(State, Synthetic);
            await Store.FlushAsync(2000);
        }
        foreach (var window in windows.Values.ToList()) window.Close();
        tray?.Dispose();
        tray = null;
        toasts?.Dispose();
        toasts = null;
        // Lets go of the WSL distributions started from the list (WslDistros.release at quit).
        if (wslKept && Services is { } services)
        {
            try
            {
                await services.WslReleaseAsync();
            }
            catch (HostServiceException)
            {
                // The host ends its keepers when it ends.
            }
        }
        if (Connection is not null) await Connection.DisposeAsync();
        shellLock?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Lets the host go (diagnostics), its sessions running.</summary>
    public async Task DetachAsync()
    {
        if (Connection is not null) await Connection.DisposeAsync();
        Connection = null;
        shellLock?.Dispose();
        shellLock = null;
    }

    /// <summary>focusNextWaiting: the next session needing the user, this window's first, then the others'.</summary>
    public void FocusNextWaiting(string windowId, string? after)
    {
        var here = State.Windows.FirstOrDefault(w => w.Id == windowId);
        var ordered = here is null ? State.Windows : [here, .. State.Windows.Where(w => w != here)];
        var order = ordered.SelectMany(LayoutActions.WindowSessions).ToList();
        var waiting = order.Where(id => StatusOf(id) == "waiting").ToHashSet();
        if (ShellWindows.NextWaiting(order, waiting, after) is not { } next) return;
        var owner = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, next));
        if (owner is null || !windows.TryGetValue(owner.Id, out var window)) return;
        window.BringForward();
        window.FocusTile(next);
    }

    // ---------- saving ----------

    /// <summary>Writes the state now (reframe's save()) and lists again in every window.</summary>
    public void Saved()
    {
        Save();
        foreach (var window in windows.Values) window.ListChanged();
        RefreshTray();
    }

    void Save() => Store?.Save(State, Synthetic);

    /// <summary>Bounds and visibility: written once they settle (mutateWindow 'soon').</summary>
    public void SaveSoon() => Store?.SaveSoon(State, Synthetic);

    // ---------- tray ----------

    int Waiting() => State.Windows.SelectMany(LayoutActions.WindowSessions).Count(id => StatusOf(id) == "waiting");

    string Tooltip() => ShellWindows.TrayTooltip(Waiting());

    /// <summary>The tray's tooltip and, per window, the taskbar badge (updateAttention).</summary>
    void RefreshTray()
    {
        tray?.SetTooltip(Tooltip());
        foreach (var (id, count) in Notifications.AttentionCounts(State, StatusOf))
        {
            if (attention.GetValueOrDefault(id) == count || !windows.TryGetValue(id, out var window)) continue;
            attention[id] = count;
            window.Badge(count, count > 0 ? Notifications.WaitingText(count) : "");
        }
    }

    // ---------- notifications ----------

    /// <summary>notify: a session is done or needs the user. A toast unless the user looks at its tile; waiting where
    /// nobody looks, its window's taskbar button flashes too.</summary>
    void Notify(string sessionId, string kind)
    {
        if (Fixture is not null) return;
        var owner = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, sessionId));
        var attentive = owner is not null && windows.TryGetValue(owner.Id, out var shown) && shown.Attentive;
        var disabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Notifications.DisableVariable));
        var decision = Notifications.Decide(State, sessionId, kind, attentive, disabled);
        if (decision.Flash && owner is not null) windows.GetValueOrDefault(owner.Id)?.Flash();
        if (!decision.Toast)
        {
            System.Diagnostics.Trace.WriteLine($"notification skipped: {sessionId} {kind} ({decision.Skipped})");
            return;
        }
        var record = State.Sessions[sessionId];
        var (title, body) = Notifications.ToastText(kind, StateValues.SessionLabel(record.Name, record.ClaudeName, record.Cwd));
        toasts?.Show(sessionId, title, body);
    }

    /// <summary>focusFromNotification: the clicked toast's session comes up in its window; one in no window any more
    /// brings the windows up instead.</summary>
    void FocusFromNotification(string sessionId)
    {
        var owner = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, sessionId));
        if (owner is null || !windows.TryGetValue(owner.Id, out var window))
        {
            Reveal();
            return;
        }
        window.BringForward();
        window.FocusTile(sessionId);
    }

    public List<MenuSpec> TrayMenu() => ShellWindows.TrayMenu(ShellWindows.TrayWindows(State), RunningSessions().Count > 0);

    void RunTray(string action)
    {
        var first = State.Windows[0].Id;
        switch (action)
        {
            case "newSession":
                Show(first);
                windows[first].OpenNewSession();
                break;
            case "newWindow":
                NewWindow();
                break;
            case "about":
                Show(first);
                windows[first].OpenAboutFromTray();
                break;
            case "terminateAll":
                TerminateAll();
                break;
            case "quit":
                _ = QuitAsync();
                break;
            default:
                if (action.StartsWith("window:", StringComparison.Ordinal) && windows.TryGetValue(action["window:".Length..], out var window))
                {
                    if (window.IsVisible) window.Hide();
                    else window.BringForward();
                }
                break;
        }
    }

    // ---------- displays ----------

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    /// <summary>The displays' work areas in DIPs (the system's scale; WPF's window coordinates).</summary>
    public static List<Box> WorkAreas()
    {
        var scale = GetDpiForSystem() / 96.0;
        var areas = new List<Box>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.rcWork;
                areas.Add(new Box(w.Left / scale, w.Top / scale, (w.Right - w.Left) / scale, (w.Bottom - w.Top) / scale));
            }
            return true;
        }, IntPtr.Zero);
        return areas;
    }
}
