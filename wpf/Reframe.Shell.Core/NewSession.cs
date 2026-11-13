using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>What the New Session dialog sends (src/shared/ipc.ts NewSessionRequest).</summary>
public sealed record NewSessionRequest(
    string Cwd,
    string Provider,
    string? Name = null,
    bool Worktree = false,
    string? WorktreeName = null,
    string? Model = null,
    /// <summary>null: Settings' default effort; "": the CLI's (deliberately no effort).</summary>
    string? Effort = null,
    string? PermissionMode = null,
    IReadOnlyList<string>? AddDirs = null,
    string? InitialPrompt = null);

/// <summary>The dialog's starting values for a provider (defaultsFor).</summary>
public sealed record DialogDefaults(string? Model, string? Effort, string? PermissionMode, IReadOnlyList<string> AddDirs);

/// <summary>
/// src/shared/new-session.ts, launch.ts and the session creation and restart of src/main/app.ts (createSession,
/// restartSession): what "New session" remembers per provider, the checks of its free-form fields, the record a new
/// session gets and how a restart changes it.
/// </summary>
public static partial class NewSession
{
    public static readonly IReadOnlyList<string> PermissionModes = ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"];
    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];
    public static readonly IReadOnlyList<string> ModelSuggestions = ["fable", "opus", "sonnet", "haiku"];
    public const int MaxAddDirs = 20;
    public const int MaxPrompt = 20_000;
    public const int MaxRecentFolders = 8;

    // Control characters break a command line; tabs and newlines are fine in a prompt.
    [GeneratedRegex(@"[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]")]
    private static partial Regex Control();

    public static string ProviderName(string? provider) =>
        provider == "chatgpt" ? "ChatGPT" : provider == "anthropic" ? "Anthropic" : "Provider not selected";

    static ProviderDefaults? Remembered(Preferences preferences, string provider) =>
        provider == "anthropic" ? preferences.ProviderDefaults?.Anthropic : preferences.ProviderDefaults?.Chatgpt;

    /// <summary>defaultsFor: what was used last for the provider, else Settings' defaults (the default model is the
    /// ChatGPT route's).</summary>
    public static DialogDefaults DefaultsFor(Preferences preferences, string provider)
    {
        if (Remembered(preferences, provider) is { } r) return new(r.Model, r.Effort, r.PermissionMode, r.AddDirs ?? []);
        return new(provider == "chatgpt" ? preferences.DefaultModel : null, preferences.DefaultEffort, preferences.DefaultPermissionMode, []);
    }

    /// <summary>parseAddDirs: one folder per line, trimmed, empty lines and repeats dropped.</summary>
    public static List<string> ParseAddDirs(string text) =>
        Regex.Split(text, "\r?\n").Select(l => l.Trim()).Distinct().Where(l => l != "").ToList();

    /// <summary>cleanAddDirs: absolute paths only (Windows or WSL), bounded; null when none is left.</summary>
    public static List<string>? CleanAddDirs(IEnumerable<string>? value)
    {
        if (value is null) return null;
        var dirs = value.Select(d => d.Trim())
            .Where(d => d.Length <= 1024 && !Control().IsMatch(d) && !Regex.IsMatch(d, "[\r\n\t]"))
            .Where(d => Regex.IsMatch(d, @"^[a-zA-Z]:[\\/]") || d.StartsWith(@"\\", StringComparison.Ordinal) || d.StartsWith('/'))
            .Distinct().Take(MaxAddDirs).ToList();
        return dirs.Count > 0 ? dirs : null;
    }

    /// <summary>cleanPrompt: bounded, no control characters except tab and line breaks.</summary>
    public static string? CleanPrompt(string? value)
    {
        var prompt = value?.Trim();
        return !string.IsNullOrEmpty(prompt) && prompt.Length <= MaxPrompt && !Control().IsMatch(prompt) ? prompt : null;
    }

    /// <summary>rememberedDefaults: only the choices that were set.</summary>
    public static ProviderDefaults RememberedDefaults(LaunchOptions launch) => new()
    {
        Model = string.IsNullOrEmpty(launch.Model) ? null : launch.Model,
        Effort = launch.Effort is { } e && EffortLevels.Contains(e) ? e : null,
        PermissionMode = launch.PermissionMode is { } p && PermissionModes.Contains(p) ? p : null,
        AddDirs = launch.AddDirs is { Count: > 0 } dirs ? dirs : null,
    };

    /// <summary>The folder's last name (the dialog's recent-folder chips).</summary>
    public static string FolderName(string folder) => folder.Trim().Split('\\', '/').LastOrDefault(p => p != "") ?? "";

    /// <summary>The recent folders the dialog offers for <paramref name="provider"/>: none inside the other provider's
    /// workspace root.</summary>
    public static List<string> ProviderRecent(IEnumerable<string> recent, IReadOnlyDictionary<string, string> roots, string provider)
    {
        var other = roots.GetValueOrDefault(provider == "chatgpt" ? "anthropic" : "chatgpt") ?? "";
        var otherKey = Workspaces.FolderKey(other);
        return recent.Where(folder =>
        {
            var key = Workspaces.FolderKey(folder);
            return other == "" || (key != otherKey && !key.StartsWith(otherKey + "/", StringComparison.Ordinal));
        }).ToList();
    }

    /// <summary>The workspace root of each provider (dialogDefaults' providerWorkspaceRoots).</summary>
    public static Dictionary<string, string> WorkspaceRoots(string documents) => new()
    {
        ["chatgpt"] = Path.Combine(documents, "ChatGPT"),
        ["anthropic"] = Path.Combine(documents, "Anthropic"),
    };

    /// <summary>createSession's record for a request (the folder and claude already checked).</summary>
    public static SessionRecord CreateRecord(NewSessionRequest request, Preferences preferences, string id, string claudeSessionId, double now)
    {
        var effort = request.Effort == "" ? null : EffortLevels.FirstOrDefault(l => l == (request.Effort ?? preferences.DefaultEffort));
        return new SessionRecord
        {
            Id = id,
            ClaudeSessionId = claudeSessionId,
            Cwd = request.Cwd.Trim(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            Launch = new LaunchOptions
            {
                Provider = StateValues.Providers.Contains(request.Provider) ? request.Provider : "chatgpt",
                Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
                Effort = effort,
                PermissionMode = PermissionModes.FirstOrDefault(m => m == request.PermissionMode),
                Worktree = request.Worktree ? true : null,
                WorktreeName = request.Worktree && !string.IsNullOrWhiteSpace(request.WorktreeName) ? request.WorktreeName.Trim() : null,
                AddDirs = CleanAddDirs(request.AddDirs),
                InitialPrompt = CleanPrompt(request.InitialPrompt),
            },
            Lifecycle = "open",
            CreatedAt = now,
            LastActiveAt = now,
        };
    }

    /// <summary>createSession's state change besides the record: the folder first among the recent ones (at most 8,
    /// compared case-insensitively), and the choices remembered for the provider.</summary>
    public static void Remember(AppState state, SessionRecord record)
    {
        state.Sessions[record.Id] = record;
        var cwd = record.Cwd;
        state.RecentFolders = [cwd, .. state.RecentFolders.Where(f => !string.Equals(f, cwd, StringComparison.OrdinalIgnoreCase))];
        if (state.RecentFolders.Count > MaxRecentFolders) state.RecentFolders.RemoveRange(MaxRecentFolders, state.RecentFolders.Count - MaxRecentFolders);
        var set = state.Preferences.ProviderDefaults ??= new ProviderDefaultsSet();
        var remembered = RememberedDefaults(record.Launch);
        if (record.Launch.Provider == "anthropic") set.Anthropic = remembered;
        else set.Chatgpt = remembered;
    }

    /// <summary>restartSession: open again; a fresh start is a new, unused conversation, last used now.</summary>
    public static void Restart(SessionRecord record, bool fresh, string freshClaudeSessionId, double now)
    {
        if (fresh)
        {
            record.ClaudeSessionId = freshClaudeSessionId;
            record.Used = false;
            record.LastActiveAt = now;
        }
        record.Lifecycle = "open";
    }

    /// <summary>The model field's datalist (MODEL_SUGGESTIONS) as Chromium filters it while typing: the suggestions
    /// that contain the text (any case); none when the text is exactly the only match.</summary>
    public static List<string> MatchingModels(string text)
    {
        var typed = text.Trim();
        var matches = ModelSuggestions.Where(m => m.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches is [var only] && string.Equals(only, typed, StringComparison.OrdinalIgnoreCase) ? [] : matches;
    }

    /// <summary>resumeSessions (src/main/app.ts start): of the open sessions, those in a window that the host does not
    /// run any more (after a reboot, say) are resumed; those the host still runs are only attached; open ones in no
    /// window are ended. Host sessions reframe's state does not know (<paramref name="synthetic"/>) are left alone.</summary>
    public static (List<string> Resume, List<string> Ended) AtStart(IEnumerable<SessionRecord> sessions, Func<string, bool> inWindow,
        Func<string, bool> hostRuns, ISet<string> synthetic)
    {
        var resume = new List<string>();
        var ended = new List<string>();
        foreach (var record in sessions)
        {
            if (record.Lifecycle != "open" || synthetic.Contains(record.Id)) continue;
            if (!inWindow(record.Id)) ended.Add(record.Id);
            else if (!hostRuns(record.Id)) resume.Add(record.Id);
        }
        return (resume, ended);
    }
}

/// <summary>The card over a tile whose session does not run (Tile.tsx .tile-overlay-card): its text and buttons.</summary>
public sealed record TileCard(string Text, string? Primary, bool StartFresh, string Close)
{
    /// <summary>Errors a start can end in (tile.error.*).</summary>
    public static readonly IReadOnlyList<string> Errors =
        ["resumeFailed", "transcriptMissing", "claudeNotFound", "spawnFailed", "folderMissing", "hostCrashed", "runningElsewhere", "olderHost"];

    public static TileCard For(string? error, string? wslDistro)
    {
        var text = error == "claudeNotFound" && wslDistro is not null
            ? Strings.T("error.claudeNotFoundInWsl", ("distro", wslDistro))
            : error is not null ? Strings.T($"tile.error.{error}") : Strings.T("tile.ended");
        if (error == "claudeNotFound" && wslDistro is null) text += " " + Strings.T("error.claudeOnlyInWsl");
        string? primary = error is "transcriptMissing" or "folderMissing" ? null
            : error is "runningElsewhere" or "olderHost" ? Strings.T("takeover.confirm") : Strings.T("tile.resume");
        // A fresh start is for a session whose history is the trouble, not for one its host lost.
        var fresh = error is not null && error is not ("folderMissing" or "hostCrashed" or "runningElsewhere" or "olderHost");
        return new TileCard(text, primary, fresh, Strings.T("tile.close"));
    }
}

/// <summary>
/// The <c>launch.spawnPlan</c> request (launch version 2, src/main/claude/spawn-plan.ts) the shell starts a session
/// with, and its answer.
/// </summary>
public static class SpawnPlans
{
    public sealed record Hooks(int Port, string Token);

    /// <summary>A spawnPlan answer: ok with the spawn, or the error the tile shows.</summary>
    public sealed record Plan(bool Ok, string? Error, string Mode, string File, JsonNode? Args, string Cwd, JsonObject Env,
        string? SettingsFile, string? SettingsContent, JsonObject Meta, IReadOnlyList<string> Warnings);

    public static JsonObject Input(SessionRecord record, string mode, string? freshClaudeSessionId, IReadOnlyDictionary<string, string> env,
        string claudeHome, string settingsDir, Hooks hooks, string? claudeBin, string? adoption, bool captureStatusLine, JsonObject? wsl)
    {
        var launch = new JsonObject();
        void Put(string key, JsonNode? value)
        {
            if (value is not null) launch[key] = value;
        }
        Put("provider", record.Launch.Provider);
        Put("model", record.Launch.Model);
        Put("effort", record.Launch.Effort);
        Put("permissionMode", record.Launch.PermissionMode);
        Put("worktree", record.Launch.Worktree);
        Put("worktreeName", record.Launch.WorktreeName);
        Put("worktreeCreated", record.Launch.WorktreeCreated);
        if (record.Launch.AddDirs is { Count: > 0 } dirs) launch["addDirs"] = new JsonArray([.. dirs.Select(d => (JsonNode?)d)]);
        Put("initialPrompt", record.Launch.InitialPrompt);
        var session = new JsonObject
        {
            ["id"] = record.Id, ["claudeSessionId"] = record.ClaudeSessionId, ["cwd"] = record.Cwd, ["launch"] = launch,
        };
        if (record.Name is not null) session["name"] = record.Name;
        if (record.HasClaudeName) session["claudeName"] = record.ClaudeName;
        if (record.Used is { } used) session["used"] = used;
        var envObject = new JsonObject();
        foreach (var (key, value) in env) envObject[key] = value;
        var input = new JsonObject
        {
            ["session"] = session, ["mode"] = mode, ["env"] = envObject, ["claudeHome"] = claudeHome, ["settingsDir"] = settingsDir,
            ["hooks"] = new JsonObject { ["port"] = hooks.Port, ["token"] = hooks.Token }, ["captureStatusLine"] = captureStatusLine,
        };
        if (freshClaudeSessionId is not null) input["freshClaudeSessionId"] = freshClaudeSessionId;
        if (!string.IsNullOrEmpty(claudeBin)) input["claudeBin"] = claudeBin;
        if (!string.IsNullOrEmpty(adoption)) input["adoption"] = adoption;
        if (wsl is not null) input["wsl"] = wsl;
        return input;
    }

    public static Plan Parse(JsonNode? answer)
    {
        var o = answer as JsonObject ?? throw new FormatException("spawnPlan answered no object");
        var warnings = (o["warnings"] as JsonArray ?? []).Select(w => (string?)w ?? "").ToList();
        if ((bool?)o["ok"] != true)
            return new Plan(false, (string?)o["error"] ?? "spawnFailed", "", "", null, "", [], null, null, [], warnings);
        return new Plan(true, null, (string?)o["mode"] ?? "new", (string?)o["file"] ?? "", o["args"]?.DeepClone(), (string?)o["cwd"] ?? "",
            o["env"]?.DeepClone() as JsonObject ?? [], (string?)o["settings"]?["file"], (string?)o["settings"]?["content"],
            o["meta"]?.DeepClone() as JsonObject ?? [], warnings);
    }

    /// <summary>The host's <c>spawn</c> request for a plan (120×32, as SessionManager starts them).</summary>
    public static JsonObject Spawn(string sessionId, int instance, Plan plan) => new()
    {
        ["type"] = "spawn", ["sessionId"] = sessionId, ["instance"] = instance, ["file"] = plan.File, ["args"] = plan.Args?.DeepClone(),
        ["cwd"] = plan.Cwd, ["env"] = plan.Env.DeepClone(), ["cols"] = 120, ["rows"] = 32, ["meta"] = plan.Meta.DeepClone(),
    };
}
