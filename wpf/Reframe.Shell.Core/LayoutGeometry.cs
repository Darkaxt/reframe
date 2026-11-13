namespace Reframe.Shell.Core;

/// <summary>A rectangle in device-independent pixels.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public sealed record TilePlace(string SessionId, Box Box, GroupNode? Group);
public sealed record GroupPlace(GroupNode Group, Box Box);
/// <summary>The separator after panel <see cref="Index"/> of <see cref="Split"/>; <see cref="SplitBox"/> is the
/// whole split (for dragging and snapping).</summary>
public sealed record SeparatorPlace(SplitNode Split, int Index, Box Box, Box SplitBox);

/// <summary>Where everything of a layout goes in the layout area.</summary>
public sealed record LayoutPlaces(List<TilePlace> Tiles, List<GroupPlace> Groups, List<SeparatorPlace> Separators);

/// <summary>
/// The WPF shell's layout of a window's split tree as the Electron renderer's CSS lays it out (styles.css: splits from
/// react-resizable-panels with 6 px separators, .group frames with a 2 px border, a 28 px header and 0 4px 4px padding):
/// absolute boxes in the layout area, so that the tiles (whose terminal HwndHosts must not be rebuilt) stay in one
/// panel and only move. Edges are rounded to whole pixels as the browser snaps borders.
/// </summary>
public static class LayoutGeometry
{
    public const double SeparatorSize = 6, GroupBorder = 2, GroupPadding = 4, GroupHeader = 28;
    /// <summary>react-resizable-panels' minSize of each panel (SplitView.tsx): 160 px across, 90 px stacked.</summary>
    public const double MinRow = 160, MinCol = 90;

    public static LayoutPlaces Compute(LayoutNode? root, Box area)
    {
        var places = new LayoutPlaces([], [], []);
        if (root is not null) Place(root, area, null, places);
        return places;
    }

    /// <summary>The inside of a group frame (where its child goes).</summary>
    public static Box GroupBody(Box frame) => new(
        frame.X + GroupBorder + GroupPadding, frame.Y + GroupBorder + GroupHeader,
        Math.Max(0, frame.Width - 2 * (GroupBorder + GroupPadding)), Math.Max(0, frame.Height - 2 * GroupBorder - GroupHeader - GroupPadding));

    static void Place(LayoutNode node, Box box, GroupNode? group, LayoutPlaces places)
    {
        switch (node)
        {
            case TileNode t:
                places.Tiles.Add(new TilePlace(t.SessionId, box, group));
                break;
            case GroupNode g:
                places.Groups.Add(new GroupPlace(g, box));
                Place(g.Child, GroupBody(box), g, places);
                break;
            case SplitNode s:
                var across = s.Dir == "row";
                var start = across ? box.X : box.Y;
                var length = across ? box.Width : box.Height;
                var n = s.Children.Count;
                var available = Math.Max(0, length - (n - 1) * SeparatorSize);
                var sizes = s.Sizes.Count == n ? s.Sizes : s.Children.Select(_ => 100.0 / n).ToList();
                var total = sizes.Sum();
                var cumulative = 0.0;
                for (var i = 0; i < n; i++)
                {
                    var from = Math.Round(start + cumulative / total * available + i * SeparatorSize);
                    cumulative += sizes[i];
                    var to = Math.Round(start + cumulative / total * available + i * SeparatorSize);
                    var child = across ? new Box(from, box.Y, to - from, box.Height) : new Box(box.X, from, box.Width, to - from);
                    Place(s.Children[i], child, group, places);
                    if (i < n - 1)
                    {
                        var sep = across ? new Box(to, box.Y, SeparatorSize, box.Height) : new Box(box.X, to, box.Width, SeparatorSize);
                        places.Separators.Add(new SeparatorPlace(s, i, sep, box));
                    }
                }
                break;
        }
    }

    /// <summary>The split's sizes with separator <paramref name="index"/> dragged so that its middle is at
    /// <paramref name="pointer"/> (along the split's axis), each of the two panels kept at its minimum size.</summary>
    public static List<double> Drag(IReadOnlyList<double> sizes, int index, Box splitBox, bool across, double pointer)
    {
        var start = across ? splitBox.X : splitBox.Y;
        var length = across ? splitBox.Width : splitBox.Height;
        var available = length - (sizes.Count - 1) * SeparatorSize;
        var result = Layout.ScaleTo100(sizes);
        if (available <= 0) return result;
        var before = result.Take(index).Sum();
        var pair = result[index] + result[index + 1];
        var min = (across ? MinRow : MinCol) / available * 100;
        var end = (pointer - start - index * SeparatorSize - SeparatorSize / 2) / available * 100;
        var own = Math.Clamp(end - before, Math.Min(min, pair / 2), Math.Max(pair - min, pair / 2));
        result[index] = own;
        result[index + 1] = pair - own;
        return result;
    }

    /// <summary>The side of <paramref name="box"/> nearest to the point (src/renderer/dnd.ts sideAt).</summary>
    public static string SideAt(Box box, double x, double y)
    {
        var fx = (x - box.X) / box.Width;
        var fy = (y - box.Y) / box.Height;
        (string Side, double Distance)[] distances = [("left", fx), ("right", 1 - fx), ("top", fy), ("bottom", 1 - fy)];
        return distances.Aggregate((best, d) => d.Distance < best.Distance ? d : best).Side;
    }

    /// <summary>The half of <paramref name="box"/> a drop on <paramref name="side"/> previews (.drop-left etc.).</summary>
    public static Box PreviewOf(Box box, string side) => side switch
    {
        "left" => box with { Width = box.Width / 2 },
        "right" => new Box(box.X + box.Width / 2, box.Y, box.Width / 2, box.Height),
        "top" => box with { Height = box.Height / 2 },
        "bottom" => new Box(box.X, box.Y + box.Height / 2, box.Width, box.Height / 2),
        _ => box,
    };

    /// <summary>Where a terminal window dragged in from another app would land at a point (dnd.ts dropTargetAt): beside
    /// the tile under it (its nearest side, the preview that half); on a group's frame after its last tile (the preview
    /// the group's body); elsewhere at the end (the preview the whole layout area while the window is empty).</summary>
    public static DropTarget? ForeignTargetAt(LayoutPlaces places, Box area, double x, double y, out Box? preview)
    {
        foreach (var t in places.Tiles)
        {
            if (!t.Box.Contains(x, y)) continue;
            var side = SideAt(t.Box, x, y);
            preview = PreviewOf(t.Box, side);
            return new DropTarget(Layout.TileKey(t.SessionId), side);
        }
        foreach (var g in places.Groups)
        {
            if (!g.Box.Contains(x, y) || places.Tiles.LastOrDefault(t => t.Group?.Id == g.Group.Id) is not { } last) continue;
            preview = GroupBody(g.Box);
            return new DropTarget(Layout.TileKey(last.SessionId), "right");
        }
        preview = places.Tiles.Count == 0 && area.Contains(x, y) ? area : null;
        return null;
    }

    /// <summary>Where a dragged node would land at a point: a group frame's 14 px edge bands (outside the group), else
    /// the tile under the point and its nearest side. A group never lands inside another group, nor on itself.</summary>
    public static DropTarget? TargetAt(LayoutPlaces places, string draggedKey, bool draggingGroup, double x, double y, out Box preview)
    {
        const double Band = 14;
        preview = default;
        foreach (var g in places.Groups)
        {
            if (g.Group.Id == draggedKey || !g.Box.Contains(x, y)) continue;
            string? side = x - g.Box.X < Band ? "left" : g.Box.Right - x < Band ? "right" : y - g.Box.Y < Band ? "top" : g.Box.Bottom - y < Band ? "bottom" : null;
            if (side is null) continue;
            preview = PreviewOf(g.Box, side);
            return new DropTarget(g.Group.Id, side);
        }
        foreach (var t in places.Tiles)
        {
            if (!t.Box.Contains(x, y)) continue;
            var key = Layout.TileKey(t.SessionId);
            if (key == draggedKey || t.Group?.Id == draggedKey || (draggingGroup && t.Group is not null)) return null;
            var side = SideAt(t.Box, x, y);
            preview = PreviewOf(t.Box, side);
            return new DropTarget(key, side);
        }
        return null;
    }
}
