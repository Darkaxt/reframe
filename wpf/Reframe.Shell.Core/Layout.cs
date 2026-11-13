namespace Reframe.Shell.Core;

/// <summary>Where a tile is dropped: next to the node with key <see cref="Key"/> (see <see cref="Layout.NodeKey"/>),
/// on <see cref="Side"/> ("left", "right", "top" or "bottom").</summary>
public sealed record DropTarget(string Key, string Side);

/// <summary>What <see cref="Layout.RemoveNode"/> returns: the remaining tree, the removed node and where it sat.</summary>
public sealed record Removal(LayoutNode? Root, LayoutNode? Removed, RestoreHint? Hint);

/// <summary>
/// Pure operations on a window's split layout, a port of src/shared/layout.ts. A layout is a tree of splits (row =
/// side by side, col = stacked), groups (a named, coloured frame around a subtree) and tiles (one session each). Every
/// operation returns a new, normalized tree and never changes its input: no split has fewer than two children, nested
/// splits in the same direction are flattened, and split sizes are percentages that sum to 100.
/// </summary>
public static class Layout
{
    static string NewId() => Guid.NewGuid().ToString();

    public static string TileKey(string sessionId) => "tile:" + sessionId;

    public static string NodeKey(LayoutNode node) => node switch
    {
        TileNode t => TileKey(t.SessionId),
        SplitNode s => s.Id,
        GroupNode g => g.Id,
        _ => "",
    };

    public static TileNode Tile(string sessionId) => new() { SessionId = sessionId };

    public static List<string> Leaves(LayoutNode? node) => node switch
    {
        null => [],
        TileNode t => [t.SessionId],
        GroupNode g => Leaves(g.Child),
        SplitNode s => s.Children.SelectMany(Leaves).ToList(),
        _ => [],
    };

    public static LayoutNode? FindNode(LayoutNode? node, string key)
    {
        if (node is null) return null;
        if (NodeKey(node) == key) return node;
        if (node is GroupNode g) return FindNode(g.Child, key);
        if (node is SplitNode s)
            foreach (var child in s.Children)
                if (FindNode(child, key) is { } found) return found;
        return null;
    }

    static string SideDir(string side) => side is "left" or "right" ? "row" : "col";
    static bool SideFirst(string side) => side is "left" or "top";

    public static List<double> ScaleTo100(IReadOnlyList<double> sizes)
    {
        if (sizes.Count == 0) return [];
        var total = sizes.Sum();
        if (!(total > 0)) return sizes.Select(_ => 100.0 / sizes.Count).ToList();
        return sizes.Select(s => s / total * 100).ToList();
    }

    static double At(IReadOnlyList<double> list, int i) => i >= 0 && i < list.Count ? list[i] : 0;

    static T Keep<T>(T copy, StateObject from) where T : StateObject
    {
        foreach (var (key, value) in from.Extra) copy.Extra[key] = value;
        return copy;
    }

    /// <summary>A copy of <paramref name="from"/> with other children and sizes.</summary>
    internal static SplitNode WithChildren(SplitNode from, List<LayoutNode> children, List<double> sizes) =>
        Keep(new SplitNode { Id = from.Id, Dir = from.Dir, Children = children, Sizes = sizes }, from);

    /// <summary>A copy of <paramref name="from"/> with another child, name or colour.</summary>
    internal static GroupNode WithChild(GroupNode from, LayoutNode child, string? name = null, string? color = null) =>
        Keep(new GroupNode { Id = from.Id, Name = name ?? from.Name, Color = color ?? from.Color, Child = child }, from);

    static SplitNode NewSplit(string dir, List<LayoutNode> children, List<double> sizes, Func<string>? newId = null) =>
        new() { Id = (newId ?? NewId)(), Dir = dir, Children = children, Sizes = sizes };

    public static LayoutNode Normalize(LayoutNode node)
    {
        if (node is TileNode) return node;
        if (node is GroupNode g) return WithChild(g, Normalize(g.Child));
        var split = (SplitNode)node;
        var sizes = split.Sizes.Count == split.Children.Count ? split.Sizes : split.Children.Select(_ => 1.0).ToList();
        var children = new List<LayoutNode>();
        var childSizes = new List<double>();
        for (var i = 0; i < split.Children.Count; i++)
        {
            var child = Normalize(split.Children[i]);
            var size = At(sizes, i);
            if (child is SplitNode inner && inner.Dir == split.Dir)
            {
                var scaled = ScaleTo100(inner.Sizes);
                for (var j = 0; j < inner.Children.Count; j++)
                {
                    children.Add(inner.Children[j]);
                    childSizes.Add(size * At(scaled, j) / 100);
                }
            }
            else
            {
                children.Add(child);
                childSizes.Add(size);
            }
        }
        if (children.Count == 1) return children[0];
        return WithChildren(split, children, ScaleTo100(childSizes));
    }

    static LayoutNode ReplaceNode(LayoutNode node, string key, Func<LayoutNode, LayoutNode> replace)
    {
        if (NodeKey(node) == key) return replace(node);
        return node switch
        {
            GroupNode g => WithChild(g, ReplaceNode(g.Child, key, replace)),
            SplitNode s => WithChildren(s, s.Children.Select(c => ReplaceNode(c, key, replace)).ToList(), [.. s.Sizes]),
            _ => node,
        };
    }

    /// <summary>Puts <paramref name="node"/> next to the node with key <paramref name="anchor"/> on
    /// <paramref name="side"/>; <paramref name="ratio"/> is the new node's share (0..1).</summary>
    public static LayoutNode InsertNode(LayoutNode? root, LayoutNode node, string? anchor = null, string side = "right", double ratio = 0.5)
    {
        if (root is null) return node;
        var r = Math.Min(0.9, Math.Max(0.1, ratio));
        LayoutNode Wrap(LayoutNode existing)
        {
            var first = SideFirst(side);
            return NewSplit(SideDir(side), first ? [node, existing] : [existing, node],
                first ? [r * 100, (1 - r) * 100] : [(1 - r) * 100, r * 100]);
        }
        if (anchor is not null && FindNode(root, anchor) is not null) return Normalize(ReplaceNode(root, anchor, Wrap));
        return AppendNode(root, node);
    }

    /// <summary>Adds a node where new tiles go when no place is given: the first fills the window, the second comes to
    /// its right, every further one at the bottom of the column with the fewest tiles (the right one when even); a
    /// group in the chosen column gets the node below it, not into it.</summary>
    public static LayoutNode AppendNode(LayoutNode? root, LayoutNode node)
    {
        if (root is null) return node;
        if (root is SplitNode { Dir: "row" } row)
        {
            static int Count(LayoutNode column) => column is SplitNode { Dir: "col" } c ? c.Children.Count : 1;
            var target = row.Children.Count - 1;
            for (var i = 0; i < row.Children.Count; i++)
                if (Count(row.Children[i]) < Count(row.Children[target])) target = i;
            var children = row.Children.Select((column, i) => i == target ? StackBelow(column, node) : column).ToList();
            return Normalize(WithChildren(row, children, [.. row.Sizes]));
        }
        return Normalize(NewSplit("row", [root, node], [50, 50]));
    }

    static LayoutNode StackBelow(LayoutNode column, LayoutNode node)
    {
        if (column is SplitNode { Dir: "col" } col)
        {
            var n = col.Children.Count;
            var sizes = ScaleTo100(col.Sizes).Select(s => s * n / (n + 1)).Append(100.0 / (n + 1)).ToList();
            return Normalize(WithChildren(col, [.. col.Children, node], sizes));
        }
        return NewSplit("col", [column, node], [50, 50]);
    }

    public static LayoutNode InsertTile(LayoutNode? root, string sessionId, DropTarget? target = null) =>
        InsertNode(root, Tile(sessionId), target?.Key, target?.Side ?? "right");

    /// <summary>Removes the node with <paramref name="key"/>; returns the remaining tree plus a hint for putting it back.</summary>
    public static Removal RemoveNode(LayoutNode? root, string key)
    {
        if (root is null) return new(null, null, null);
        if (NodeKey(root) == key) return new(null, root, null);
        LayoutNode? removed = null;
        RestoreHint? hint = null;

        LayoutNode? Walk(LayoutNode node)
        {
            if (node is TileNode) return node;
            if (node is GroupNode g)
            {
                if (NodeKey(g.Child) == key)
                {
                    removed = g.Child;
                    return null; // an empty group disappears
                }
                var inner = Walk(g.Child);
                return inner is null ? null : WithChild(g, inner);
            }
            var s = (SplitNode)node;
            var index = s.Children.FindIndex(c => NodeKey(c) == key);
            if (index >= 0)
            {
                removed = s.Children[index];
                var neighborIndex = index > 0 ? index - 1 : index + 1;
                var neighbor = s.Children[neighborIndex];
                var own = At(s.Sizes, index);
                var other = At(s.Sizes, neighborIndex);
                var before = neighborIndex < index;
                var total = s.Sizes.Sum();
                hint = new RestoreHint
                {
                    Anchor = NodeKey(neighbor),
                    Side = s.Dir == "row" ? (before ? "right" : "left") : before ? "bottom" : "top",
                    Ratio = own + other > 0 ? own / (own + other) : 0.5,
                    Share = total > 0 ? own / total : null,
                };
                return WithChildren(s, s.Children.Where((_, i) => i != index).ToList(), s.Sizes.Where((_, i) => i != index).ToList());
            }
            var children = new List<LayoutNode>();
            var sizes = new List<double>();
            for (var i = 0; i < s.Children.Count; i++)
                if (Walk(s.Children[i]) is { } next)
                {
                    children.Add(next);
                    sizes.Add(At(s.Sizes, i));
                }
            return children.Count > 0 ? WithChildren(s, children, sizes) : null;
        }

        var rest = Walk(root);
        return new(rest is null ? null : Normalize(rest), removed, hint);
    }

    public static Removal RemoveTile(LayoutNode? root, string sessionId) => RemoveNode(root, TileKey(sessionId));

    /// <summary>Puts a node back where a <see cref="RestoreHint"/> says; appends it if the anchor is gone.</summary>
    public static LayoutNode RestoreNode(LayoutNode? root, LayoutNode node, RestoreHint? hint)
    {
        if (hint is null || root is null || FindNode(root, hint.Anchor) is null) return AppendNode(root, node);
        var restored = InsertNode(root, node, hint.Anchor, hint.Side, hint.Ratio);
        var parent = FindParentSplit(restored, NodeKey(node));
        if (parent is null || hint.Share is not { } share || parent.Dir != SideDir(hint.Side) || !(share > 0 && share < 1)) return restored;
        // Take the remembered share back from all siblings, as they all grew when the tile left. Insertion carved the
        // tile out of the anchor's slot, so the anchor gets that space back before scaling.
        var index = parent.Children.FindIndex(c => NodeKey(c) == NodeKey(node));
        var anchorIndex = parent.Children.FindIndex(c => NodeKey(c) == hint.Anchor);
        var before = parent.Sizes.Select((s, i) => i == anchorIndex ? s + At(parent.Sizes, index) : s).ToList();
        var others = ScaleTo100(before.Where((_, i) => i != index).ToList());
        var sizes = new List<double>();
        var o = 0;
        for (var i = 0; i < parent.Children.Count; i++) sizes.Add(i == index ? share * 100 : At(others, o++) * (1 - share));
        return SetSplitSizes(restored, parent.Id, sizes) ?? restored;
    }

    static SplitNode? FindParentSplit(LayoutNode node, string key)
    {
        if (node is TileNode) return null;
        if (node is GroupNode g) return FindParentSplit(g.Child, key);
        var s = (SplitNode)node;
        if (s.Children.Any(c => NodeKey(c) == key)) return s;
        foreach (var child in s.Children)
            if (FindParentSplit(child, key) is { } found) return found;
        return null;
    }

    /// <summary>Moves the node with <paramref name="key"/> (a tile or a whole group) next to the target.</summary>
    public static LayoutNode? MoveNode(LayoutNode? root, string key, DropTarget target)
    {
        var node = FindNode(root, key);
        // Dropping a node onto itself or a group onto one of its own tiles changes nothing.
        if (node is null || FindNode(node, target.Key) is not null) return root;
        var (without, removed, _) = RemoveNode(root, key);
        if (removed is null) return root;
        var resolved = ResolveTarget(without, removed, target);
        return InsertNode(without, removed, resolved.Key, resolved.Side);
    }

    public static LayoutNode? MoveTile(LayoutNode? root, string sessionId, DropTarget target) => MoveNode(root, TileKey(sessionId), target);

    /// <summary>Groups do not nest: a group dropped next to a grouped tile lands next to that tile's group instead.</summary>
    public static DropTarget ResolveTarget(LayoutNode? root, LayoutNode node, DropTarget target)
    {
        if (node is not GroupNode) return target;
        return GroupOf(root, target.Key) is { } group ? target with { Key = group.Id } : target;
    }

    // ---------- groups ----------

    /// <summary>The group the node with <paramref name="key"/> sits in, if any.</summary>
    public static GroupNode? GroupOf(LayoutNode? root, string key)
    {
        // found: the node was seen (group: the one it sits in, or null).
        (bool Found, GroupNode? Group) Walk(LayoutNode node, GroupNode? group)
        {
            if (NodeKey(node) == key) return (true, group);
            if (node is GroupNode g) return Walk(g.Child, g);
            if (node is SplitNode s)
                foreach (var child in s.Children)
                    if (Walk(child, group) is { Found: true } hit) return hit;
            return (false, null);
        }
        return root is null ? null : Walk(root, null).Group;
    }

    public static List<GroupNode> Groups(LayoutNode? root) => root switch
    {
        GroupNode g => [g, .. Groups(g.Child)],
        SplitNode s => s.Children.SelectMany(Groups).ToList(),
        _ => [],
    };

    /// <summary>Puts a frame around a single tile, starting a new group.</summary>
    public static LayoutNode? GroupTile(LayoutNode? root, string sessionId, string name, string color, string? id = null)
    {
        if (root is null || GroupOf(root, TileKey(sessionId)) is not null) return root;
        return ReplaceNode(root, TileKey(sessionId), child => new GroupNode { Id = id ?? NewId(), Name = name, Color = color, Child = child });
    }

    /// <summary>Removes a group's frame; its tiles stay where they are.</summary>
    public static LayoutNode? DissolveGroup(LayoutNode? root, string groupId) =>
        root is null ? null : Normalize(ReplaceNode(root, groupId, node => node is GroupNode g ? g.Child : node));

    public static LayoutNode? UpdateGroup(LayoutNode? root, string groupId, string? name = null, string? color = null) =>
        root is null ? null : ReplaceNode(root, groupId, node => node is GroupNode g ? WithChild(g, g.Child, name, color) : node);

    /// <summary>Moves a tile into a group. A tile right next to the group keeps its place and the frame grows around
    /// it; any other tile goes after the group's last tile.</summary>
    public static LayoutNode? AddToGroup(LayoutNode? root, string sessionId, string groupId)
    {
        if (root is null || FindNode(root, groupId) is not GroupNode || GroupOf(root, TileKey(sessionId))?.Id == groupId) return root;
        if (GrowAround(root, sessionId, groupId) is { } grown) return grown;
        var (without, removed, _) = RemoveTile(root, sessionId);
        var group = FindNode(without, groupId);
        if (removed is null || group is null) return root;
        return InsertNode(without, removed, TileKey(Leaves(group)[^1]), "right");
    }

    static LayoutNode? GrowAround(LayoutNode root, string sessionId, string groupId)
    {
        var parent = FindParentSplit(root, groupId);
        if (parent is null) return null;
        var index = parent.Children.FindIndex(c => NodeKey(c) == groupId);
        bool IsTile(int i) => i >= 0 && i < parent.Children.Count && parent.Children[i] is var n &&
            (NodeKey(n) == TileKey(sessionId) || n is GroupNode g && NodeKey(g.Child) == TileKey(sessionId));
        int? other = IsTile(index - 1) ? index - 1 : IsTile(index + 1) ? index + 1 : null;
        if (other is not { } o || parent.Children[index] is not GroupNode group) return null;
        var before = o < index;
        var own = At(parent.Sizes, index);
        var theirs = At(parent.Sizes, o);
        var grown = WithChild(group, NewSplit(parent.Dir,
            before ? [Tile(sessionId), group.Child] : [group.Child, Tile(sessionId)],
            before ? [theirs, own] : [own, theirs]));
        var first = Math.Min(index, o);
        var children = new List<LayoutNode>(parent.Children);
        children.RemoveRange(first, 2);
        children.Insert(first, grown);
        var sizes = new List<double>(parent.Sizes);
        sizes.RemoveRange(first, Math.Min(2, sizes.Count - first));
        sizes.Insert(first, own + theirs);
        return Normalize(ReplaceNode(root, parent.Id, _ => WithChildren(parent, children, sizes)));
    }

    /// <summary>Moves a tile out of its group, next to the group.</summary>
    public static LayoutNode? RemoveFromGroup(LayoutNode? root, string sessionId)
    {
        var group = GroupOf(root, TileKey(sessionId));
        if (group is null) return root;
        var (without, removed, _) = RemoveTile(root, sessionId);
        if (removed is null) return root;
        return FindNode(without, group.Id) is not null ? InsertNode(without, removed, group.Id, "right") : AppendNode(without, removed);
    }

    public static LayoutNode? SetSplitSizes(LayoutNode? root, string splitId, IReadOnlyList<double> sizes) =>
        root is null ? null : ReplaceNode(root, splitId, node =>
            node is SplitNode s && sizes.Count == s.Children.Count && sizes.All(x => double.IsFinite(x) && x >= 0)
                ? WithChildren(s, [.. s.Children], ScaleTo100(sizes))
                : node);

    /// <summary>Tiles and whole groups, in layout order: what a grid arranges (a group stays together).</summary>
    public static List<LayoutNode> LayoutUnits(LayoutNode? node) => node switch
    {
        null => [],
        SplitNode s => s.Children.SelectMany(LayoutUnits).ToList(),
        _ => [node],
    };

    /// <summary>Rows × columns for <paramref name="count"/> tiles: as square as possible, wider than tall.</summary>
    public static (int Rows, int Cols) GridShape(int count)
    {
        if (count <= 0) return (0, 0);
        var cols = (int)Math.Ceiling(Math.Sqrt(count));
        return ((int)Math.Ceiling(count / (double)cols), cols);
    }

    /// <summary>The units arranged in an even grid of rows; a last, shorter row shares its width among fewer tiles.</summary>
    public static LayoutNode? GridLayout(IReadOnlyList<LayoutNode> units, Func<string>? newId = null)
    {
        if (units.Count == 0) return null;
        if (units.Count == 1) return units[0];
        var (rows, cols) = GridShape(units.Count);
        var rowNodes = new List<LayoutNode>();
        for (var r = 0; r < rows; r++)
        {
            var cells = units.Skip(r * cols).Take(cols).ToList();
            rowNodes.Add(cells.Count == 1 ? cells[0] : NewSplit("row", cells, cells.Select(_ => 100.0 / cells.Count).ToList(), newId));
        }
        if (rowNodes.Count == 1) return Normalize(rowNodes[0]);
        return Normalize(NewSplit("col", rowNodes, rowNodes.Select(_ => 100.0 / rows).ToList(), newId));
    }
}

/// <summary>Group colours and the automatic name and colour for new groups (src/shared/groups.ts).</summary>
public static class GroupColors
{
    /// <summary>The palette, with the English names of their i18n labels (color.*).</summary>
    public static readonly IReadOnlyList<(string Hex, string Label)> All =
    [
        ("#d97757", "Orange"), ("#5b9cf0", "Blue"), ("#4fbf7f", "Green"), ("#a67ff0", "Purple"),
        ("#e06fa8", "Pink"), ("#e0b04a", "Yellow"), ("#3fbfbf", "Teal"), ("#e06060", "Red"),
    ];

    public static bool IsGroupColor(string? value) => All.Any(c => c.Hex == value);

    /// <summary>The first colour no existing group uses; once all are taken, they repeat in order.</summary>
    public static string NextGroupColor(IReadOnlyList<string> used) =>
        All.Select(c => c.Hex).FirstOrDefault(hex => !used.Contains(hex)) ?? All[used.Count % All.Count].Hex;

    /// <summary>The lowest number n for which <paramref name="nameFor"/>(n) is not taken yet.</summary>
    public static int NextGroupNumber(IReadOnlyList<string> used, Func<int, string> nameFor)
    {
        var n = 1;
        while (used.Contains(nameFor(n))) n++;
        return n;
    }
}
