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
        /// <summary>For a wake-up: the thread that sent it, told when it could not be delivered.</summary>
        public string? AskerId { get; init; }
        /// <summary>The text to type instead of the task's prompt: a wake-up from another thread (message_thread), which
        /// is not a saved task (nothing of it is written, only a failure is told).</summary>
        public string? Wake { get; init; }
        /// <summary>A message_thread message without wake: held again instead of resuming its thread, should the thread
        /// end before it is typed in.</summary>
        public HeldMessage? HoldIfEnded { get; init; }
        /// <summary>The log told it waits for the user's unsent prompt.</summary>
        public bool WaitedForUser { get; set; }
    }

    static readonly TimeSpan TaskWaitLimit = TimeSpan.FromHours(12);
    static readonly TimeSpan ResumeLimit = TimeSpan.FromMinutes(3);

    List<ScheduledTask> tasks = [];
    readonly Dictionary<string, PendingRun> pendingRuns = [];
    DispatcherTimer? taskTimer;
    DispatcherTimer? taskReload;
    FileSystemWatcher? taskWatcher;

    public static string TasksDir => Path.Combine(App.ProfileDir, "tasks");
    static string HeldFile => Path.Combine(MessagesDir, "held.json");
    /// <summary>Messages for ended threads (HeldMessages), loaded at the first use; the shell alone writes the file.</summary>
    List<HeldMessage>? held;
    List<HeldMessage> Held => held ??= HeldMessages.Load(HeldFile);
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
    // A thread something was just typed into, until it is seen busy (or a minute passed): another run due at the same
    // time waits for its turn instead of typing into the same prompt.
    readonly Dictionary<string, DateTimeOffset> typedInto = [];

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
        DeliverHeld(now);
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

    /// <summary>A thread messaged another by name (its <c>message_thread</c> or <c>wake_thread</c> tool): the message is
    /// delivered like a task's run (waiting until it can be typed in, resuming an ended thread); without "resume"
    /// (message_thread's wake false) an ended thread is not resumed: the message is held until it next runs. An older
    /// host posts no "resume": it wakes. Threads sharing the name all get it (only the running ones if any runs), unless
    /// "provider" picks one; the asker is told which got it, or that no thread has the name.</summary>
    async Task WakeThreadAsync(JsonObject hook)
    {
        var askerId = (string?)hook["sessionId"];
        string? Text(string key) => hook["payload"] is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
        if (askerId is null || !State.Sessions.TryGetValue(askerId, out var asker) || Text("wake") is not { } name || Text("message") is not { } message) return;
        var provider = Text("provider");
        var matches = ThreadsNamed(askerId, name).Where(r => provider is null || ToolProvider(r) == provider).ToList();
        // Several of that name all get it (a copy of a thread, or one per provider), only the running ones if any runs.
        if (matches.Count > 1 && matches.Any(r => Runs(r.Id))) matches = matches.Where(r => Runs(r.Id)).ToList();
        var askerName = StateValues.SessionLabel(asker.Name, asker.ClaudeName, asker.Cwd);
        if (matches.Count == 0)
        {
            var why = $"no thread{(provider is null ? "" : $" on {provider}")} is named \"{name}\"";
            TaskLog($"wake: {askerId}: {why}");
            if (Runs(askerId)) await TypeIntoAsync(askerId, $"[Reframe: your message was not delivered: {why}]");
            return;
        }
        var resume = hook["payload"]?["resume"] is not JsonValue r || !r.TryGetValue<bool>(out var b) || b;
        // message_thread answers it, whatever its provider (a Gemini thread has no SendMessage).
        var by = $"[message from the Reframe thread \"{askerName}\"; answer it with message_thread to \"{askerName}\"]";
        var notes = new List<string>();
        if (matches.Count > 1)
            notes.Add($"{matches.Count} threads are named \"{name}\" and each gets your message: {string.Join(", ", matches.Select(t => $"[{t.Id[..6]}] ({ToolProvider(t)}, {(Runs(t.Id) ? "running" : "ended")})"))}. Give provider, or the id in brackets after the name, to reach one.");
        var runs = new List<PendingRun>();
        foreach (var target in matches)
        {
            var kept = new HeldMessage(target.Id, name, askerId, askerName, $"{by}\n{message}", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            if (!resume && !Runs(target.Id))
            {
                Hold(kept);
                notes.Add($"\"{name}\"{(matches.Count > 1 ? $" [{target.Id[..6]}]" : "")} has ended; your message is kept and given to it when it next starts. Send it with wake: true to start it now.");
                continue;
            }
            runs.Add(WakeRun(kept, resume));
            TaskLog($"wake: {askerId} {(resume ? "wakes" : "messages")} {target.Id}");
        }
        if (notes.Count > 0 && Runs(askerId)) await TypeIntoAsync(askerId, $"[Reframe: {string.Join(" ", notes)}]");
        await Task.WhenAll(runs.Select(AdvanceAsync));
    }

    /// <summary>The provider as the thread tools name it (openai, claude or gemini).</summary>
    static string ToolProvider(SessionRecord record) => (record.Launch.Provider ?? "chatgpt") switch
    {
        "anthropic" => "claude",
        "gemini" => "gemini",
        _ => "openai",
    };

    /// <summary>A pending run typing <paramref name="message"/> into its thread; without <paramref name="resume"/> it
    /// is held again if the thread has ended by then.</summary>
    PendingRun WakeRun(HeldMessage message, bool resume)
    {
        var task = new ScheduledTask($"wake-{Guid.NewGuid():N}", "heartbeat", message.TargetName, message.Text, "ACTIVE", "", message.TargetId, "important", 0, 0, new Dictionary<string, object>());
        var run = new PendingRun(task, DateTimeOffset.Now) { Wake = message.Text, AskerId = message.AskerId, HoldIfEnded = resume ? null : message };
        pendingRuns[task.Id] = run;
        return run;
    }

    void Hold(HeldMessage message)
    {
        Held.Add(message);
        SaveHeld();
        TaskLog($"wake: held for ended {message.TargetId} from {message.AskerId}");
    }

    void SaveHeld()
    {
        try
        {
            HeldMessages.Save(HeldFile, Held);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TaskLog($"held messages not saved: {e.Message}");
        }
    }

    /// <summary>At each tick: the held messages of threads that run now become their wake-ups; those held longer than
    /// LongMessages.KeepFor are dropped, their senders told.</summary>
    void DeliverHeld(DateTimeOffset now)
    {
        if (Held.Count == 0) return;
        var changed = false;
        foreach (var message in HeldMessages.TakeExpired(Held, now))
        {
            changed = true;
            TaskLog($"wake: held message for {message.TargetId} expired");
            if (Runs(message.AskerId))
                _ = TypeIntoAsync(message.AskerId, $"[Reframe: your message to \"{message.TargetName}\" was not delivered: it was kept {LongMessages.KeepFor.TotalDays:0} days and \"{message.TargetName}\" did not start.]");
        }
        foreach (var targetId in Held.Select(m => m.TargetId).Distinct().Where(Runs).ToList())
            foreach (var message in HeldMessages.TakeFor(Held, targetId))
            {
                changed = true;
                var run = WakeRun(message, resume: false);
                TaskLog($"wake: held message delivered to {targetId} ({run.Task.Id})");
                _ = AdvanceAsync(run);
            }
        if (changed) SaveHeld();
    }

    /// <summary>A Claude Code cron job of one thread fired in another (Claude Code keeps durable jobs per folder), and
    /// the host blocked it there (reframe-host's cron.rs): it is delivered to its own thread like a task's run
    /// (waiting for idle, resuming an ended thread); one whose thread is gone is told as a run that failed. A notice of
    /// missed jobs comes split by thread, the part with the thread's own jobs back to it as it was.</summary>
    async Task HandOffAsync(JsonObject hook)
    {
        if (hook["payload"]?["to"] is not JsonArray to) return;
        var fromId = (string?)hook["sessionId"];
        var from = fromId is not null && State.Sessions.TryGetValue(fromId, out var r) ? StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd) : "another thread";
        var runs = new List<PendingRun>();
        foreach (var item in to)
        {
            if (item?["owner"] is not JsonValue o || !o.TryGetValue<string>(out var owner) || item["prompt"] is not JsonValue p || !p.TryGetValue<string>(out var prompt)) continue;
            var task = new ScheduledTask($"handoff-{Guid.NewGuid():N}", "heartbeat", "Scheduled job", prompt, "ACTIVE", "", owner, "important", 0, 0, new Dictionary<string, object>());
            var wake = owner == fromId ? prompt : $"[Reframe: this scheduled job of yours came up in the thread \"{from}\", which did not run it; it is yours]\n{prompt}";
            var run = new PendingRun(task, DateTimeOffset.Now) { Wake = wake };
            pendingRuns[task.Id] = run;
            TaskLog($"handoff: a job of {owner} came up in {fromId}");
            runs.Add(run);
        }
        foreach (var run in runs) await AdvanceAsync(run);
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
                if (typedInto.TryGetValue(record.Id, out var typedAt))
                {
                    if (DateTimeOffset.Now - typedAt < TimeSpan.FromMinutes(1)) return;
                    typedInto.Remove(record.Id);
                }
                // The user writes in the thread: the run waits (typed in, it would be sent with their half-written prompt).
                if (InputGuard.UserOwns(record.Id))
                {
                    if (!run.WaitedForUser) TaskLog($"{task.Id}: waits: the user is writing in its thread");
                    run.WaitedForUser = true;
                    return;
                }
                if (CanTypeInNow(record))
                {
                    typedInto[record.Id] = DateTimeOffset.Now;
                    var typed = await TypeIntoAsync(record.Id, ThroughFile(record, run.Wake ?? ScheduledTasks.RunText(task)));
                    if (!typed) typedInto.Remove(record.Id);
                    Finish(run, typed, typed ? "delivered" : "could not type into its thread");
                }
                else if (DateTimeOffset.Now - run.Since > TaskWaitLimit) Finish(run, false, "its thread stayed busy for 12 hours");
                return;
            }
            if (run.HoldIfEnded is { } again)
            {
                pendingRuns.Remove(task.Id);
                Hold(again);
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

    /// <summary>Where long messages for threads are kept (<see cref="LongMessages"/>).</summary>
    public static string MessagesDir => Path.Combine(App.ProfileDir, "messages");

    /// <summary>A long message goes to a file and the thread gets a line naming it (as a WSL thread reaches it); a short
    /// one, or one whose file could not be written, is typed in as it is.</summary>
    static string ThroughFile(SessionRecord record, string text)
    {
        try
        {
            var distro = WslPaths.Parse(record.Cwd)?.Distro;
            Func<string, string>? reader = distro is null ? null : p => WslPaths.LinuxPathOf(p, distro, WslPaths.DefaultMountRoot) ?? p;
            var prepared = LongMessages.Prepare(text, MessagesDir, DateTimeOffset.Now, reader);
            if (prepared != text) TaskLog($"{record.Id}: a message of {text.Length} characters goes through a file");
            return prepared ?? text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TaskLog($"{record.Id}: the long message's file was not written ({e.Message}); typed in as it is");
            return text;
        }
    }

    /// <summary>Whether a message can be typed into a running thread now. An idle thread takes it. A busy Claude Code
    /// thread (Anthropic or ChatGPT route), working or idle with background work, queues it for its next step, as when the user types while it works: most
    /// threads are rarely idle, so it is not kept waiting, unless the user is in that thread's tile. Never while the
    /// thread asks something (a permission or a question: typing would answer it), nor into a busy Gemini thread.</summary>
    bool CanTypeInNow(SessionRecord record) => StatusOf(record.Id) switch
    {
        "idle" => true,
        "busy" or "background" => record.Launch.Provider != "gemini" && !windows.Values.Any(w => w.IsIn(record.Id)),
        _ => false,
    };

    void Finish(PendingRun run, bool delivered, string result)
    {
        var task = run.Task;
        pendingRuns.Remove(task.Id);
        if (run.Wake is not null)
        {
            TaskLog($"{task.Id}: {result}");
            if (!delivered) toasts?.Show(task.TargetThreadId, $"Could not wake: {task.Name}", result);
            // The sender is told too, so it can send again instead of waiting for an answer that cannot come.
            if (!delivered && run.AskerId is { } asker && Runs(asker))
                _ = TypeIntoAsync(asker, $"[Reframe: your message to \"{task.Name}\" was not delivered: {result}. Send it again with message_thread if it is still needed.]");
            return;
        }
        var runs = ScheduledTasks.ReadRuns(TasksDir, task.Id);
        // A run that was not delivered does not use up one of the task's runs (COUNT); its time stays taken, so it is not
        // tried again at once.
        ScheduledTasks.WriteRuns(TasksDir, task.Id, runs with
        {
            LastResult = result, LastResultAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(), Runs = delivered ? runs.Runs : Math.Max(0, runs.Runs - 1),
        });
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
        else if (running) sent = await TypeIntoAsync(sessionId, text, queued: false);
        TaskLog($"chat: {text.Length} characters to {sessionId}: {(sent ? "typed" : running ? "typing failed" : "not running")}");
        return sent;
    }

    /// <summary>Types the text into the thread (a bracketed paste, then Enter a moment later), through a view of its own.
    /// Reframe's own messages (queued) wait in the thread's queue while the user writes in it; the user's own (a
    /// broadcast, the chat view's) do not.</summary>
    async Task<bool> TypeIntoAsync(string sessionId, string text, bool interrupt = false, bool queued = true)
    {
        // The host that runs the thread: an older host keeps the threads it ran before Reframe was updated.
        if (ConnectionOf(sessionId) is not { IsOpen: true, IsObserver: false } connection) return false;
        if (queued && InputGuard.UserOwns(sessionId)) TaskLog($"typing into {sessionId} waits: the user is writing in it");
        // The user's own typing into this thread waits until the message is in.
        using var hold = queued ? await InputGuard.TakeAsync(sessionId) : InputGuard.Hold(sessionId);
        if (!connection.IsOpen || !Runs(sessionId)) return false;
        var view = connection.CreateView(sessionId);
        try
        {
            await view.OpenAsync();
            if (interrupt)
            {
                await view.InputAsync(Broadcast.Background);
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            // An idle thread shows it took the prompt by turning busy (UserPromptSubmit). A long paste into a big
            // session can still be read in when Enter comes, and that Enter is lost: it is pressed again while the
            // prompt waits (an Enter on an empty prompt does nothing). A slash command (/rename, /model) runs in Claude
            // Code itself and never turns the thread busy: it is not waited for.
            var idle = StatusOf(sessionId) == "idle" && !text.StartsWith('/');
            var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (idle) submitWaits[sessionId] = taken;
            await view.InputAsync("\u001b[200~" + text + "\u001b[201~");
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(300));
            await view.InputAsync("\r");
            if (!idle) return true;
            for (var attempt = 1; ; attempt++)
            {
                if (await System.Threading.Tasks.Task.WhenAny(taken.Task, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5))) == taken.Task)
                {
                    if (attempt > 1) TaskLog($"typing into {sessionId}: taken after Enter number {attempt} ({text.Length} characters)");
                    return true;
                }
                if (attempt == 4 || !Runs(sessionId)) break;
                await view.InputAsync("\r");
            }
            if (submitWaits.GetValueOrDefault(sessionId) == taken) submitWaits.Remove(sessionId);
            TaskLog($"typing into {sessionId}: the prompt was not taken ({text.Length} characters; still {StatusOf(sessionId)} after 4 Enters)");
            return false;
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
        // A run already due (one not delivered before the quit) does not start Reframe again right after you quit it.
        var soonest = DateTimeOffset.Now.AddMinutes(15);
        if (next < soonest) next = soonest;
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
