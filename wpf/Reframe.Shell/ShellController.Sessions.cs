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
    /// <summary>A fixture capture's window, which is not one of the profile's windows.</summary>
    internal MainWindow? CaptureWindow { get; set; }

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
        instances = connection.Welcome.Sessions.Select(s => s.Instance).Append(instances).Max();
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
        LeaveOlderHost(id);
        HostStatus.Remove(id);
        SessionChanged(id);
    }

    /// <summary>Every window showing the session builds its tile anew (a terminal or the card).</summary>
    void SessionChanged(string sessionId)
    {
        foreach (var window in windows.Values) window.SessionChanged(sessionId);
        CaptureWindow?.SessionChanged(sessionId);
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
            Elsewhere? outside = null;
            try
            {
                if (mode == "resume") outside = await RunningElsewhereAsync(record);
            }
            catch (Exception e) when (e is HostServiceException or IOException or UnauthorizedAccessException)
            {
                Log($"could not tell whether a session runs elsewhere, starting it: {e.Message}");
            }
            if (outside is { } found)
            {
                var asked = windowId is not null && windows.TryGetValue(windowId, out var window) && window.ConfirmTakeover(Label(record));
                Log($"session runs elsewhere already: {sessionId} pid {found.Entry.Pid} asked {asked}");
                if (!asked || !await StopOutsideAsync(found))
                {
                    if (asked) MessageBox.Show(windows[windowId!], Strings.T("takeover.failed"), "Reframe", MessageBoxButton.OK, MessageBoxImage.Error);
                    startErrors[sessionId] = "runningElsewhere";
                    SessionChanged(sessionId);
                    return;
                }
                // Resumed in another folder, the conversation went on there; so it does here. It was used there, too.
                if (found.Entry.Cwd is { Length: > 0 } cwd) record.Cwd = found.WindowsPath(cwd);
                if (await WithTimeout(Task.Run(() => ClaudeRegistry.TranscriptModifiedAt(found.Home, record.ClaudeSessionId)), null) is { } usedThere
                    && usedThere > record.LastActiveAt)
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

    /// <summary>Conversations being adopted from the list, so two clicks make one record (adopting).</summary>
    readonly HashSet<string> adopting = [];

    /// <summary>recordFor: the session to open for a list entry that is no session of reframe's yet: the one that
    /// already has its conversation, else a new record for a past chat (found) or for a claude running in another
    /// terminal, ended there first after asking (adopt). Null when there is nothing (any more) to open.</summary>
    public async Task<string?> RecordForAsync(string windowId, ListItemRef item)
    {
        if (Fixture is not null || !Writable || item.ClaudeSessionId is not { } claudeId) return null;
        // A hidden thread is never opened, adopted, resumed or taken over.
        if (HistoryBusy.Contains(claudeId) || State.HiddenThreads.Any(h => h.ClaudeSessionId == claudeId)) return null;
        string? Known() => State.Sessions.Values.FirstOrDefault(r => r.ClaudeSessionId == claudeId)?.Id;
        if (Known() is { } known) return known;
        if (!adopting.Add(claudeId)) return null;
        try
        {
            var record = item.Kind switch
            {
                ListItemKind.Found => await FoundRecordAsync(item, claudeId),
                ListItemKind.Running => await TakeOverRunningAsync(windowId, item, claudeId),
                _ => null,
            };
            if (record is null) return null;
            if (Known() is { } meanwhile) return meanwhile;
            State.Sessions[record.Id] = record;
            Log($"session adopted from the list: {record.Id} ({item.Kind})");
            return record.Id;
        }
        catch (Exception e) when (e is HostServiceException or IOException or UnauthorizedAccessException)
        {
            Log($"could not open the list entry {claudeId}: {e.Message}");
            return null;
        }
        finally
        {
            adopting.Remove(claudeId);
        }
    }

    /// <summary>A past chat's record, in the folder its transcript tells (in WSL: under the share).</summary>
    async Task<SessionRecord?> FoundRecordAsync(ListItemRef item, string claudeId)
    {
        if (Connection is not { } connection) return null;
        TranscriptItem? info;
        Func<string, string> windowsPath = folder => folder;
        if (string.IsNullOrEmpty(item.Distro)) info = await new HostTranscriptSource(connection, ClaudeRegistry.Home()).FindAsync(claudeId);
        else if (wslScan?.Sources.FirstOrDefault(s => string.Equals(s.Distro, item.Distro, StringComparison.OrdinalIgnoreCase)) is { } source)
        {
            info = await WithTimeout(source.Transcripts.FindAsync(claudeId), null);
            windowsPath = source.WindowsPath;
        }
        else return null;
        if (string.IsNullOrEmpty(info?.Cwd))
        {
            Log($"found session has no folder: {claudeId}");
            return null;
        }
        return Takeover.Adopted(claudeId, windowsPath(info.Cwd), null, info.ModifiedAt > 0 ? info.ModifiedAt : null, Now());
    }

    /// <summary>A claude running in another terminal (Windows): asked first, ended there, then adopted; a name the user
    /// gave it survives.</summary>
    async Task<SessionRecord?> TakeOverRunningAsync(string windowId, ListItemRef item, string claudeId)
    {
        if (!string.IsNullOrEmpty(item.Distro) || !windows.TryGetValue(windowId, out var window)) return null;
        var home = ClaudeRegistry.Home();
        var entry = await Task.Run(() => ClaudeRegistry.Entry(home, item.Pid));
        if (entry is not { Cwd: { Length: > 0 } cwd } || entry.SessionId != claudeId || !ClaudeRegistry.Alive(entry.Pid))
        {
            Log($"running session is gone, nothing to take over: pid {item.Pid}");
            return null;
        }
        if (!window.ConfirmTakeover(entry.Name ?? Path.GetFileName(cwd.TrimEnd('\\', '/')))) return null;
        Log($"taking over session from another terminal: pid {item.Pid}");
        if (!await StopOutsideAsync(new Elsewhere(entry, null, home)))
        {
            MessageBox.Show(window, Strings.T("takeover.failed"), "Reframe", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        var modified = await Task.Run(() => ClaudeRegistry.TranscriptModifiedAt(home, claudeId));
        return Takeover.Adopted(claudeId, cwd, entry.NameSource == "user" ? entry.Name : null, modified, Now());
    }

    static string Label(SessionRecord record) => StateValues.SessionLabel(record.Name, record.ClaudeName, record.Cwd);

    Takeover? takeoverStopper() => takeover ??= Store is { ReadOnly: false }
        ? new Takeover(consoles, ClaudeRegistry.Alive, KillTreeAsync, ms => Task.Delay(ms), Log)
        : null;

    /// <summary>A claude running a session's conversation outside reframe: its registry entry, the WSL distribution it
    /// runs in (null: Windows) and Claude Code's home there, as Windows reaches it.</summary>
    sealed record Elsewhere(RunningClaude Entry, string? Distro, string Home)
    {
        public string WindowsPath(string folder) => Distro is null ? folder : WslSource.WindowsPath(Distro, folder, WslShare);
    }

    /// <summary>How long a WSL distribution's files over the share may take to answer (SHARE_TIMEOUT_MS).</summary>
    static readonly TimeSpan ShareTimeout = TimeSpan.FromSeconds(3);

    static async Task<T> WithTimeout<T>(Task<T> task, T fallback) =>
        await Task.WhenAny(task, Task.Delay(ShareTimeout)) == task ? await task : fallback;

    /// <summary>runningElsewhere: the claude running the session's conversation outside reframe (not one of the host's
    /// own, nor the session's own last process), if any; in WSL only if its distribution runs (asking one that does not
    /// would start it), its registry read over the share and its process asked of the distribution.</summary>
    async Task<Elsewhere?> RunningElsewhereAsync(SessionRecord record)
    {
        if (WslPaths.Parse(record.Cwd, WslShare) is not { } where)
        {
            var own = HostSessions.Values.Select(s => s.Pid).Append((int)(record.Pid ?? 0)).ToHashSet();
            var home = ClaudeRegistry.Home();
            return await Task.Run(() => ClaudeRegistry.Running(home).FirstOrDefault(e => e.SessionId == record.ClaudeSessionId && !own.Contains(e.Pid)))
                is { } entry ? new Elsewhere(entry, null, home) : null;
        }
        if (Services is not { } services || !services.Offers("wsl")) return null;
        var running = await services.WslRunningAsync();
        if (!running.Any(d => string.Equals(d, where.Distro, StringComparison.OrdinalIgnoreCase))) return null;
        if (await services.WslInfoAsync(where.Distro) is not { Claude: not null, ClaudeHome: { } linuxHome }) return null;
        var wslHome = WslSource.WindowsPath(where.Distro, linuxHome, WslShare);
        var last = (int)(record.LinuxPid ?? 0);
        var entries = await WithTimeout(Task.Run(() => ClaudeRegistry.Entries(wslHome)), new List<RunningClaude>());
        foreach (var entry in entries.Where(e => e.SessionId == record.ClaudeSessionId && e.Pid != last))
            if (await WithTimeout(services.WslIsClaudeAsync(where.Distro, entry.Pid), false)) return new Elsewhere(entry, where.Distro, wslHome);
        return null;
    }

    /// <summary>stopOutside: ends the claude the way its user would (in WSL as a signal, as from Ctrl+C); whether it went.</summary>
    async Task<bool> StopOutsideAsync(Elsewhere outside)
    {
        var result = outside.Distro is { } distro
            ? Services is { } services ? await services.WslStopClaudeAsync(distro, outside.Entry.Pid) : "failed"
            : takeoverStopper() is { } stopper ? await stopper.StopExternalClaudeAsync(outside.Entry.Pid) : "failed";
        Log($"other terminal stopped: {outside.Entry.Pid} {result}");
        return result != "failed";
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
        LeaveOlderHost(record.Id);
        HostSessions[record.Id] = new HostSession(record.Id, instance, 0, null, (string?)plan.Meta["claudeHome"], record.ClaudeSessionId);
        HostStatus.Remove(record.Id);
        Synthetic.Remove(record.Id);
        Log($"session start: {record.Id} {plan.Mode} in {plan.Cwd}");
        await connection.SpawnAsync(SpawnPlans.Spawn(record.Id, instance, plan));
        SessionChanged(record.Id);
    }
}
