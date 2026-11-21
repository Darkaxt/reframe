using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Scheduled tasks (ScheduledTasks): the threads save them through their thread tools into the profile's
/// <c>tasks</c> folder; here they are watched, and each one due is run: its prompt typed into its thread once the
/// thread is idle (an ended thread is resumed first, a busy one waited for), or a new thread started with it. A run
/// missed while reframe was closed runs once at the next start. With Settings' "Wake Reframe for scheduled tasks",
/// quitting leaves a Task Scheduler task that starts reframe in the tray when the next one is due.
/// </summary>
sealed partial class ShellController
{
    /// <summary>A run taken and not delivered yet.</summary>
    sealed class PendingRun(ScheduledTask task, DateTimeOffset since)
    {
        public ScheduledTask Task { get; } = task;
        public DateTimeOffset Since { get; } = since;
        public DateTimeOffset? ResumedAt { get; set; }
        public bool Busy { get; set; }
        /// <summary>The text to type instead of the task's prompt: a wake-up from another thread (wake_thread), which
        /// is not a saved task (nothing of it is written, only a failure is told).</summary>
        public string? Wake { get; init; }
    }

    static readonly TimeSpan TaskWaitLimit = TimeSpan.FromHours(12);
    static readonly TimeSpan ResumeLimit = TimeSpan.FromMinutes(3);

    List<ScheduledTask> tasks = [];
    readonly Dictionary<string, PendingRun> pendingRuns = [];
    DispatcherTimer? taskTimer;
    DispatcherTimer? taskReload;
    FileSystemWatcher? taskWatcher;

    public static string TasksDir => Path.Combine(App.ProfileDir, "tasks");
    public IReadOnlyList<ScheduledTask> ScheduledTasksList => tasks;
    public bool IsRunPending(string taskId) => pendingRuns.ContainsKey(taskId);

    /// <summary>A task's run that was typed into a thread, until the thread is idle again (seen busy since), or for two
    /// minutes when it never showed busy.</summary>
    sealed class TaskActivity(DateTimeOffset at)
    {
        public DateTimeOffset At { get; } = at;
        public bool SeenBusy { get; set; }
    }

    readonly Dictionary<string, TaskActivity> taskActive = [];

    /// <summary>Threads a task or a wake-up resumed, and when: while their start is awaited their tile does not take the
    /// keyboard focus when the terminal appears (the run must not pull the focus from what the user is typing in).</summary>
    readonly Dictionary<string, DateTimeOffset> quietStarts = [];

    public bool StartsQuietly(string sessionId) =>
        quietStarts.TryGetValue(sessionId, out var at) && DateTimeOffset.Now - at < ResumeLimit;

    /// <summary>Whether a scheduled task is running in the thread (its run taken and being delivered, or delivered and the
    /// thread still at it): its clock pulses. A wake-up from another thread is not a task.</summary>
    public bool TaskRunningOn(string sessionId) =>
        taskActive.ContainsKey(sessionId) || pendingRuns.Values.Any(r => r.Wake is null && r.Task.TargetThreadId == sessionId);

    /// <summary>The thread's status changed: a task's run ends when it is idle again after having been busy.</summary>
    void TaskActivityChanged(string sessionId)
    {
        if (!taskActive.TryGetValue(sessionId, out var activity)) return;
        var status = StatusOf(sessionId);
        if (status is "busy" or "background") activity.SeenBusy = true;
        else if (activity.SeenBusy && status is "idle" or "ended" or "error" or "done") EndTaskActivity(sessionId);
    }

    void EndTaskActivity(string sessionId)
    {
        if (taskActive.Remove(sessionId)) TasksChanged?.Invoke();
    }

    void ExpireTaskActivity()
    {
        var now = DateTimeOffset.Now;
        foreach (var (id, activity) in taskActive.ToList())
            if (now - activity.At > (activity.SeenBusy ? TaskWaitLimit : TimeSpan.FromMinutes(2)) || !Runs(id)) EndTaskActivity(id);
    }
    public event Action? TasksChanged;

    static void TaskLog(string message) => Trace.WriteLine($"tasks: {message}");

    void StartTasks()
    {
        if (Fixture is not null || Store is not { ReadOnly: false }) return;
        // The wake-up task is only there while reframe is closed.
        _ = System.Threading.Tasks.Task.Run(RemoveWakeTask);
        try
        {
            Directory.CreateDirectory(TasksDir);
            taskWatcher = new FileSystemWatcher(TasksDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                Filter = ScheduledTasks.FileName,
            };
            FileSystemEventHandler changed = (_, _) => Ui.InvokeAsync(() => { taskReload?.Stop(); taskReload?.Start(); });
            taskWatcher.Changed += changed;
            taskWatcher.Created += changed;
            taskWatcher.Deleted += changed;
            taskWatcher.Renamed += (_, _) => Ui.InvokeAsync(() => { taskReload?.Stop(); taskReload?.Start(); });
            taskWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            TaskLog($"cannot watch {TasksDir}: {e.Message}");
        }
        taskReload = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        taskReload.Tick += (_, _) =>
        {
            taskReload.Stop();
            ReloadTasks();
        };
        ReloadTasks();
        // The first look comes after the sessions resumed at start had a moment to come up.
        taskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        taskTimer.Tick += (_, _) =>
        {
            TickTasks();
            ExpireTaskActivity();
            CloseEnded();
        };
        taskTimer.Start();
    }

    public void ReloadTasks()
    {
        tasks = ScheduledTasks.Load(TasksDir);
        TasksChanged?.Invoke();
    }

    /// <summary>The scheduler's tick; whatever it trips on is logged, never allowed to take the shell (and every
    /// terminal in it) down.</summary>
    void TickTasks()
    {
        try
        {
            TickTasksOnce();
        }
        catch (Exception e)
        {
            TaskLog($"tick failed: {e.GetType().Name}: {e.Message}");
        }
    }

    void TickTasksOnce()
    {
        if (Quitting || Connection is not { IsOpen: true, IsObserver: false }) return;
        var now = DateTimeOffset.Now;
        foreach (var task in tasks.Where(t => t.Active && !pendingRuns.ContainsKey(t.Id)))
        {
            var runs = ScheduledTasks.ReadRuns(TasksDir, task.Id);
            if (ScheduledTasks.NextDue(task, runs) is not { } due || due > now) continue;
            Take(task, runs, now, $"due {due:yyyy-MM-dd HH:mm:ss}");
        }
        foreach (var run in pendingRuns.Values.Where(r => !r.Busy).ToList()) _ = AdvanceAsync(run);
        SyncNames();
    }

    readonly HashSet<string> renaming = [];

    /// <summary>When each session's Claude exited while the shell ran (ms since the epoch); a session found ended
    /// counts from its last use.</summary>
    readonly Dictionary<string, long> endedAt = [];

    /// <summary>"Close ended threads after": a thread whose Claude ended that long ago, still in a window, is ended as
    /// its tile's × ends it (the tile goes, the thread stays in the list and can be resumed). Never a running thread, one
    /// starting, one a task or a wake-up is about to resume, or one whose start failed (its tile says why).</summary>
    void CloseEnded()
    {
        var hours = State.Preferences.CloseEndedAfterHours;
        if (hours <= 0 || Quitting || Fixture is not null || !Writable) return;
        var limit = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
        var waiting = pendingRuns.Values.Select(r => r.Task.TargetThreadId).ToHashSet();
        foreach (var window in State.Windows.ToList())
            foreach (var id in LayoutActions.WindowSessions(window).ToList())
            {
                if (Runs(id) || startsPending.Contains(id) || startErrors.ContainsKey(id) || waiting.Contains(id)) continue;
                if (!State.Sessions.TryGetValue(id, out var record) || record.Lifecycle != "open") continue;
                var since = endedAt.TryGetValue(id, out var at) ? at : (long)record.LastActiveAt;
                if (since <= 0 || since > limit) continue;
                Trace.WriteLine($"closing: {id} ended more than {hours} h ago");
                EndSession(window.Id, id, closeIfEmpty: false);
            }
    }

    /// <summary>Tells each running, idle Claude Code renamed here its new name with /rename (a busy one at a later
    /// tick; one not running gets it with --name at its next start).</summary>
    public void SyncNames()
    {
        if (Quitting || Connection is not { IsOpen: true, IsObserver: false }) return;
        foreach (var record in State.Sessions.Values.ToList())
            if (ShellActions.NameToSync(record) is { } name && !renaming.Contains(record.Id) && Runs(record.Id) && StatusOf(record.Id) == "idle")
                _ = RenameRunningAsync(record, name);
    }

    async Task RenameRunningAsync(SessionRecord record, string name)
    {
        renaming.Add(record.Id);
        try
        {
            if (!await TypeIntoAsync(record.Id, "/rename " + name)) return;
            if (record.Name == name) ShellActions.NameSynced(record, name);
            SaveSoon();
            Trace.WriteLine($"rename: {record.Id} told its name");
        }
        finally
        {
            renaming.Remove(record.Id);
        }
    }

    void Take(ScheduledTask task, TaskRuns runs, DateTimeOffset now, string why)
    {
        ScheduledTasks.WriteRuns(TasksDir, task.Id, runs with { LastRunAt = now.ToUnixTimeMilliseconds(), Runs = runs.Runs + 1 });
        pendingRuns[task.Id] = new PendingRun(task, now);
        TaskLog($"{task.Id}: run taken ({why})");
        TasksChanged?.Invoke();
    }

    /// <summary>A thread asked to wake another by name (its <c>wake_thread</c> tool): the message is delivered like a
    /// task's run (waiting for idle, resuming an ended thread); an unknown or ambiguous name is told to the asker.</summary>
    async Task WakeThreadAsync(JsonObject hook)
    {
        var askerId = (string?)hook["sessionId"];
        string? Text(string key) => hook["payload"] is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
        if (askerId is null || !State.Sessions.TryGetValue(askerId, out var asker) || Text("wake") is not { } name || Text("message") is not { } message) return;
        var matches = ThreadsNamed(askerId, name);
        var askerName = StateValues.SessionLabel(asker.Name, asker.ClaudeName, asker.Cwd);
        if (matches.Count != 1)
        {
            var why = matches.Count == 0 ? $"no thread is named \"{name}\"" : $"more than one thread is named \"{name}\"";
            TaskLog($"wake: {askerId}: {why}");
            if (Runs(askerId)) await TypeIntoAsync(askerId, $"[Reframe: could not wake: {why}]");
            return;
        }
        var target = matches[0];
        var by = $"[woken by the Reframe thread \"{askerName}\"; answer it with SendMessage to \"{askerName}\"]";
        var task = new ScheduledTask($"wake-{Guid.NewGuid():N}", "heartbeat", name, message, "ACTIVE", "", target.Id, "important", 0, 0, new Dictionary<string, object>());
        var run = new PendingRun(task, DateTimeOffset.Now) { Wake = $"{by}\n{message}" };
        pendingRuns[task.Id] = run;
        TaskLog($"wake: {askerId} wakes {target.Id}");
        await AdvanceAsync(run);
    }

    /// <summary>Run now (the Tasks dialog): taken at once, the schedule going on from now.</summary>
    public void RunTaskNow(ScheduledTask task)
    {
        if (pendingRuns.ContainsKey(task.Id)) return;
        Take(task, ScheduledTasks.ReadRuns(TasksDir, task.Id), DateTimeOffset.Now, "run now");
        if (pendingRuns.TryGetValue(task.Id, out var run)) _ = AdvanceAsync(run);
    }

    async Task AdvanceAsync(PendingRun run)
    {
        run.Busy = true;
        try
        {
            var task = run.Task;
            if (!State.Sessions.TryGetValue(task.TargetThreadId, out var record))
            {
                Finish(run, false, "its thread is not in Reframe any more");
                return;
            }
            if (task.NewChat)
            {
                var window = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, record.Id)) ?? State.Windows.FirstOrDefault();
                if (window is null)
                {
                    Finish(run, false, "no window to open the new thread in");
                    return;
                }
                var defaults = NewSession.DefaultsFor(State.Preferences, record.Launch.Provider ?? "chatgpt");
                // The task's thread's own choices; what it has from the defaults (or not at all) is the defaults' now, and follows them.
                var follows = NewSession.FollowFields.Where(f => record.Launch.FollowsDefaults?.Contains(f) == true || f switch
                {
                    "model" => record.Launch.Model is null,
                    "effort" => record.Launch.Effort is null,
                    _ => record.Launch.PermissionMode is null,
                }).ToList();
                var request = new NewSessionRequest(record.Cwd, record.Launch.Provider ?? "chatgpt", $"{task.Name} {run.Since:MM-dd HH:mm}",
                    Model: follows.Contains("model") ? defaults.Model : record.Launch.Model,
                    Effort: follows.Contains("effort") ? defaults.Effort : record.Launch.Effort,
                    PermissionMode: follows.Contains("permissionMode") ? defaults.PermissionMode : record.Launch.PermissionMode,
                    InitialPrompt: ScheduledTasks.RunText(task), Follows: follows);
                var (error, _) = await CreateSessionAsync(window.Id, request, _ => false, quiet: true);
                Finish(run, error is null, error is null ? "started a new thread" : $"the new thread did not start: {error}");
                return;
            }
            if (Runs(record.Id))
            {
                if (StatusOf(record.Id) == "idle")
                {
                    var typed = await TypeIntoAsync(record.Id, run.Wake ?? ScheduledTasks.RunText(task));
                    Finish(run, typed, typed ? "delivered" : "could not type into its thread");
                }
                else if (DateTimeOffset.Now - run.Since > TaskWaitLimit) Finish(run, false, "its thread stayed busy for 12 hours");
                return;
            }
            if (run.ResumedAt is { } resumed)
            {
                if (DateTimeOffset.Now - resumed > ResumeLimit) Finish(run, false, "its thread did not start again");
                return;
            }
            // Ended: resumed (in the window that has it, else placed in the first, without taking the focus).
            var owner = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, record.Id));
            if (owner is null && State.Windows.FirstOrDefault() is { } first && windows.TryGetValue(first.Id, out var shown))
            {
                shown.PlaceSessionQuietly(record.Id);
                owner = first;
            }
            if (owner is null)
            {
                Finish(run, false, "no window to resume its thread in");
                return;
            }
            run.ResumedAt = DateTimeOffset.Now;
            TaskLog($"{task.Id}: resuming {record.Id} for the run");
            quietStarts[record.Id] = DateTimeOffset.Now;
            Resume(owner.Id, record.Id);
        }
        catch (Exception e)
        {
            Finish(run, false, $"failed: {e.Message}");
        }
        finally
        {
            run.Busy = false;
        }
    }

    void Finish(PendingRun run, bool delivered, string result)
    {
        var task = run.Task;
        pendingRuns.Remove(task.Id);
        if (run.Wake is not null)
        {
            TaskLog($"{task.Id}: {result}");
            if (!delivered) toasts?.Show(task.TargetThreadId, $"Could not wake: {task.Name}", result);
            return;
        }
        var runs = ScheduledTasks.ReadRuns(TasksDir, task.Id);
        ScheduledTasks.WriteRuns(TasksDir, task.Id, runs with { LastResult = result, LastResultAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        TaskLog($"{task.Id}: {result}");
        if (delivered && !task.NewChat) taskActive[task.TargetThreadId] = new TaskActivity(DateTimeOffset.Now);
        TasksChanged?.Invoke();
        var tell = delivered ? task.NotificationPolicy == "all" : true;
        if (tell) toasts?.Show(task.TargetThreadId, delivered ? $"Scheduled task: {task.Name}" : $"Scheduled task not run: {task.Name}", result);
    }

    /// <summary>A message the user wrote in the chat view: typed into the running thread as Reframe's own messages are.</summary>
    public async Task<bool> SendChatMessageAsync(string sessionId, string text, HostTerminalConnection? own)
    {
        var running = Runs(sessionId);
        var sent = false;
        if (running && own is not null)
        {
            // Through the tile's own terminal connection, as the keys the user types go (a paste, then Enter).
            own.Paste(text);
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(300));
            own.WriteInput("\r");
            sent = true;
        }
        else if (running) sent = await TypeIntoAsync(sessionId, text);
        TaskLog($"chat: {text.Length} characters to {sessionId}: {(sent ? "typed" : running ? "typing failed" : "not running")}");
        return sent;
    }

    /// <summary>Types the text into the thread (a bracketed paste, then Enter a moment later), through a view of its own.</summary>
    async Task<bool> TypeIntoAsync(string sessionId, string text, bool interrupt = false)
    {
        if (Connection is not { IsOpen: true, IsObserver: false } connection) return false;
        var view = connection.CreateView(sessionId);
        // The user's own typing into this thread waits until the message is in.
        using var hold = InputGuard.Hold(sessionId);
        try
        {
            await view.OpenAsync();
            if (interrupt)
            {
                await view.InputAsync(Broadcast.Background);
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            await view.InputAsync("\u001b[200~" + text + "\u001b[201~");
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(300));
            await view.InputAsync("\r");
            return true;
        }
        catch (Exception e)
        {
            Trace.WriteLine($"typing into {sessionId} failed: {e.Message}");
            TaskLog($"typing into {sessionId} failed: {e.Message}");
            return false;
        }
        finally
        {
            view.Close();
        }
    }

    // ---------- the tasks dialog's changes ----------

    public void SetTaskStatus(ScheduledTask task, bool active) => ChangeTask(task, new Dictionary<string, object> { ["status"] = active ? "ACTIVE" : "PAUSED" });

    public void ChangeTask(ScheduledTask task, IReadOnlyDictionary<string, object> changes)
    {
        try
        {
            ScheduledTasks.Update(TasksDir, task, changes, DateTimeOffset.Now.ToUnixTimeMilliseconds());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TaskLog($"{task.Id}: not saved: {e.Message}");
        }
        ReloadTasks();
    }

    public void DeleteTask(ScheduledTask task)
    {
        pendingRuns.Remove(task.Id);
        try
        {
            ScheduledTasks.Delete(TasksDir, task.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TaskLog($"{task.Id}: not deleted: {e.Message}");
        }
        ReloadTasks();
    }

    /// <summary>The Codex automations whose thread has the name of one of reframe's (read only; Codex's files are
    /// never changed).</summary>
    public List<ScheduledTasks.CodexCandidate> CodexTasks()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var index = Path.Combine(codexHome, "session_index.jsonl");
        if (!File.Exists(index)) return [];
        try
        {
            var names = ScheduledTasks.CodexThreadNames(File.ReadLines(index));
            var sessions = State.Sessions.Values.Select(r => (r.Id, StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd)));
            return ScheduledTasks.CodexCandidates(ScheduledTasks.Load(Path.Combine(codexHome, "automations")), names, sessions, tasks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TaskLog($"codex tasks not read: {e.Message}");
            return [];
        }
    }

    public void ImportCodexTasks(IEnumerable<ScheduledTasks.CodexCandidate> chosen)
    {
        var taken = tasks.Select(t => t.Id).ToHashSet();
        foreach (var candidate in chosen)
        {
            var fields = ScheduledTasks.Imported(candidate, taken, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            taken.Add((string)fields["id"]);
            try
            {
                ScheduledTasks.Save(TasksDir, fields);
                // The schedule goes on from now: what Codex missed does not run here at once.
                ScheduledTasks.WriteRuns(TasksDir, (string)fields["id"], new TaskRuns(DateTimeOffset.Now.ToUnixTimeMilliseconds(), 0, "imported from Codex", null));
                TaskLog($"imported codex task {candidate.Task.Id} as {fields["id"]} for {candidate.SessionId}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TaskLog($"codex task {candidate.Task.Id} not imported: {e.Message}");
            }
        }
        ReloadTasks();
    }

    // ---------- waking reframe ----------

    static (int Exit, string Output) Schtasks(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info);
        if (process is null) return (-1, "not started");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000)) return (-1, "timed out");
        return (process.ExitCode, output.Trim());
    }

    static void RemoveWakeTask()
    {
        try
        {
            var name = ScheduledTasks.WakeTaskName(App.ProfileDir);
            if (Schtasks("/Query", "/TN", name).Exit != 0) return;
            var (exit, output) = Schtasks("/Delete", "/TN", name, "/F");
            TaskLog(exit == 0 ? "wake-up task removed" : $"wake-up task not removed: {output}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskLog($"wake-up task not checked: {e.Message}");
        }
    }

    /// <summary>At quit: with the setting on, the wake-up task for the next run due.</summary>
    void LeaveWakeTask()
    {
        if (Fixture is not null || Store is not { ReadOnly: false } || !State.Preferences.WakeForTasks || Environment.ProcessPath is not { } exe) return;
        var next = tasks.Where(t => t.Active).Select(t => ScheduledTasks.NextDue(t, ScheduledTasks.ReadRuns(TasksDir, t.Id)))
            .OfType<DateTimeOffset>().DefaultIfEmpty().Min();
        if (next == default)
        {
            TaskLog("no wake-up task: nothing scheduled");
            return;
        }
        var file = Path.Combine(TasksDir, ".wake-up.xml");
        try
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            File.WriteAllText(file, ScheduledTasks.WakeTaskXml(exe, next.LocalDateTime, user), System.Text.Encoding.Unicode);
            var (exit, output) = Schtasks("/Create", "/TN", ScheduledTasks.WakeTaskName(App.ProfileDir), "/XML", file, "/F");
            TaskLog(exit == 0 ? $"wake-up task set for {next.LocalDateTime:yyyy-MM-dd HH:mm}" : $"wake-up task not set: {output}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskLog($"wake-up task not set: {e.Message}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void StopTasks()
    {
        taskTimer?.Stop();
        taskReload?.Stop();
        taskWatcher?.Dispose();
        taskWatcher = null;
        LeaveWakeTask();
    }
}
