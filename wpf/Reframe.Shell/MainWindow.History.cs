using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// The chat history actions of src/main/app.ts: a chat's model provider and model (selectProvider, editChatModels
/// with ChatModelDialog), archive and delete (retireChats: the host's <c>history</c> service, one prepare and one retire
/// for any number of chats, after one confirmation), the selected chats' batch (runChatBatch) with its report
/// (.chat-batch-report), restoring an archived chat (restoreChat), and starting a stopped WSL distribution from the list
/// (<c>wsl.keep</c>). Only inactive native Windows chats change: a chat in WSL or one that runs is refused.
/// </summary>
public partial class MainWindow
{
    const string HelperMissing =
        "Archive/delete need the configured Python and verified thread-cleanup helper. See the ClaudeGPT installation guide.";

    Border? batchReport;
    bool batchOpen;

    static ControlTemplate BareButton()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        frame.AppendChild(presenter);
        template.VisualTree = frame;
        return template;
    }
    ChatModelDialog? modelDialog;

    sealed class HistoryError(string message) : Exception(message);

    // ---------- the chats a job is about ----------

    SessionRecord? RecordOf(ListItemRef item) =>
        item.Kind == ListItemKind.App
            ? item.SessionId is { } id ? state.Sessions.GetValueOrDefault(id) : null
            : state.Sessions.Values.FirstOrDefault(r => r.ClaudeSessionId == item.ClaudeSessionId);

    bool Active(SessionRecord r) => r.Lifecycle != "ended" || Runs(r.Id) || InWsl(r.Cwd);

    /// <summary>inactiveChat: the chat's record (a found chat's adopted one, not yet in the state), or why it cannot change.</summary>
    async Task<SessionRecord> InactiveChatAsync(ListItemRef item)
    {
        if (item.Kind == ListItemKind.Running || (item.Kind == ListItemKind.Found && !string.IsNullOrEmpty(item.Distro)))
            throw new HistoryError(ChatHistory.EndNativeFirst);
        if (RecordOf(item) is { } existing)
        {
            if (Active(existing)) throw new HistoryError(ChatHistory.EndFirst);
            return existing;
        }
        if (item.Kind != ListItemKind.Found || item.ClaudeSessionId is not { } id) throw new HistoryError("Chat no longer exists.");
        var info = await Sources().Transcripts.FindAsync(id);
        if (string.IsNullOrEmpty(info?.Cwd)) throw new HistoryError("Chat transcript/folder could not be resolved.");
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var record = new SessionRecord
        {
            Id = Guid.NewGuid().ToString(), ClaudeSessionId = id, Cwd = info.Cwd, Name = info.Title, Lifecycle = "ended",
            CreatedAt = now, LastActiveAt = info.ModifiedAt != 0 ? info.ModifiedAt : now, Used = true,
        };
        AssertStillInactive(record);
        return record;
    }

    /// <summary>assertChatStillInactive: rechecked right before a change.</summary>
    void AssertStillInactive(SessionRecord record)
    {
        if (state.Sessions.Values.Any(r => r.ClaudeSessionId == record.ClaudeSessionId && Active(r)))
            throw new HistoryError("This chat started or changed ownership; unchanged.");
    }

    string LabelOfRecord(SessionRecord r) => StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd);

    void ShowError(string message) => MessageBox.Show(this, message, "Reframe", MessageBoxButton.OK, MessageBoxImage.Error);

    // ---------- provider and model ----------

    async Task SelectProviderAsync(ListItemRef item, string provider, string? model = null, bool modelGiven = false)
    {
        var record = await InactiveChatAsync(item);
        if (!shell.HistoryBusy.Add(record.ClaudeSessionId)) throw new HistoryError(ChatHistory.Busy);
        try
        {
            AssertStillInactive(record);
            ChatHistory.SelectProvider(state, record, provider, model, modelGiven);
            Saved();
        }
        finally
        {
            shell.HistoryBusy.Remove(record.ClaudeSessionId);
        }
    }

    /// <summary>editChatModels: the dialog with the chats' common provider and model.</summary>
    void EditChatModels(IReadOnlyList<ListItemRef> items)
    {
        if (modal is not null) return;
        var (provider, model) = ChatHistory.ModelEditOf([.. items.Select(RecordOf).OfType<SessionRecord>()]);
        var dialog = new ChatModelDialog(items.Count, provider, model)
        {
            Apply = async (chosen, typed) =>
            {
                if (shell.ChatBatchRunning) return "Another batch or shutdown is already running.";
                await RunChatBatchAsync(items, "model", chosen, typed);
                return null;
            },
        };
        modelDialog = dialog;
        dialog.CloseRequested += CloseModal;
        ShowModal(dialog.Frame);
        modal!.Closed += () => modelDialog = null;
        if (!Capturing) Dispatcher.BeginInvoke(() => System.Windows.Input.Keyboard.Focus(dialog.Model), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- archive and delete ----------

    /// <summary>history_backend.py: REFRAME_HISTORY_BACKEND, else beside the shell, else the repository's maintenance
    /// folder above it (a development build).</summary>
    static string? HistoryBackend()
    {
        if (Environment.GetEnvironmentVariable("REFRAME_HISTORY_BACKEND") is { Length: > 0 } configured) return configured;
        var beside = Path.Combine(AppContext.BaseDirectory, "history_backend.py");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "maintenance", "history_backend.py");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    (HostServices Services, HistoryHelper Helper, string Root) HistoryRun()
    {
        if (services is not { } s || !s.Offers("history")) throw new HistoryError("The pty host does not offer chat history jobs.");
        var backend = HistoryBackend() ?? throw new HistoryError(HelperMissing);
        string root;
        try
        {
            root = ChatHistory.ArchiveRoot(state.Preferences.ArchiveRoot, Environment.GetEnvironmentVariable("REFRAME_ARCHIVE_ROOT"), App.ProfileDir);
        }
        catch (InvalidOperationException e)
        {
            throw new HistoryError(e.Message);
        }
        return (s, new HistoryHelper(backend, App.ProfileDir), root);
    }

    async Task RetireChatAsync(ListItemRef item, string operation)
    {
        var record = await InactiveChatAsync(item);
        var outcome = (await RetireChatsAsync([new ChatTarget(record, record.ClaudeSessionId)], operation)).FirstOrDefault();
        if (outcome?.Status == "failed") throw new HistoryError(outcome.Detail ?? "Chat operation failed.");
    }

    /// <summary>retireChats: one confirmation for the chats; one prepare and one retire job; a chat that started
    /// meanwhile is skipped. A chat without Claude history only loses Reframe's entry (delete).</summary>
    async Task<List<ChatBatchEntry>> RetireChatsAsync(IReadOnlyList<ChatTarget> targets, string operation)
    {
        var results = new List<ChatBatchEntry>();
        void Skip(ChatTarget t, string detail, string status = "skipped") => results.Add(new(t.Key, LabelOfRecord(t.Record), status, detail));
        var locked = new List<ChatTarget>();
        foreach (var target in targets)
        {
            if (shell.HistoryBusy.Add(target.Record.ClaudeSessionId)) locked.Add(target);
            else Skip(target, ChatHistory.Busy);
        }
        try
        {
            if (locked.Count == 0) return results;
            var recordOnly = new List<ChatTarget>();
            var withHistory = new List<ChatTarget>();
            foreach (var target in locked)
            {
                if (ChatHistory.HasClaudeHistory(home, target.Record.ClaudeSessionId)) withHistory.Add(target);
                else if (operation == "delete") recordOnly.Add(target);
                else Skip(target, "This chat has no Claude history to archive; Delete removes its entry.", "failed");
            }
            var prepared = new Dictionary<string, PreparedHistory>();
            (HostServices Services, HistoryHelper Helper, string Root)? run = null;
            if (withHistory.Count > 0)
            {
                try
                {
                    run = HistoryRun();
                    foreach (var outcome in await run.Value.Services.HistoryPrepareAsync(run.Value.Helper, home, run.Value.Root,
                                 withHistory.Select(t => t.Record.ClaudeSessionId)))
                    {
                        if (PreparedHistory.From(outcome) is { } plan) prepared[plan.Id] = plan;
                        else if (withHistory.FirstOrDefault(t => t.Record.ClaudeSessionId == (string?)outcome["id"]) is { } target)
                            Skip(target, (string?)outcome["error"] ?? "Chat operation failed.", "failed");
                    }
                }
                catch (Exception e) when (e is HistoryError or HostServiceException)
                {
                    foreach (var target in withHistory) Skip(target, e.Message, "failed");
                }
            }
            var ready = withHistory.Where(t => prepared.ContainsKey(t.Record.ClaudeSessionId)).ToList();
            var confirmed = ready.Concat(recordOnly).ToList();
            if (confirmed.Count == 0) return results;
            var (message, detail, button) = ChatHistory.Confirmation(operation,
                [.. confirmed.Select(t => (LabelOfRecord(t.Record), t.Record, prepared.GetValueOrDefault(t.Record.ClaudeSessionId)))]);
            var answer = MessageBox.Show(this, $"{message}\n\n{detail}\n\nOK: {button}.", "Reframe", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK || shell.Quitting)
            {
                foreach (var target in confirmed) Skip(target, "Cancelled; unchanged.");
                return results;
            }
            var changed = false;
            foreach (var target in recordOnly)
            {
                try
                {
                    AssertStillInactive(target.Record);
                    if (ChatHistory.HasClaudeHistory(home, target.Record.ClaudeSessionId))
                        throw new HistoryError("Claude history appeared since the confirmation; nothing was deleted.");
                    ChatHistory.WithoutHistory(state, target.Record.ClaudeSessionId);
                    changed = true;
                    results.Add(new(target.Key, LabelOfRecord(target.Record), "completed"));
                }
                catch (HistoryError e)
                {
                    Skip(target, e.Message);
                }
            }
            var jobs = new System.Text.Json.Nodes.JsonArray();
            var jobTargets = new List<ChatTarget>();
            foreach (var target in ready)
            {
                try
                {
                    AssertStillInactive(target.Record);
                    jobs.Add(ChatHistory.RetireJob(prepared[target.Record.ClaudeSessionId], operation, target.Record, LabelOfRecord(target.Record)));
                    jobTargets.Add(target);
                }
                catch (HistoryError e)
                {
                    Skip(target, e.Message);
                }
            }
            if (jobTargets.Count > 0 && run is { } r)
            {
                try
                {
                    foreach (var outcome in await r.Services.HistoryRetireAsync(r.Helper, home, jobs))
                    {
                        var id = (string?)outcome["id"];
                        if (jobTargets.FirstOrDefault(t => t.Record.ClaudeSessionId == id) is not { } target) continue;
                        if ((bool?)outcome["ok"] != true)
                        {
                            Skip(target, (string?)outcome["error"] ?? "Chat operation failed.", (string?)outcome["code"] == "chat_active" ? "skipped" : "failed");
                            continue;
                        }
                        var label = LabelOfRecord(target.Record);
                        ChatHistory.WithoutHistory(state, target.Record.ClaudeSessionId);
                        if ((string?)outcome["archive"] is { } archive && (string?)outcome["sha256"] is { } sha256)
                            ChatHistory.AddArchive(state, target.Record, label, archive, sha256, prepared[target.Record.ClaudeSessionId].Operation);
                        changed = true;
                        results.Add(new(target.Key, label, "completed"));
                    }
                }
                catch (HostServiceException e)
                {
                    foreach (var target in jobTargets.Where(t => results.All(x => x.Id != t.Key))) Skip(target, e.Message, "failed");
                }
            }
            if (changed) Saved();
            return results;
        }
        finally
        {
            foreach (var target in locked) shell.HistoryBusy.Remove(target.Record.ClaudeSessionId);
        }
    }

    // ---------- the selected chats ----------

    /// <summary>runChatBatch: each distinct chat checked, then archived/deleted together or its route changed; the
    /// report shows progress and the outcome of each.</summary>
    async Task RunChatBatchAsync(IReadOnlyList<ListItemRef> items, string operation, string? provider = null, string? model = null)
    {
        if (shell.ChatBatchRunning || shell.Quitting) throw new HistoryError("Another batch or shutdown is already running.");
        shell.ChatBatchRunning = true;
        var results = new List<ChatBatchEntry>();
        ShowBatch(results, running: true);
        var seen = new HashSet<string>();
        var targets = new List<(ListItemRef Item, ChatTarget Target)>();
        try
        {
            foreach (var item in items)
            {
                var existing = item.Kind == ListItemKind.App && item.SessionId is { } sid ? state.Sessions.GetValueOrDefault(sid) : null;
                var id = item.Kind == ListItemKind.App ? existing?.ClaudeSessionId ?? item.SessionId ?? "" : item.ClaudeSessionId ?? "";
                var distro = item.Kind == ListItemKind.App ? existing is not null && InWsl(existing.Cwd) ? DistroOf(existing.Cwd) : null : item.Distro;
                var key = SidebarChats.ChatKey(id, distro);
                if (!seen.Add(key)) continue;
                try
                {
                    var record = await InactiveChatAsync(item);
                    if (shell.HistoryBusy.Contains(record.ClaudeSessionId)) throw new HistoryError(ChatHistory.Busy);
                    targets.Add((item, new ChatTarget(record, key)));
                }
                catch (HistoryError e)
                {
                    results.Add(new(key, existing?.Name is { Length: > 0 } name ? name : id, "skipped", e.Message));
                }
            }
            if (operation is "archive" or "delete")
            {
                if (targets.Count > 0) results.AddRange(await RetireChatsAsync([.. targets.Select(t => t.Target)], operation));
                return;
            }
            foreach (var (item, target) in targets)
            {
                var entry = new ChatBatchEntry(target.Key, LabelOfRecord(target.Record), "completed");
                try
                {
                    if (provider is null) throw new HistoryError("Choose an explicit provider.");
                    if (operation == "model") await SelectProviderAsync(item, provider, model?.Trim(), modelGiven: true);
                    else await SelectProviderAsync(item, provider);
                    results.Add(entry);
                }
                catch (HistoryError e)
                {
                    results.Add(entry with { Status = "skipped", Detail = e.Message });
                }
                ShowBatch(results, running: true);
            }
        }
        finally
        {
            shell.ChatBatchRunning = false;
            ShowBatch(results, running: false);
            shell.ListChanged();
        }
    }

    static string? DistroOf(string cwd)
    {
        var parts = cwd.TrimStart('\\').Split('\\', 3);
        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>.chat-batch-report over the window: the counts, the results (a details), Dismiss once done.</summary>
    void ShowBatch(IReadOnlyList<ChatBatchEntry> results, bool running)
    {
        var panel = new StackPanel();
        var status = new TextBlock { Text = ChatHistory.Summary(results, running), Style = S("Text13"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        panel.Children.Add(status);
        var lines = new StackPanel { Margin = new Thickness(20, 6, 0, 6) };
        foreach (var entry in results)
        {
            var line = new TextBlock { Style = S("Text13"), TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new System.Windows.Documents.Run("• "));
            line.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run(entry.Label)));
            line.Inlines.Add(new System.Windows.Documents.Run(ChatHistory.Line(entry)[entry.Label.Length..]));
            lines.Children.Add(line);
        }
        // The details element: its summary toggles the list (closed at first).
        lines.Visibility = batchOpen ? Visibility.Visible : Visibility.Collapsed;
        var summary = new Button
        {
            Template = BareButton(), Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = $"{(batchOpen ? "▾" : "▸")} Chat results", Style = S("Text13"), Margin = new Thickness(0, 4, 0, 0) },
        };
        AutomationProperties.SetName(summary, "Chat results");
        AutomationProperties.SetItemStatus(summary, batchOpen ? "expanded" : "collapsed");
        summary.Click += (_, _) =>
        {
            batchOpen = !batchOpen;
            lines.Visibility = batchOpen ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)summary.Content).Text = $"{(batchOpen ? "▾" : "▸")} Chat results";
            AutomationProperties.SetItemStatus(summary, batchOpen ? "expanded" : "collapsed");
        };
        panel.Children.Add(summary);
        panel.Children.Add(lines);
        if (!running)
        {
            var dismiss = new Button { Style = S("ButtonBaseStyle"), Content = new TextBlock { Text = "Dismiss results", Style = S("Text13") }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            dismiss.Click += (_, _) => batchReport!.Visibility = Visibility.Collapsed;
            panel.Children.Add(dismiss);
        }
        batchReport!.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        batchReport.Visibility = Visibility.Visible;
    }

    /// <summary>The report's place: a row of the window under the toolbar (hidden until a batch runs).</summary>
    Border BatchReport()
    {
        batchReport = new Border
        {
            Padding = new Thickness(16, 8, 16, 8), MaxHeight = 180, BorderBrush = B("borderBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
            Background = B("bgBrush"), Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(batchReport, "Selected chat results");
        return batchReport;
    }

    // ---------- menus ----------

    /// <summary>A chat action of a row's or tile's menu (provider, model, archive, delete).</summary>
    bool RunHistoryAction(string action, ChatRow? chat, string? sessionId)
    {
        var item = chat?.Ref ?? (sessionId is null ? null : new ListItemRef(ListItemKind.App, sessionId));
        if (item is null) return false;
        Task? work = action switch
        {
            _ when action.StartsWith("provider:", StringComparison.Ordinal) => SelectProviderAsync(item, action["provider:".Length..]),
            "changeModel" => EditChatModelsAsync(item),
            "archive" => RetireChatAsync(item, "archive"),
            "delete" => RetireChatAsync(item, "delete"),
            _ => null,
        };
        if (work is null) return false;
        _ = Report(work);
        return true;
    }

    Task EditChatModelsAsync(ListItemRef item)
    {
        EditChatModels([item]);
        return Task.CompletedTask;
    }

    async Task Report(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception e) when (e is HistoryError or HostServiceException)
        {
            ShowError(e.Message);
        }
    }

    /// <summary>The selected chats' menu (listMenu with batchChatActions).</summary>
    void ShowSelectionMenu(IReadOnlyList<ChatRow> chats, FrameworkElement target)
    {
        var items = SelectionMenuOf(chats);
        var refs = chats.Select(c => c.Ref).ToList();
        var menu = ShellMenus.Build(items, action => action == "open" || (Writable && action.StartsWith("batch", StringComparison.Ordinal)), action =>
        {
            switch (action)
            {
                case "open":
                    foreach (var chat in chats) OpenChat(chat);
                    break;
                case "batchModel":
                    EditChatModels(refs);
                    break;
                case "batchArchive":
                    _ = Report(RunChatBatchAsync(refs, "archive"));
                    break;
                case "batchDelete":
                    _ = Report(RunChatBatchAsync(refs, "delete"));
                    break;
                default:
                    if (action.StartsWith("batchProvider:", StringComparison.Ordinal))
                        _ = Report(RunChatBatchAsync(refs, "provider", action["batchProvider:".Length..]));
                    break;
            }
        });
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    List<MenuSpec> SelectionMenuOf(IReadOnlyList<ChatRow> chats) =>
        ContextMenus.ListMenu(chats.Count, false, ContextMenus.BatchChatActions(shell.ChatBatchRunning));

    // ---------- archived chats and WSL ----------

    /// <summary>restoreChat: the archive's files back, an ended record of it listed; the error, or null.</summary>
    async Task<string?> RestoreArchiveAsync(ArchiveEntry entry)
    {
        if (!Writable) return "Reframe's state is read-only here.";
        if (!shell.HistoryBusy.Add(entry.Id)) return ChatHistory.Busy;
        try
        {
            var (s, helper, _) = HistoryRun();
            await s.HistoryRestoreAsync(helper, home, entry);
            ChatHistory.Restored(state, entry, Guid.NewGuid().ToString(), DateTimeOffset.Now.ToUnixTimeMilliseconds());
            Saved();
            return null;
        }
        catch (Exception e) when (e is HistoryError or HostServiceException)
        {
            return e.Message;
        }
        finally
        {
            shell.HistoryBusy.Remove(entry.Id);
        }
    }

    // ---------- capture ----------

    /// <summary>The "history" fixture: two past chats selected with Ctrl+click (history-selection), then Change
    /// model… of their menu (history-model-editor).</summary>
    async Task CaptureHistoryAsync(HwndSource popup, string dir)
    {
        sidebar.SelectLabels(fixture?.Select ?? []);
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "history-selection");
        var chosen = sidebar.Chosen();
        EditChatModels([.. chosen.Select(c => c.Ref)]);
        // Electron focuses the Model field (its accent border).
        if (modelDialog is not null) Diag.SetFocused(modelDialog.Model, true);
        await DiagCapture.SettleAsync(Dispatcher, 500);
        DiagCapture.Save(popup, root, dir, "history-model-editor", modal?.Overlay);
        if (modelDialog is not null) DiagCapture.Save(popup, modelDialog.Frame, dir, "history-model-editor-component", modal?.Overlay);
        CloseModal();
        // WPF only: the report after a batch (no host jobs in a capture: the fixture's results).
        ShowBatch([new("a", "Bulk 1: tidy the release scripts", "completed"), new("b", "Bulk 2: profile the search endpoint", "skipped", ChatHistory.EndFirst)], running: false);
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "history-results");
        batchReport!.Visibility = Visibility.Collapsed;
    }

    /// <summary>The "wsl" fixture: a chat of a running distribution, a stopped distribution's note and the archived
    /// chats (opened).</summary>
    async Task CaptureWslAsync(HwndSource popup, string dir)
    {
        sidebar.OpenArchives();
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "sidebar-wsl");
        if (fixture?.Wsl.FirstOrDefault() is { } source)
        {
            var title = (await source.Transcripts.ListAsync("", 1, [], null)).Items.FirstOrDefault()?.Title;
            if (title is not null) await SaveRowAsync(popup, dir, "sidebar-row-wsl-running", title);
        }
        foreach (var distro in fixture?.WslStopped ?? [])
            if (sidebar.WslNoteOf(distro) is { } note)
            {
                note.BringIntoView();
                await DiagCapture.SettleAsync(Dispatcher, 200);
                DiagCapture.Save(popup, note, dir, "sidebar-row-wsl-stopped");
                break;
            }
    }
}
