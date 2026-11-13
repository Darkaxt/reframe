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
    bool HasMore);

/// <summary>The sidebar's sections (PINNED, PROJECTS, RECENTS) for one session list.</summary>
public sealed record SidebarSections(IReadOnlyList<ChatRow> Pinned, IReadOnlyList<ProjectRow> Projects, IReadOnlyList<ChatRow> Recents, bool HasMore);

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
                chats.Take(limit).Select(c => Row(c, section, now)).ToList(), chats.Count > limit);
        }).ToList();
        return new SidebarSections(pinned, projects, recents, list.HasMore || catalogue.Count > limit);
    }

    public static ChatRow Row(SidebarChat chat, string section, double now)
    {
        var item = chat.Item;
        var managed = item.Kind == ListItemKind.App ? item : null;
        var stamp = chat.LastActiveAt != 0 ? RelativeTime.Short(chat.LastActiveAt, now) : "";
        var status = managed?.Elsewhere == true ? "Running"
            : managed is not null ? (managed.WindowNumber is { } n ? $"window {n}" : "ended")
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
