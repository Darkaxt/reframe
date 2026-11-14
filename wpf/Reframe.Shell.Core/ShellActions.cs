namespace Reframe.Shell.Core;

/// <summary>
/// The state changes of the session list's and tiles' actions, as src/main/app.ts makes them: rename
/// (renameSession), pin/unpin (chatActions), hide and restore (hideFromList, restoreHiddenThreads), end (endSession)
/// and remove (removeSession). Each changes the given state in place and says whether anything changed; the caller
/// saves it.
/// </summary>
public static class ShellActions
{
    /// <summary>renameSession: the name trimmed to 100 characters (empty: the automatic name again). Claude Code keeps
    /// the name it was started with, so the first rename records it as claudeName.</summary>
    public static bool Rename(AppState state, string sessionId, string name)
    {
        if (!state.Sessions.TryGetValue(sessionId, out var record)) return false;
        var trimmed = name.Trim();
        if (trimmed.Length > 100) trimmed = trimmed[..100];
        if (!record.HasClaudeName)
        {
            record.ClaudeName = record.Name;
            record.HasClaudeName = true;
        }
        record.Name = trimmed.Length > 0 ? trimmed : null;
        return true;
    }

    public static bool IsPinned(AppState state, string chatKey) => state.PinnedChats?.Contains(chatKey) == true;

    /// <summary>Pin chat / Unpin chat: the chat key added at the end (once) or taken out.</summary>
    public static bool TogglePin(AppState state, string chatKey)
    {
        var pinned = state.PinnedChats ?? [];
        state.PinnedChats = pinned.Contains(chatKey)
            ? pinned.Where(k => k != chatKey).ToList()
            : [.. pinned.Append(chatKey).Distinct()];
        return true;
    }

    /// <summary>hideFromList after the question: the thread added to (or replaced in) the hidden threads. A session
    /// reframe runs must be ended first (<paramref name="busy"/>): then nothing changes.</summary>
    public static bool Hide(AppState state, HiddenThread thread, bool busy = false)
    {
        if (busy) return false;
        state.HiddenThreads = HiddenThreads.Hide(state.HiddenThreads, thread);
        return true;
    }

    /// <summary>The takeover popup leaves these sessions alone from now on (one entry per session, the newest kept).</summary>
    public static bool Exclude(AppState state, IReadOnlyCollection<TakeoverExclusion> sessions)
    {
        if (sessions.Count == 0) return false;
        var ids = sessions.Select(s => s.ClaudeSessionId).ToHashSet();
        state.TakeoverExclusions = [.. (state.TakeoverExclusions ?? []).Where(x => !ids.Contains(x.ClaudeSessionId)), .. sessions];
        return true;
    }

    /// <summary>The given sessions' exclusions (null: all) removed: the popup offers them again.</summary>
    public static bool Unexclude(AppState state, IReadOnlyCollection<string>? ids)
    {
        var current = state.TakeoverExclusions ?? [];
        var next = ids is null ? [] : current.Where(x => !ids.Contains(x.ClaudeSessionId)).ToList();
        if (next.Count == current.Count) return false;
        state.TakeoverExclusions = next;
        return true;
    }

    /// <summary>restoreHiddenThreads: the given threads (null: all) visible again.</summary>
    public static bool Restore(AppState state, IReadOnlyCollection<string>? ids)
    {
        var next = HiddenThreads.Restore(state.HiddenThreads, ids);
        if (next.Count == state.HiddenThreads.Count) return false;
        state.HiddenThreads = next;
        return true;
    }

    /// <summary>endSession: the record ended and the session taken out of every window's layout and dock (the
    /// caller asks the host to dispose it).</summary>
    public static bool End(AppState state, string sessionId)
    {
        if (!state.Sessions.TryGetValue(sessionId, out var record)) return false;
        record.Lifecycle = "ended";
        foreach (var window in state.Windows) DropSession(window, sessionId);
        return true;
    }

    /// <summary>removeSession: forgets an ended session that is in no window (Claude Code's transcript stays).</summary>
    public static bool Remove(AppState state, string sessionId)
    {
        if (!state.Sessions.ContainsKey(sessionId) || state.Windows.Any(w => Contains(w, sessionId))) return false;
        return state.Sessions.Remove(sessionId);
    }

    static bool Contains(WindowState window, string sessionId) =>
        Tiles(window.Layout).Contains(sessionId) || window.Dock.Any(d => d switch
        {
            DockTile t => t.SessionId == sessionId,
            DockGroup g => Tiles(g.Node).Contains(sessionId),
            _ => false,
        });

    static IEnumerable<string> Tiles(LayoutNode? node) => node switch
    {
        TileNode t => [t.SessionId],
        GroupNode g => Tiles(g.Child),
        SplitNode s => s.Children.SelectMany(Tiles),
        _ => [],
    };

    /// <summary>dropSession (src/shared/actions.ts): out of the layout (removeTile) and the dock.</summary>
    public static void DropSession(WindowState window, string sessionId)
    {
        var dock = new List<DockEntry>();
        foreach (var entry in window.Dock)
        {
            if (entry is DockTile tile)
            {
                if (tile.SessionId != sessionId) dock.Add(entry);
                continue;
            }
            if (entry is DockGroup group && RemoveTile(group.Node, sessionId) is GroupNode rest)
            {
                group.Node = rest;
                dock.Add(group);
            }
        }
        window.Dock = dock;
        window.Layout = RemoveTile(window.Layout, sessionId);
    }

    /// <summary>removeTile: the tree without the tile (an emptied group or split goes, then normalize).</summary>
    public static LayoutNode? RemoveTile(LayoutNode? root, string sessionId)
    {
        if (root is null || root is TileNode t0 && t0.SessionId == sessionId) return null;
        LayoutNode? Walk(LayoutNode node)
        {
            switch (node)
            {
                case TileNode t:
                    return t.SessionId == sessionId ? null : t;
                case GroupNode g:
                    var child = Walk(g.Child);
                    if (child is null) return null;
                    g.Child = child;
                    return g;
                case SplitNode s:
                    var children = new List<LayoutNode>();
                    var sizes = new List<double>();
                    for (var i = 0; i < s.Children.Count; i++)
                        if (Walk(s.Children[i]) is { } next)
                        {
                            children.Add(next);
                            sizes.Add(i < s.Sizes.Count ? s.Sizes[i] : 0);
                        }
                    if (children.Count == 0) return null;
                    s.Children = children;
                    s.Sizes = sizes;
                    return s;
                default:
                    return node;
            }
        }
        return Walk(root) is { } rest ? Normalize(rest) : null;
    }

    static List<double> ScaleTo100(List<double> sizes)
    {
        var total = sizes.Sum();
        if (sizes.Count == 0) return [];
        return !(total > 0) ? sizes.Select(_ => 100.0 / sizes.Count).ToList() : sizes.Select(s => s / total * 100).ToList();
    }

    /// <summary>normalize (src/shared/layout.ts): nested splits of the same direction merged, one-child splits gone.</summary>
    public static LayoutNode Normalize(LayoutNode node)
    {
        if (node is GroupNode g)
        {
            g.Child = Normalize(g.Child);
            return g;
        }
        if (node is not SplitNode s) return node;
        var sizes = s.Sizes.Count == s.Children.Count ? s.Sizes : s.Children.Select(_ => 1.0).ToList();
        var children = new List<LayoutNode>();
        var childSizes = new List<double>();
        for (var i = 0; i < s.Children.Count; i++)
        {
            var child = Normalize(s.Children[i]);
            var size = sizes[i];
            if (child is SplitNode inner && inner.Dir == s.Dir)
            {
                var scaled = ScaleTo100(inner.Sizes);
                for (var j = 0; j < inner.Children.Count; j++)
                {
                    children.Add(inner.Children[j]);
                    childSizes.Add(size * (j < scaled.Count ? scaled[j] : 0) / 100);
                }
            }
            else
            {
                children.Add(child);
                childSizes.Add(size);
            }
        }
        if (children.Count == 1) return children[0];
        s.Children = children;
        s.Sizes = ScaleTo100(childSizes);
        return s;
    }
}
