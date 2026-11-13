namespace Reframe.Shell.Core;

/// <summary>A layout intent a window sends (src/shared/actions.ts LayoutAction).</summary>
public abstract record LayoutAction
{
    /// <summary>Moves a tile or a whole group (by node key) next to a target; a collapsed one comes out of the dock,
    /// to the end of the layout without a target.</summary>
    public sealed record NodeMove(string Key, DropTarget? Target) : LayoutAction;
    public sealed record TileCollapse(string SessionId) : LayoutAction;
    public sealed record TileExpand(string SessionId) : LayoutAction;
    public sealed record GroupCollapse(string GroupId) : LayoutAction;
    public sealed record GroupExpand(string GroupId) : LayoutAction;
    public sealed record GroupRename(string GroupId, string Name) : LayoutAction;
    public sealed record SplitResize(string SplitId, IReadOnlyList<double> Sizes) : LayoutAction;
    public sealed record WindowSidebar(bool Open) : LayoutAction;
    /// <summary>"single" (one thread fills the window) or "tiled" (the visible tiles kept in an even grid).</summary>
    public sealed record WindowViewMode(string Mode) : LayoutAction;
}

/// <summary>The pure reducer main applies to a window's state (src/shared/actions.ts). Never changes its input.</summary>
public static class LayoutActions
{
    /// <summary>A window's view mode; windows from before it was added are tiled.</summary>
    public static string ViewModeOf(WindowState win) => win.ViewMode ?? "tiled";

    /// <summary>A shallow copy of the window (its fields and unknown keys), for changing some of them.</summary>
    public static WindowState Copy(WindowState w, LayoutNode? layout, List<DockEntry> dock)
    {
        var copy = new WindowState
        {
            Id = w.Id, Bounds = w.Bounds, Maximized = w.Maximized, Fullscreen = w.Fullscreen, Hidden = w.Hidden,
            Layout = layout, Dock = dock, SidebarOpen = w.SidebarOpen, ActiveProject = w.ActiveProject, ViewMode = w.ViewMode,
        };
        foreach (var (key, value) in w.Extra) copy.Extra[key] = value;
        return copy;
    }

    static WindowState With(WindowState w, LayoutNode? layout) => Copy(w, layout, w.Dock);

    /// <summary>What a tiled window's grid depends on: its mode, project and set of tiles/groups (not order or sizes).</summary>
    public static string GridKey(WindowState win)
    {
        var units = Layout.LayoutUnits(win.Layout).Select(Layout.NodeKey).OrderBy(k => k, StringComparer.Ordinal);
        return string.Join("|", new[] { ViewModeOf(win), win.ActiveProject ?? "" }.Concat(units));
    }

    /// <summary>Keeps a tiled window in the even grid: arranged again when a tile or group came or went, or the project
    /// or the mode changed, since <paramref name="previousKey"/>. A pure resize or reorder is left as it is, as is a
    /// window seen for the first time (no previous key) and a single-window one.</summary>
    public static (WindowState Win, string Key) SettleGrid(WindowState win, string? previousKey)
    {
        var key = GridKey(win);
        if (previousKey is null || key == previousKey || ViewModeOf(win) == "single") return (win, key);
        return (With(win, Layout.GridLayout(Layout.LayoutUnits(win.Layout))), key);
    }

    public static List<string> DockSessions(DockEntry entry) => entry switch
    {
        DockTile t => [t.SessionId],
        DockGroup g => Layout.Leaves(g.Node),
        _ => [],
    };

    /// <summary>Every session in the window, visible or collapsed.</summary>
    public static List<string> WindowSessions(WindowState win) => [.. Layout.Leaves(win.Layout), .. win.Dock.SelectMany(DockSessions)];

    public static bool WindowHasSession(WindowState win, string sessionId) => WindowSessions(win).Contains(sessionId);

    /// <summary>Whether the session is collapsed into the dock (alone or with its group).</summary>
    public static bool IsCollapsed(WindowState win, string sessionId) => win.Dock.Any(d => DockSessions(d).Contains(sessionId));

    public static WindowState Apply(WindowState win, LayoutAction action)
    {
        switch (action)
        {
            case LayoutAction.NodeMove move:
                if (Layout.FindNode(win.Layout, move.Key) is null)
                {
                    var (rest, node) = TakeNode(win, move.Key);
                    return node is null ? win : PutNode(rest, node, move.Target);
                }
                return move.Target is { } target ? With(win, Layout.MoveNode(win.Layout, move.Key, target)) : win;
            case LayoutAction.TileCollapse c:
            {
                // The last tile of a group takes its group along, otherwise the group would be lost.
                var group = Layout.GroupOf(win.Layout, Layout.TileKey(c.SessionId));
                if (group is not null && Layout.Leaves(group).Count == 1) return Apply(win, new LayoutAction.GroupCollapse(group.Id));
                var (root, removed, hint) = Layout.RemoveTile(win.Layout, c.SessionId);
                if (removed is null) return win;
                return Copy(win, root, [.. win.Dock, new DockTile { SessionId = c.SessionId, Hint = hint }]);
            }
            case LayoutAction.TileExpand e:
            {
                var entry = win.Dock.OfType<DockTile>().FirstOrDefault(d => d.SessionId == e.SessionId);
                if (entry is null) return win;
                return Copy(win, Layout.RestoreNode(win.Layout, Layout.Tile(e.SessionId), entry.Hint), win.Dock.Where(d => d != entry).ToList());
            }
            case LayoutAction.GroupCollapse c:
            {
                var (root, removed, hint) = Layout.RemoveNode(win.Layout, c.GroupId);
                if (removed is not GroupNode group) return win;
                return Copy(win, root, [.. win.Dock, new DockGroup { Node = group, Hint = hint }]);
            }
            case LayoutAction.GroupExpand e:
            {
                var entry = win.Dock.OfType<DockGroup>().FirstOrDefault(d => d.Node.Id == e.GroupId);
                if (entry is null) return win;
                return Copy(win, Layout.RestoreNode(win.Layout, entry.Node, entry.Hint), win.Dock.Where(d => d != entry).ToList());
            }
            case LayoutAction.GroupRename r:
            {
                var name = r.Name.Trim();
                if (name.Length > 80) name = name[..80];
                return name.Length > 0 ? PatchGroup(win, r.GroupId, name: name) : win;
            }
            case LayoutAction.SplitResize r:
                return With(win, Layout.SetSplitSizes(win.Layout, r.SplitId, r.Sizes));
            case LayoutAction.WindowSidebar s:
            {
                var copy = With(win, win.Layout);
                copy.SidebarOpen = s.Open;
                return copy;
            }
            case LayoutAction.WindowViewMode m:
            {
                if (m.Mode is not ("single" or "tiled")) return win;
                var copy = With(win, win.Layout);
                copy.ViewMode = m.Mode;
                return copy;
            }
        }
        return win;
    }

    /// <summary>Changes a group's name or colour, whether it is visible or collapsed.</summary>
    public static WindowState PatchGroup(WindowState win, string groupId, string? name = null, string? color = null) =>
        Copy(win, Layout.UpdateGroup(win.Layout, groupId, name, color), win.Dock.Select(d =>
            d is DockGroup g && g.Node.Id == groupId ? WithNode(g, Layout.WithChild(g.Node, g.Node.Child, name, color)) : d).ToList());

    static DockGroup WithNode(DockGroup entry, GroupNode node)
    {
        var copy = new DockGroup { Node = node, Hint = entry.Hint };
        foreach (var (key, value) in entry.Extra) copy.Extra[key] = value;
        return copy;
    }

    /// <summary>Adds a new session's tile; with a target it lands next to that node, otherwise at the end.</summary>
    public static WindowState AddTile(WindowState win, string sessionId, DropTarget? target = null) =>
        With(win, Layout.InsertTile(win.Layout, sessionId, target));

    /// <summary>Removes a session from the window entirely (layout, collapsed tiles and collapsed groups).</summary>
    public static WindowState DropSession(WindowState win, string sessionId)
    {
        var dock = new List<DockEntry>();
        foreach (var entry in win.Dock)
        {
            if (entry is DockTile t)
            {
                if (t.SessionId != sessionId) dock.Add(entry);
                continue;
            }
            var g = (DockGroup)entry;
            if (Layout.RemoveTile(g.Node, sessionId).Root is GroupNode rest) dock.Add(WithNode(g, rest));
        }
        return Copy(win, Layout.RemoveTile(win.Layout, sessionId).Root, dock);
    }

    /// <summary>Takes a tile or group (by node key) out of a window: out of its layout, or out of the dock (a tile of a
    /// collapsed group comes out alone, and the group stays collapsed with the rest).</summary>
    public static (WindowState Win, LayoutNode? Node) TakeNode(WindowState win, string key)
    {
        var (root, removed, _) = Layout.RemoveNode(win.Layout, key);
        if (removed is not null) return (With(win, root), removed);
        foreach (var entry in win.Dock)
        {
            var others = win.Dock.Where(d => d != entry).ToList();
            if (entry is DockTile t)
            {
                if (Layout.TileKey(t.SessionId) == key) return (Copy(win, win.Layout, others), Layout.Tile(t.SessionId));
                continue;
            }
            var g = (DockGroup)entry;
            if (g.Node.Id == key) return (Copy(win, win.Layout, others), g.Node);
            var inGroup = Layout.RemoveNode(g.Node, key);
            if (inGroup.Removed is null) continue;
            var dock = inGroup.Root is GroupNode rest ? win.Dock.Select(d => d == entry ? WithNode(g, rest) : d).ToList() : others;
            return (Copy(win, win.Layout, dock), inGroup.Removed);
        }
        return (win, null);
    }

    /// <summary>Puts a tile or group into a window next to a target, or at the end without one.</summary>
    public static WindowState PutNode(WindowState win, LayoutNode node, DropTarget? target)
    {
        if (target is null || Layout.FindNode(win.Layout, target.Key) is null) return With(win, Layout.AppendNode(win.Layout, node));
        var resolved = Layout.ResolveTarget(win.Layout, node, target);
        return With(win, Layout.InsertNode(win.Layout, node, resolved.Key, resolved.Side));
    }

    /// <summary>Every group in the window, visible or collapsed.</summary>
    public static List<GroupNode> WindowGroups(WindowState win) =>
        [.. Layout.Groups(win.Layout), .. win.Dock.OfType<DockGroup>().Select(d => d.Node)];

    /// <summary>A window with neither tiles nor collapsed tiles.</summary>
    public static bool IsWindowEmpty(WindowState win) => win.Layout is null && win.Dock.Count == 0;

    /// <summary>The windows a start brings back: the ones with something in them, or the first when all are empty.</summary>
    public static List<WindowState> WindowsToRestore(IReadOnlyList<WindowState> windows)
    {
        var kept = windows.Where(w => !IsWindowEmpty(w)).ToList();
        return kept.Count > 0 ? kept : windows.Take(1).ToList();
    }
}

/// <summary>Single window mode (src/renderer/view-mode.ts): which thread fills the window as its tiles come and go.</summary>
public static class ViewModes
{
    /// <summary>The thread to show after the window's threads went from <paramref name="before"/> to
    /// <paramref name="after"/>: one that just came is shown; otherwise the selected one while it is there; when it
    /// closed (or left the window), none: the window stays blank until the user picks the next one (an improvement on
    /// view-mode.ts, which showed the one that took its place, where a second click on × could end that one too);
    /// else the first.</summary>
    public static string? NextShown(IReadOnlyList<string> before, IReadOnlyList<string> after, string? selected)
    {
        var added = after.Where(id => !before.Contains(id)).ToList();
        if (added.Count == 1 && before.Count > 0) return added[0];
        if (selected is not null && after.Contains(selected)) return selected;
        if (selected is not null && before.Contains(selected)) return null;
        return after.FirstOrDefault();
    }

    /// <summary>The thread <paramref name="step"/> places from <paramref name="current"/> among <paramref name="ids"/>, going round.</summary>
    public static string? Cycle(IReadOnlyList<string> ids, string? current, int step)
    {
        if (ids.Count == 0) return null;
        var index = current is null ? -1 : ids.ToList().IndexOf(current);
        if (index < 0) return ids[0];
        return ids[((index + step) % ids.Count + ids.Count) % ids.Count];
    }
}

/// <summary>Snapping separators to a grid over the whole layout (src/renderer/snap.ts), so that separators of different
/// splits line up. Positions along one axis, in pixels.</summary>
public static class Snap
{
    /// <summary>Grid lines across the layout.</summary>
    public const int GridSteps = 12;

    /// <summary>A split along its axis: where it starts, how long it is, how thick each separator is.</summary>
    public readonly record struct SplitAxis(double Start, double Length, double Separator);
    /// <summary>The grid along the same axis: the layout's start and length, divided into steps.</summary>
    public readonly record struct GridAxis(double Start, double Length, int Steps);

    /// <summary>The split's panel sizes (percentages) with every separator that moved since <paramref name="before"/>
    /// on the nearest grid line, unless that would push it past a neighbour.</summary>
    public static List<double> SnapSizes(IReadOnlyList<double> sizes, IReadOnlyList<double> before, SplitAxis split, GridAxis grid)
    {
        if (sizes.Count != before.Count || sizes.Count < 2) return [.. sizes];
        var available = split.Length - (sizes.Count - 1) * split.Separator;
        if (available <= 0 || grid.Length <= 0) return [.. sizes];
        var step = grid.Length / grid.Steps;
        static List<double> Prefix(IReadOnlyList<double> list)
        {
            var sum = 0.0;
            return list.Select(x => sum += x).ToList();
        }
        var now = Prefix(sizes);
        var was = Prefix(before);
        double Position(double share, int i) => split.Start + share / 100 * available + i * split.Separator + split.Separator / 2;
        double Share(double x, int i) => (x - split.Start - i * split.Separator - split.Separator / 2) / available * 100;

        var snapped = new List<double>(now);
        for (var i = 0; i < sizes.Count - 1; i++)
        {
            if (Math.Abs(now[i] - was[i]) < 0.01) continue;
            var line = grid.Start + Math.Round((Position(now[i], i) - grid.Start) / step, MidpointRounding.AwayFromZero) * step;
            var target = Share(line, i);
            var low = i > 0 ? snapped[i - 1] : 0;
            var high = now[i + 1];
            if (target > low && target < high) snapped[i] = target;
        }
        return snapped.Select((end, i) => end - (i > 0 ? snapped[i - 1] : 0)).ToList();
    }
}
