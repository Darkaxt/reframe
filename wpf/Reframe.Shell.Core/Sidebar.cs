using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

// The session list's pure rules, ported from src/shared (sidebar-chats.ts, folder-workspaces.ts, project-view.ts,
// last-used.ts, hidden-threads.ts) and src/renderer/time.ts, with their tests in Reframe.Shell.Tests/SidebarTests.cs.

public enum ListItemKind { App, Running, Found }

/// <summary>An entry of the session list (AppListItem | RunningListItem | FoundListItem in src/shared/ipc.ts): a session
/// reframe manages (<c>App</c>), a claude running outside reframe (<c>Running</c>), or a past one from Claude Code's
/// transcripts (<c>Found</c>). Fields that a kind does not have keep their defaults.</summary>
public sealed record ListItem
{
    public required ListItemKind Kind { get; init; }
    public required string ClaudeSessionId { get; init; }
    public required string Cwd { get; init; }
    public string? Distro { get; init; }
    public string? Provider { get; init; }
    /// <summary>App: reframe's session id.</summary>
    public string Id { get; init; } = "";
    /// <summary>App: its label (sessionLabel).</summary>
    public string Label { get; init; } = "";
    /// <summary>App: its status (starting, idle, background, busy, waiting, ended, error).</summary>
    public string Status { get; init; } = "ended";
    public string Lifecycle { get; init; } = "open";
    /// <summary>App: the window that shows it, and its 1-based number; null when in no window.</summary>
    public string? WindowId { get; init; }
    public int? WindowNumber { get; init; }
    /// <summary>App and Running: when it was last used (ms since 1970); 0 when not known.</summary>
    public double LastActiveAt { get; init; }
    /// <summary>App: not running in reframe, but resumed in another terminal.</summary>
    public bool Elsewhere { get; init; }
    /// <summary>Running: its process id.</summary>
    public int Pid { get; init; }
    /// <summary>Running: its name in Claude Code's registry.</summary>
    public string? Name { get; init; }
    /// <summary>Found: Claude Code's title.</summary>
    public string? Title { get; init; }
    /// <summary>Found: when its transcript was last written.</summary>
    public double ModifiedAt { get; init; }
}

/// <summary>SessionList (src/shared/ipc.ts): what main sends the session list.</summary>
public sealed record SessionList
{
    public List<string> PinnedChats { get; init; } = [];
    public List<ListItem> PinnedItems { get; init; } = [];
    public List<string> ExpandedProjects { get; init; } = [];
    public List<FolderWorkspace> FolderWorkspaces { get; init; } = [];
    public List<ListItem> App { get; init; } = [];
    public List<ListItem> Running { get; init; } = [];
    public List<ListItem> Found { get; init; } = [];
    public bool HasMore { get; init; }
    public List<string> WslStopped { get; init; } = [];
    /// <summary>Archived chats (state.archives), restorable from the list.</summary>
    public List<ArchiveEntry> Archives { get; init; } = [];
}

/// <summary>What a list entry stands for when it is opened (ListItemRef).</summary>
public sealed record ListItemRef(ListItemKind Kind, string? SessionId = null, int Pid = 0, string? ClaudeSessionId = null, string? Distro = null);

/// <summary>One conversation of the sidebar, whatever its source (SidebarChat).</summary>
public sealed record SidebarChat(string Key, ListItemRef Ref, string Label, string Cwd, string? Provider, bool Defaulted,
    double LastActiveAt, string Status, ListItem Item);

public static class SidebarChats
{
    static readonly JsonSerializerOptions Js = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Regex Uuid = new(@"^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$", RegexOptions.IgnoreCase);

    /// <summary>chatKey: a conversation's identity across sources, <c>["distro", "id"]</c> as JSON.</summary>
    public static string ChatKey(string id, string? distro = null) =>
        JsonSerializer.Serialize(new[] { string.IsNullOrEmpty(distro) ? "" : distro.ToLowerInvariant(), id }, Js);

    /// <summary>parseChatKey: null unless the key is two strings, the second a UUID.</summary>
    public static (string Id, string? Distro)? ParseChatKey(string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(key);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2) return null;
            if (root[0].ValueKind != JsonValueKind.String || root[1].ValueKind != JsonValueKind.String) return null;
            var id = root[1].GetString()!;
            if (!Uuid.IsMatch(id)) return null;
            var distro = root[0].GetString()!;
            return (id, distro == "" ? null : distro);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string FolderName(string cwd) => cwd.Split('\\', '/').LastOrDefault(p => p != "") ?? cwd;

    /// <summary>sidebarChats: every source's conversations once (the first source wins), newest first.</summary>
    public static List<SidebarChat> Build(SessionList list)
    {
        var result = new Dictionary<string, SidebarChat>();
        foreach (var item in list.App.Concat(list.Running).Concat(list.Found).Concat(list.PinnedItems))
        {
            var key = ChatKey(item.ClaudeSessionId, item.Distro);
            if (result.ContainsKey(key)) continue;
            var folder = FolderName(item.Cwd);
            var reference = item.Kind switch
            {
                ListItemKind.App => new ListItemRef(ListItemKind.App, SessionId: item.Id),
                ListItemKind.Running => new ListItemRef(ListItemKind.Running, Pid: item.Pid, ClaudeSessionId: item.ClaudeSessionId, Distro: item.Distro),
                _ => new ListItemRef(ListItemKind.Found, ClaudeSessionId: item.ClaudeSessionId, Distro: item.Distro),
            };
            var label = item.Kind switch
            {
                ListItemKind.App => item.Label,
                ListItemKind.Running => string.IsNullOrEmpty(item.Name) ? folder : item.Name,
                _ => string.IsNullOrEmpty(item.Title) ? folder : item.Title,
            };
            result[key] = new SidebarChat(key, reference, label, item.Cwd,
                item.Provider ?? (item.Kind == ListItemKind.Found ? "chatgpt" : null),
                item.Kind == ListItemKind.Found && item.Provider is null,
                item.Kind == ListItemKind.Found ? item.ModifiedAt : item.LastActiveAt,
                item.Kind switch { ListItemKind.App => item.Status, ListItemKind.Running => "idle", _ => "ended" },
                item);
        }
        return result.Values.OrderByDescending(c => c.LastActiveAt).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
    }
}

/// <summary>src/shared/folder-workspaces.ts: projects are folders with the same last name.</summary>
public static class Workspaces
{
    static readonly Regex WindowsPath = new(@"^[a-z]:[\\/]|^\\\\", RegexOptions.IgnoreCase);

    /// <summary>folderKey: lexical canonical form (Windows paths case-insensitive).</summary>
    public static string FolderKey(string folder)
    {
        var windows = WindowsPath.IsMatch(folder);
        var normalized = folder.Replace('\\', '/').TrimEnd('/');
        var parts = new List<string>();
        foreach (var part in normalized.Split('/'))
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 1) parts.RemoveAt(parts.Count - 1);
            else parts.Add(part);
        }
        var key = string.Join('/', parts);
        if (key == "") key = "/";
        return windows ? key.ToLowerInvariant() : key;
    }

    /// <summary>workspaceKey: <c>project:</c> and the folder's last name, lower case.</summary>
    public static string WorkspaceKey(string folder)
    {
        var key = FolderKey(folder);
        var last = key.Split('/').LastOrDefault(p => p != "");
        return "project:" + (last is null ? key : last.ToLowerInvariant());
    }

    public static string ExpandedWorkspaceKey(string key) =>
        key.StartsWith("project:", StringComparison.Ordinal) ? key : WorkspaceKey(Regex.Replace(key, "^(chatgpt|anthropic):", ""));

    /// <summary>folderWorkspaces: the chats grouped by project, each location (folder and provider) kept; by name.</summary>
    public static List<FolderWorkspace> Build(IEnumerable<(string Cwd, string Id, string? Provider)> chats)
    {
        var result = new Dictionary<string, FolderWorkspace>();
        var order = new List<FolderWorkspace>();
        foreach (var (cwd, id, provider0) in chats)
        {
            var provider = provider0 ?? "chatgpt";
            var key = WorkspaceKey(cwd);
            if (!result.TryGetValue(key, out var group))
            {
                group = new FolderWorkspace
                {
                    Id = key, Cwd = cwd, Name = cwd.Split('\\', '/').LastOrDefault(p => p != "") ?? cwd, Locations = [],
                };
                result[key] = group;
                order.Add(group);
            }
            if (!group.SessionIds.Contains(id)) group.SessionIds.Add(id);
            var location = group.Locations!.FirstOrDefault(l => FolderKey(l.Cwd) == FolderKey(cwd) && l.Provider == provider);
            if (location is null)
            {
                location = new FolderLocation { Cwd = cwd, Provider = provider };
                group.Locations!.Add(location);
            }
            if (!location.SessionIds.Contains(id)) location.SessionIds.Add(id);
        }
        // localeCompare (stable, as Array.prototype.sort is).
        return order.OrderBy(g => g.Name, StringComparer.Create(CultureInfo.InvariantCulture, false)).ToList();
    }

    /// <summary>rebaseFolder: a folder at or inside <paramref name="from"/> moved under <paramref name="to"/>.</summary>
    public static string RebaseFolder(string cwd, string from, string to)
    {
        if (cwd.Trim() == "") return to;
        if (from.Trim() == "" || to.Trim() == "") return cwd;
        var root = FolderKey(from);
        for (var i = 1; i <= cwd.Length; i++)
        {
            if (i < cwd.Length && cwd[i] != '\\' && cwd[i] != '/') continue;
            if (FolderKey(cwd[..i]) != root) continue;
            var rest = cwd[i..].TrimStart('\\', '/');
            if (rest == "") return to;
            var separator = to.EndsWith('\\') || to.EndsWith('/') ? "" : to.Contains('/') && !to.Contains('\\') ? "/" : "\\";
            return to + separator + rest;
        }
        return cwd;
    }
}

/// <summary>A project's activity (ProjectActivity): how many of its sessions run, and the most urgent status.</summary>
public sealed record ProjectActivity(int Count, string Status);

public static class ProjectView
{
    static readonly string[] Attention = ["waiting", "error", "busy", "background", "idle"];
    static readonly HashSet<string> Active = ["starting", "idle", "background", "busy", "waiting", "error"];

    /// <summary>projectActivity: per project key; starting counts as busy.</summary>
    public static Dictionary<string, ProjectActivity> Activity(IEnumerable<(string Cwd, string Status)> sessions)
    {
        var statuses = new Dictionary<string, List<string>>();
        var order = new List<string>();
        foreach (var (cwd, status) in sessions)
        {
            if (!Active.Contains(status)) continue;
            var key = Workspaces.WorkspaceKey(cwd);
            if (!statuses.TryGetValue(key, out var list))
            {
                statuses[key] = list = [];
                order.Add(key);
            }
            list.Add(status);
        }
        var result = new Dictionary<string, ProjectActivity>();
        foreach (var key in order)
        {
            var shown = statuses[key].Select(s => s == "starting" ? "busy" : s).ToList();
            result[key] = new ProjectActivity(statuses[key].Count, Attention.FirstOrDefault(shown.Contains) ?? "idle");
        }
        return result;
    }
}

public static class LastUsed
{
    /// <summary>countsAsUse: a prompt (busy), Claude done with it (busy → anything), or Claude asking (waiting).</summary>
    public static bool CountsAsUse(string before, string after) =>
        before != after && (after == "busy" || after == "waiting" || before == "busy");
}

public static class HiddenThreads
{
    public static HashSet<string> HiddenIds(IEnumerable<HiddenThread>? hidden) =>
        (hidden ?? []).Select(h => h.ClaudeSessionId).ToHashSet();

    /// <summary>hideThread: added, or its entry replaced (it keeps the newest label and time), at the end.</summary>
    public static List<HiddenThread> Hide(IEnumerable<HiddenThread>? hidden, HiddenThread thread) =>
        [.. (hidden ?? []).Where(h => h.ClaudeSessionId != thread.ClaudeSessionId), thread];

    /// <summary>restoreThreads: without the given ones; <paramref name="ids"/> null restores all.</summary>
    public static List<HiddenThread> Restore(IEnumerable<HiddenThread>? hidden, IReadOnlyCollection<string>? ids) =>
        ids is null ? [] : (hidden ?? []).Where(h => !ids.Contains(h.ClaudeSessionId)).ToList();

    public static List<T> WithoutHidden<T>(IEnumerable<T> items, ISet<string> hidden, Func<T, string> idOf) =>
        hidden.Count == 0 ? items.ToList() : items.Where(i => !hidden.Contains(idOf(i))).ToList();
}

/// <summary>src/renderer/time.ts: when a session was last used, short ("5m", "2h", "3d", then the date).</summary>
public static class RelativeTime
{
    const double Minute = 60_000, Hour = 60 * Minute, Day = 24 * Hour;

    public static DateTime Local(double time) => DateTimeOffset.FromUnixTimeMilliseconds((long)time).LocalDateTime;

    public static string Short(double time, double now, string language = "en")
    {
        var de = language == "de";
        var past = Math.Max(0, now - time);
        if (past < Minute) return de ? "jetzt" : "now";
        if (past < Hour) return de ? $"{Math.Floor(past / Minute)} Min." : $"{Math.Floor(past / Minute)}m";
        if (past < Day) return de ? $"{Math.Floor(past / Hour)} Std." : $"{Math.Floor(past / Hour)}h";
        if (past < 7 * Day) return de ? $"{Math.Floor(past / Day)} Tg." : $"{Math.Floor(past / Day)}d";
        var date = Local(time);
        var culture = CultureInfo.GetCultureInfo(de ? "de-DE" : "en-US");
        var thisYear = date.Year == Local(now).Year;
        return thisYear
            ? date.ToString(de ? "d. MMM" : "MMM d", culture)
            : date.ToString(de ? "MMM yyyy" : "MMM yyyy", culture);
    }

    /// <summary>exactTime, for the tooltip.</summary>
    public static string Exact(double time, string language = "en") =>
        Local(time).ToString(language == "de" ? "dd.MM.yyyy, HH:mm" : "MMM d, yyyy, h:mm tt", CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"));

    /// <summary>homeRelative (src/main/paths.ts, on Windows): a folder under the home folder as <c>~\rest</c>.</summary>
    public static string HomeRelative(string folder, string home)
    {
        var root = home.TrimEnd('\\', '/');
        if (root == "") return folder;
        if (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)) return "~";
        var next = folder.Length > root.Length ? folder[root.Length] : '\0';
        return next is '/' or '\\' && string.Equals(folder[..root.Length], root, StringComparison.OrdinalIgnoreCase)
            ? "~" + folder[root.Length..]
            : folder;
    }
}
