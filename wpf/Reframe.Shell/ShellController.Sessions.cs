using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Session control as src/main/app.ts and sessions.ts do it: New session (createSession), Resume, Take over and
/// Start fresh (restartSession, startOrTakeOver), opening an ended session from the list, and the host's spawn events.
/// A start asks the host for its spawn plan (<c>launch.spawnPlan</c>: claude, arguments, environment, hook settings),
/// writes the settings file and sends <c>spawn</c>; what keeps it from starting (folder gone, no claude, no transcript,
/// running in another terminal) is the error its tile shows (<see cref="ErrorOf"/>).
/// </summary>
sealed partial class ShellController
{
    readonly Dictionary<string, string> startErrors = [];
    /// <summary>Sessions on their way to a start, so a second click does not start one twice (startsPending).</summary>
    readonly HashSet<string> startsPending = [];
    int instances;

    /// <summary>Why the session's last start failed (tile.error.*), or null.</summary>
    public string? ErrorOf(string sessionId) => startErrors.GetValueOrDefault(sessionId);

    /// <summary>A capture's error tile: the error a start would end in.</summary>
    public void SetError(string sessionId, string? error)
    {
        if (error is null) startErrors.Remove(sessionId);
        else startErrors[sessionId] = error;
    }

    static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>getDialogDefaults: each provider's workspace root, the recent folders that are there, the preferences.</summary>
    public (Dictionary<string, string> Roots, List<string> Recent) DialogDefaults() =>
        (NewSession.WorkspaceRoots(Documents),
         State.RecentFolders.Where(f => Fixture is not null || WslPaths.Parse(f, WslShare) is not null || Directory.Exists(f)).ToList());

    static string WslShare => Environment.GetEnvironmentVariable("REFRAME_WSL_SHARE") is { Length: > 0 } s ? s : @"\\wsl.localhost";

    void FollowSpawns(HostConnection connection)
    {
        instances = connection.Welcome.Sessions.Select(s => s.Instance).DefaultIfEmpty(0).Max();
        connection.HostEvent += message => Ui.InvokeAsync(() => HostEventReceived(message));
    }

    /// <summary>spawned, spawn-failed and exit of the sessions this connection started (or found running).</summary>
    void HostEventReceived(JsonObject message)
    {
        var type = (string?)message["type"];
        if ((string?)message["sessionId"] is not { } id || (int?)message["instance"] is not { } instance) return;
        if (!HostSessions.TryGetValue(id, out var hosted) || hosted.Instance != instance) return;
        switch (type)
        {
            case "spawned":
                HostSessions[id] = hosted with { Pid = (int?)message["pid"] ?? 0 };
                if (State.Sessions.TryGetValue(id, out var record) && (int?)message["pid"] is { } pid) record.Pid = pid;
                return;
            case "spawn-failed":
                startErrors[id] = "spawnFailed";
                break;
            case "exit":
                break;
            default:
                return;
        }
        // The tile shows the card over the ended session (its terminal stays readable until then).
        HostSessions.Remove(id);
        HostStatus.Remove(id);
        SessionChanged(id);
    }

    /// <summary>Every window showing the session builds its tile anew (a terminal or the card).</summary>
    void SessionChanged(string sessionId)
    {
        foreach (var window in windows.Values) window.SessionChanged(sessionId);
        ListChanged();
        RefreshTray();
    }

    /// <summary>createSession: checks the folder (offering to create it) and claude, then the session is added to
    /// the window and started. The error the dialog shows (and the WSL distribution without claude), or null.</summary>
    public async Task<(string? Error, string? Distro)> CreateSessionAsync(string windowId, NewSessionRequest request, Func<string, bool> offerToCreate)
    {
        var cwd = request.Cwd.Trim();
        var wsl = WslPaths.Parse(cwd, WslShare);
        if (!FolderThere(cwd) && (Fixture is not null || !Path.IsPathFullyQualified(cwd) || !offerToCreate(cwd) || !CreateFolder(cwd)))
            return ("folderMissing", null);
        if (Fixture is not null || !Writable) return (null, null);
        var record = NewSession.CreateRecord(request, State.Preferences, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Now());
        SpawnPlans.Plan plan;
        try
        {
            plan = await PlanAsync(record, "new", null);
        }
        catch (Exception e) when (e is HostServiceException or FormatException or IOException)
        {
            SetStatus(Status + $" (new session: {e.Message})");
            return ("claudeNotFound", wsl?.Distro);
        }
        if (!plan.Ok) return (plan.Error == "folderMissing" ? "folderMissing" : "claudeNotFound", plan.Error == "claudeNotFound" ? wsl?.Distro : null);
        NewSession.Remember(State, record);
        if (windows.TryGetValue(windowId, out var window)) window.PlaceSession(record.Id, null);
        else Save();
        await SpawnAsync(record, plan);
        return (null, null);
    }

    static bool FolderThere(string cwd) => cwd != "" && Directory.Exists(cwd);

    static bool CreateFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log($"could not create session folder {folder}: {e.Message}");
            return false;
        }
    }

    /// <summary>restartSession: Resume (or Take over) and Start fresh on the tile's card.</summary>
    public void RestartSession(string windowId, string sessionId, bool fresh)
    {
        if (!Writable || !State.Sessions.TryGetValue(sessionId, out var record) || HistoryBusy.Contains(record.ClaudeSessionId) || Runs(sessionId)) return;
        NewSession.Restart(record, fresh, Guid.NewGuid().ToString(), Now());
        Saved();
        _ = StartOrTakeOverAsync(sessionId, fresh ? "new" : "resume", windowId);
    }

    /// <summary>openSession for one of reframe's sessions that does not run: open again and resumed.</summary>
    public void Resume(string windowId, string sessionId)
    {
        if (!Writable || !State.Sessions.TryGetValue(sessionId, out var record) || Runs(sessionId)) return;
        record.Lifecycle = "open";
        _ = StartOrTakeOverAsync(sessionId, "resume", windowId);
    }

    /// <summary>startOrTakeOver: starts the session, unless its conversation runs in another terminal; then it is
    /// taken over after asking in the window (no answer: its tile says so and offers Take over).</summary>
    async Task StartOrTakeOverAsync(string sessionId, string mode, string? windowId)
    {
        if (!State.Sessions.TryGetValue(sessionId, out var record) || HistoryBusy.Contains(record.ClaudeSessionId)) return;
        if (State.HiddenThreads.Any(h => h.ClaudeSessionId == record.ClaudeSessionId)) return;
        if (!startsPending.Add(sessionId)) return;
        try
        {
            startErrors.Remove(sessionId);
            // A capture starts nothing: the error a start would end in is what it shows.
            if (Fixture is not null)
            {
                if (!FolderThere(record.Cwd)) startErrors[sessionId] = "folderMissing";
                SessionChanged(sessionId);
                return;
            }
            if (Connection is not { IsOpen: true } || Services?.Offers("launch") != true || Connection.Welcome.Hooks is null)
            {
                startErrors[sessionId] = "hostCrashed";
                SessionChanged(sessionId);
                return;
            }
            if (mode == "resume" && WslPaths.Parse(record.Cwd, WslShare) is null && await RunningElsewhereAsync(record) is { } outside)
            {
                var asked = windowId is not null && windows.TryGetValue(windowId, out var window) && window.ConfirmTakeover(Label(record));
                Log($"session runs elsewhere already: {sessionId} pid {outside.Pid} asked {asked}");
                if (!asked || takeoverStopper() is not { } stopper || await stopper.StopExternalClaudeAsync(outside.Pid) == "failed")
                {
                    if (asked) MessageBox.Show(windows[windowId!], Strings.T("takeover.failed"), "Reframe", MessageBoxButton.OK, MessageBoxImage.Error);
                    startErrors[sessionId] = "runningElsewhere";
                    SessionChanged(sessionId);
                    return;
                }
                if (outside.Cwd is { Length: > 0 } cwd) record.Cwd = cwd;
                if (await Task.Run(() => ClaudeRegistry.TranscriptModifiedAt(ClaudeRegistry.Home(), record.ClaudeSessionId)) is { } usedThere && usedThere > record.LastActiveAt)
                    record.LastActiveAt = usedThere;
                Save();
            }
            // Its tile was closed meanwhile, or it started.
            if (!State.Windows.Any(w => LayoutActions.WindowHasSession(w, sessionId)) || Runs(sessionId)) return;
            var plan = await PlanAsync(record, mode, null);
            if (!plan.Ok)
            {
                startErrors[sessionId] = plan.Error ?? "spawnFailed";
                Log($"session could not start: {sessionId} {plan.Error} {string.Join("; ", plan.Warnings)}");
                SessionChanged(sessionId);
                return;
            }
            await SpawnAsync(record, plan);
        }
        catch (Exception e) when (e is HostServiceException or FormatException or IOException or UnauthorizedAccessException)
        {
            Log($"session could not start: {sessionId} {e.Message}");
            startErrors[sessionId] = "spawnFailed";
            SessionChanged(sessionId);
        }
        finally
        {
            startsPending.Remove(sessionId);
        }
    }

    static string Label(SessionRecord record) => StateValues.SessionLabel(record.Name, record.ClaudeName, record.Cwd);

    Takeover? takeoverStopper() => takeover ??= Store is { ReadOnly: false }
        ? new Takeover(consoles, ClaudeRegistry.Alive, KillTreeAsync, ms => Task.Delay(ms), Log)
        : null;

    /// <summary>The claude running the session's conversation outside reframe (not one of the host's own), if any.</summary>
    async Task<RunningClaude?> RunningElsewhereAsync(SessionRecord record)
    {
        var own = HostSessions.Values.Select(s => s.Pid).Append((int)(record.Pid ?? 0)).ToHashSet();
        var home = ClaudeRegistry.Home();
        return await Task.Run(() => ClaudeRegistry.Running(home).FirstOrDefault(e => e.SessionId == record.ClaudeSessionId && !own.Contains(e.Pid)));
    }

    /// <summary>The host's spawn plan for the session (in WSL folders with the distribution's claude).</summary>
    async Task<SpawnPlans.Plan> PlanAsync(SessionRecord record, string mode, string? freshClaudeSessionId)
    {
        if (Connection is not { IsOpen: true } connection || Services is not { } services || connection.Welcome.Hooks is not { } hooks)
            throw new HostServiceException("unavailable", "no pty host attached");
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string key && e.Value is string value) env[key] = value;
        JsonObject? wsl = null;
        if (WslPaths.Parse(record.Cwd, WslShare) is { } where && services.Offers("wsl"))
        {
            var info = await connection.CallAsync("wsl", "info", new JsonObject { ["distro"] = where.Distro });
            var wslExe = env.GetValueOrDefault("REFRAME_WSL_EXE");
            var node = env.GetValueOrDefault("REFRAME_NODE");
            var command = wslExe is { Length: > 0 } && System.Text.RegularExpressions.Regex.IsMatch(wslExe, @"\.(c|m)?js$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && node is { Length: > 0 }
                ? new JsonObject { ["file"] = node, ["prefixArgs"] = new JsonArray(wslExe), ["env"] = new JsonObject() }
                : new JsonObject
                {
                    ["file"] = wslExe is { Length: > 0 } ? wslExe : Path.Combine(env.GetValueOrDefault("SystemRoot") ?? @"C:\Windows", "System32", "wsl.exe"),
                    ["prefixArgs"] = new JsonArray(), ["env"] = new JsonObject(),
                };
            wsl = new JsonObject
            {
                ["share"] = WslShare, ["command"] = command,
                ["info"] = info is JsonObject o && (string?)o["claude"] is { Length: > 0 } ? o.DeepClone() : null,
                ["homeDir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
        }
        var claudeHome = ClaudeRegistry.Home();
        // An Anthropic session's status line is captured when the user has none of their own (quota).
        var capture = false;
        if (record.Launch.Provider == "anthropic" && services.Offers("quota") && string.IsNullOrEmpty(env.GetValueOrDefault("REFRAME_E2E")))
        {
            try
            {
                capture = (bool?)await connection.CallAsync("quota", "statusLine", new JsonObject { ["claudeHome"] = claudeHome }) == false;
            }
            catch (HostServiceException) { }
        }
        var input = SpawnPlans.Input(record, mode, freshClaudeSessionId, env, claudeHome, Path.Combine(App.ProfileDir, "hooks"), hooks,
            State.Preferences.ClaudeBin, env.GetValueOrDefault("REFRAME_CLIENT_ADOPTION"), capture, wsl);
        var plan = SpawnPlans.Parse(await connection.CallAsync("launch", "spawnPlan", input));
        foreach (var warning in plan.Warnings) Log($"spawn plan: {record.Id}: {warning}");
        return plan;
    }

    /// <summary>Writes the plan's settings file and sends the spawn; the session runs (starting) from now on.</summary>
    async Task SpawnAsync(SessionRecord record, SpawnPlans.Plan plan)
    {
        if (Connection is not { IsOpen: true } connection) return;
        if (plan.SettingsFile is { } file && plan.SettingsContent is { } content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, content);
        }
        var instance = ++instances;
        startErrors.Remove(record.Id);
        HostSessions[record.Id] = new HostSession(record.Id, instance, 0, null, (string?)plan.Meta["claudeHome"], record.ClaudeSessionId);
        HostStatus.Remove(record.Id);
        Synthetic.Remove(record.Id);
        Log($"session start: {record.Id} {plan.Mode} in {plan.Cwd}");
        await connection.SpawnAsync(SpawnPlans.Spawn(record.Id, instance, plan));
        SessionChanged(record.Id);
    }
}
