using System.Globalization;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>One chat's outcome in a selected-chats batch (ChatBatchResult's results).</summary>
public sealed record ChatBatchEntry(string Id, string Label, string Status, string? Detail = null);

/// <summary>A chat a history job is about: its record (adopted for a found chat) and its chat key.</summary>
public sealed record ChatTarget(SessionRecord Record, string Key);

/// <summary>A prepared chat's plan (history.prepare's ok outcome).</summary>
public sealed record PreparedHistory(string Id, string Plan, string Sha256, string Operation, long Files, long Bytes, JsonObject Raw)
{
    public static PreparedHistory? From(JsonObject o) =>
        (bool?)o["ok"] == true && (string?)o["id"] is { } id
            ? new PreparedHistory(id, (string?)o["plan"] ?? "", (string?)o["sha256"] ?? "", (string?)o["operation"] ?? "",
                (long?)o["files"] ?? 0, (long?)o["bytes"] ?? 0, o)
            : null;
}

/// <summary>
/// The rules of src/main/app.ts's chat history actions (selectProvider, editChatModels, runChatBatch, retireChats,
/// restoreChat, withoutHistory) and the texts they show, without the host: the shell runs the jobs through the host's
/// <c>history</c> service and applies these to its state.
/// </summary>
public static class ChatHistory
{
    public const string Busy = "A history operation is already running.";
    public const string EndFirst = "End this chat before changing its provider or history.";
    public const string EndNativeFirst = "End the native Windows chat before changing it.";

    /// <summary>modelForProviderChange (src/shared/providers.ts): the model is kept only on the same route.</summary>
    public static string? ModelForProviderChange(string? current, string next, string? model) =>
        (current ?? "chatgpt") == next ? model : null;

    /// <summary>selectProvider: the record's route of its next start (provider; model as given, else kept only on
    /// the same route; an empty model is the provider's default), put into the state.</summary>
    public static SessionRecord SelectProvider(AppState state, SessionRecord record, string provider, string? model = null, bool modelGiven = false)
    {
        record.Launch.Model = modelGiven
            ? (string.IsNullOrEmpty(model) ? null : model)
            : ModelForProviderChange(record.Launch.Provider, provider, record.Launch.Model);
        record.Launch.Provider = provider;
        state.Sessions[record.Id] = record;
        return record;
    }

    /// <summary>editChatModels: the dialog's provider (the records' common one, else ChatGPT) and model (their common one).</summary>
    public static (string Provider, string? Model) ModelEditOf(IReadOnlyList<SessionRecord> records)
    {
        var provider = records.FirstOrDefault()?.Launch.Provider ?? "chatgpt";
        var same = records.All(r => (r.Launch.Provider ?? "chatgpt") == provider);
        var model = same && records.All(r => r.Launch.Model == records.FirstOrDefault()?.Launch.Model) ? records.FirstOrDefault()?.Launch.Model : null;
        return (same ? provider : "chatgpt", model);
    }

    /// <summary>The model field's check (changeChatModels): at most 300 characters, no control characters.</summary>
    public static bool ValidModel(string model) => model.Length <= 300 && model.All(c => c >= 32);

    /// <summary>withoutHistory: the thread's records gone (from their windows too), its Windows pin unpinned.</summary>
    public static void WithoutHistory(AppState state, string claudeSessionId)
    {
        var removed = state.Sessions.Values.Where(r => r.ClaudeSessionId == claudeSessionId).Select(r => r.Id).ToList();
        foreach (var id in removed) state.Sessions.Remove(id);
        if (state.PinnedChats is { } pins)
            state.PinnedChats = pins.Where(key => SidebarChats.ParseChatKey(key) is not { } pin || pin.Id != claudeSessionId || pin.Distro is not null).ToList();
        foreach (var window in state.Windows)
            foreach (var id in removed) ShellActions.DropSession(window, id);
    }

    /// <summary>restoreChat's change: an ended record of the archived chat (its label, folder and provider), the
    /// archive entry gone.</summary>
    public static SessionRecord Restored(AppState state, ArchiveEntry entry, string newId, double now)
    {
        var record = new SessionRecord
        {
            Id = newId, ClaudeSessionId = entry.Id, Cwd = entry.Cwd, Name = entry.Label,
            Launch = new LaunchOptions { Provider = entry.Provider }, Lifecycle = "ended", Used = true, CreatedAt = now, LastActiveAt = now,
        };
        state.Sessions[record.Id] = record;
        state.Archives = (state.Archives ?? []).Where(a => a.Id != entry.Id).ToList();
        return record;
    }

    /// <summary>A retired chat's archive (retire's outcome with an archive and its sha256) added to state.archives.</summary>
    public static void AddArchive(AppState state, SessionRecord record, string label, string archive, string sha256, string operation)
    {
        state.Archives = [.. state.Archives ?? [], new ArchiveEntry
        {
            Id = record.ClaudeSessionId, Label = label, Cwd = record.Cwd, Provider = record.Launch.Provider ?? "chatgpt",
            Archive = archive, Sha256 = sha256, Operation = operation,
        }];
    }

    /// <summary>retire's job for a prepared chat: <c>{ prepared, mode, metadata: { id, label, cwd, provider } }</c>.</summary>
    public static JsonObject RetireJob(PreparedHistory prepared, string mode, SessionRecord record, string label) => new()
    {
        ["prepared"] = prepared.Raw.DeepClone(),
        ["mode"] = mode,
        ["metadata"] = new JsonObject
        {
            ["id"] = record.ClaudeSessionId, ["label"] = label, ["cwd"] = record.Cwd, ["provider"] = record.Launch.Provider ?? "chatgpt",
        },
    };

    /// <summary>The archive/delete confirmation (retireChats' message box): its message, detail and confirming button.</summary>
    public static (string Message, string Detail, string Button) Confirmation(string operation,
        IReadOnlyList<(string Label, SessionRecord Record, PreparedHistory? Prepared)> confirmed)
    {
        var verb = operation == "archive" ? "Archive" : "Permanently delete";
        var message = confirmed.Count == 1 ? $"{verb} “{confirmed[0].Label}”?" : $"{verb} {confirmed.Count} selected chats?";
        var ready = confirmed.Where(c => c.Prepared is not null).ToList();
        var files = ready.Sum(c => c.Prepared!.Files);
        var bytes = ready.Sum(c => c.Prepared!.Bytes);
        var detail = string.Join("\n\n", confirmed.Select(c =>
                $"{c.Label} · {c.Record.ClaudeSessionId}\n{c.Record.Cwd}" + (c.Prepared is null ? "\nNo Claude history: only the Reframe entry is removed." : "")))
            + (ready.Count > 0 ? $"\n\n{files} exact chat files ({bytes.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} bytes).\n" : "\n\n")
            + (operation == "archive"
                ? "Each chat gets a verified restorable ZIP before its live files are removed. Restore remains available in Archived chats."
                : "Transcripts, owned session/subagent files and matching history entries are permanently removed. This cannot be undone.")
            + "\nProject files and other chats are preserved. A chat that is running by then is skipped.";
        return (message, detail, operation == "archive" ? "Archive" : "Delete permanently");
    }

    /// <summary>The batch report's status line (App.tsx .chat-batch-report).</summary>
    public static string Summary(IReadOnlyList<ChatBatchEntry> results, bool running) =>
        (running ? "Working on selected chats… " : "Selected chat action finished. ")
        + $"{results.Count(r => r.Status == "completed")} completed · {results.Count(r => r.Status == "skipped")} skipped · {results.Count(r => r.Status == "failed")} failed";

    /// <summary>A result's line in the report: label: status — detail.</summary>
    public static string Line(ChatBatchEntry entry) => $"{entry.Label}: {entry.Status}{(string.IsNullOrEmpty(entry.Detail) ? "" : $" — {entry.Detail}")}";

    /// <summary>Whether Claude wrote anything for the session that the history helper would plan (hasClaudeHistory).</summary>
    public static bool HasClaudeHistory(string home, string id)
    {
        bool Exists(params string[] parts) => Path.Exists(Path.Combine([home, .. parts]));
        string[] projects;
        try
        {
            projects = Directory.GetDirectories(Path.Combine(home, "projects")).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (IOException)
        {
            projects = [];
        }
        catch (UnauthorizedAccessException)
        {
            projects = [];
        }
        return projects.Any(p => Exists("projects", p, id + ".jsonl") || Exists("projects", p, id))
            || new[] { "file-history", "session-env", "tasks" }.Any(root => Exists(root, id))
            || Exists("debug", id + ".txt");
    }

    /// <summary>The archive root (History.root): the chosen one, REFRAME_ARCHIVE_ROOT, else the profile's archives
    /// folder; it must be an absolute folder off drive C.</summary>
    public static string ArchiveRoot(string? chosen, string? environment, string profileDir)
    {
        var root = !string.IsNullOrEmpty(chosen) ? chosen : !string.IsNullOrEmpty(environment) ? environment : Path.Combine(profileDir, "archives");
        if (!Settings.IsArchiveRoot(root)) throw new InvalidOperationException("Set REFRAME_ARCHIVE_ROOT to an absolute folder on a non-C drive.");
        return root;
    }
}
