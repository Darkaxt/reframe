namespace Reframe.Shell.Core;

/// <summary>A conversation row of the sidebar, as SessionSidebar's renderChat shows it.</summary>
public sealed record ChatRow(
    string Key,
    string Section,
    string Label,
    /// <summary>The status dot's status; a running claude elsewhere shows the running mark instead.</summary>
    string Status,
    bool RunningMark,
    string? Provider,
    bool Defaulted,
    /// <summary>When it was last used, short ("now", "5m"); empty when not known.</summary>
    string Stamp,
    /// <summary>The second line: folder · distro · window n / ended / Running.</summary>
    string Meta,
    string Tooltip,
    ListItemRef Ref,
    SidebarChat Chat);

/// <summary>A project branch (folder workspace) of the sidebar.</summary>
public sealed record ProjectRow(
    string Id,
    string Name,
    string Cwd,
    int Count,
    ProjectActivity? Activity,
    string StateLabel,
    bool Expanded,
    bool Current,
    string Tooltip,
    IReadOnlyList<ChatRow> Chats,
    bool HasMore,
    IReadOnlyList<FolderLocation>? Locations = null);

/// <summary>The sidebar's sections (PINNED, PROJECTS, RECENTS) for one session list.</summary>
public sealed record SidebarSections(IReadOnlyList<ChatRow> Pinned, IReadOnlyList<ProjectRow> Projects, IReadOnlyList<ChatRow> Recents, bool HasMore)
{
    /// <summary>Archived chats (Restore chat), and the stopped WSL distributions (Start).</summary>
    public IReadOnlyList<ArchiveEntry> Archives { get; init; } = [];
    public IReadOnlyList<string> WslStopped { get; init; } = [];

    /// <summary>The rows in the order shown, each chat once (SessionSidebar's rows: pinned, open projects, recents),
    /// for a Shift+click range.</summary>
    public List<ChatRow> Ordered() =>
    [
        .. Pinned.Concat(Projects.Where(p => p.Expanded).SelectMany(p => p.Chats)).Concat(Recents)
            .GroupBy(r => r.Key).Select(g => g.First()),
    ];
}

/// <summary>What SessionSidebar.tsx renders from a session list (English texts of src/shared/i18n.ts).</summary>
public static class SidebarView
{
    public static readonly IReadOnlyDictionary<string, string> StatusLabels = new Dictionary<string, string>
    {
        ["starting"] = "Starting", ["idle"] = "Ready", ["busy"] = "Working", ["background"] = "Idle, background work running",
        ["waiting"] = "Needs you", ["ended"] = "Ended", ["error"] = "Error",
    };

    const string OpenHint = "Click to open here, or drag onto a tile. Shift/Ctrl+click selects several, right-click for more.";
    const string ShowHint = "Click to show it";

    static string FolderName(string cwd) => cwd.Split('\\', '/').LastOrDefault(p => p != "") ?? cwd;

    public static SidebarSections Build(SessionList list, double now, int limit = SessionListBuilder.Page, string? activeProject = null)
    {
        var catalogue = SidebarChats.Build(list);
        var activity = ProjectView.Activity(list.App.Select(a => (a.Cwd, a.Status)));
        var pinned = list.PinnedChats.Select(key => catalogue.FirstOrDefault(c => c.Key == key)).OfType<SidebarChat>()
            .Select(c => Row(c, "pinned", now)).ToList();
        var recents = catalogue.Take(limit).Select(c => Row(c, "recents", now)).ToList();
        var projects = list.FolderWorkspaces.Select(project =>
        {
            var busy = activity.GetValueOrDefault(project.Id);
            var state = busy is null ? "" : StatusLabels.GetValueOrDefault(busy.Status, busy.Status);
            var paths = string.Join("\n", (project.Locations ?? []).Select(l => l.Cwd).Distinct());
            if (paths == "") paths = project.Cwd;
            var section = "project:" + project.Id;
            // The project's chats among those listed (reframe asks for a page of the project's own when expanded).
            var chats = catalogue.Where(c => Workspaces.WorkspaceKey(c.Cwd) == project.Id).ToList();
            return new ProjectRow(project.Id, project.Name, project.Cwd, project.SessionIds.Count, busy, state,
                list.ExpandedProjects.Contains(project.Id), activeProject == project.Id,
                busy is null ? paths : $"{paths}\n{busy.Count} running · {state}",
                chats.Take(limit).Select(c => Row(c, section, now)).ToList(), chats.Count > limit, project.Locations);
        }).ToList();
        return new SidebarSections(pinned, projects, recents, list.HasMore || catalogue.Count > limit)
        {
            Archives = list.Archives, WslStopped = list.WslStopped,
        };
    }

    /// <summary>list.wslStopped's note beside a stopped distribution.</summary>
    public static string WslStoppedText(string distro) => $"{distro} (WSL) is not running: its sessions show up here once it runs.";

    /// <summary>onRowClick's selection: Shift extends from the anchor, Ctrl toggles, a plain click selects only the
    /// row (and opens it). Returns the new selection and anchor, and whether the row is opened.</summary>
    public static (List<string> Selected, string? Anchor, bool Open) Click(IReadOnlyList<string> order, IReadOnlyList<string> selected,
        string? anchor, string key, bool shift, bool ctrl)
    {
        var from = anchor is null ? -1 : order.ToList().IndexOf(anchor);
        if (shift && from >= 0)
        {
            var to = order.ToList().IndexOf(key);
            return ([.. order.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1)], anchor, false);
        }
        if (ctrl) return (selected.Contains(key) ? [.. selected.Where(k => k != key)] : [.. selected, key], key, false);
        return ([key], key, true);
    }

    public static ChatRow Row(SidebarChat chat, string section, double now)
    {
        var item = chat.Item;
        var managed = item.Kind == ListItemKind.App ? item : null;
        var stamp = chat.LastActiveAt != 0 ? RelativeTime.Short(chat.LastActiveAt, now) : "";
        var status = managed?.Elsewhere == true ? "Running"
            : managed is not null ? (managed.WindowNumber is { } n && managed.Status is not ("ended" or "error") ? $"window {n}" : "ended")
            : item.Kind == ListItemKind.Running ? "Running"
            : "ended";
        var meta = string.Join(" · ", new[] { section.StartsWith("project:", StringComparison.Ordinal) ? "" : FolderName(chat.Cwd), item.Distro ?? "", status }
            .Where(s => s != ""));
        var hint = managed?.WindowId is not null ? ShowHint : OpenHint;
        var tooltip = string.Join("\n", new[]
        {
            chat.Label, chat.Cwd, hint, chat.LastActiveAt != 0 ? $"Last used {RelativeTime.Exact(chat.LastActiveAt)}" : "",
        }.Where(s => s != ""));
        return new ChatRow(chat.Key, section, chat.Label, chat.Status, item.Kind == ListItemKind.Running, chat.Provider,
            chat.Defaulted, stamp, meta, tooltip, chat.Ref, chat);
    }
}
