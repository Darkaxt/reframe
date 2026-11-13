using Reframe.Shell.Core;
using Xunit;
using static Reframe.Shell.Core.Layout;

namespace Reframe.Shell.Tests;

/// <summary>Ports of src/shared/layout.test.ts, groups.test.ts, actions.test.ts, folder-workspaces.test.ts (the cases
/// SidebarTests does not have) and src/renderer/snap.test.ts and view-mode.test.ts, plus the shell's layout geometry.</summary>
public class LayoutTests
{
    static int splits;

    internal static SplitNode Split(string dir, LayoutNode[] children, double[]? sizes = null) => new()
    {
        Id = $"s-{++splits}", Dir = dir, Children = [.. children],
        Sizes = sizes?.ToList() ?? children.Select(_ => 100.0 / children.Length).ToList(),
    };

    /// <summary>Structure without ids or exact sizes, e.g. row(a, col(b, c)).</summary>
    internal static string Shape(LayoutNode? node) => node switch
    {
        null => "∅",
        TileNode t => t.SessionId,
        GroupNode g => $"group({Shape(g.Child)})",
        SplitNode s => $"{s.Dir}({string.Join(", ", s.Children.Select(Shape))})",
        _ => "?",
    };

    static double[] SizesOf(LayoutNode? node) =>
        node is SplitNode s ? s.Sizes.Select(x => Math.Round(x * 10, MidpointRounding.AwayFromZero) / 10).ToArray() : [];

    static LayoutNode? Group(LayoutNode? root, string sessionId, string id) => GroupTile(root, sessionId, id.ToUpperInvariant(), "#f00", id);

    static DropTarget At(string sessionId, string side) => new(TileKey(sessionId), side);

    // ---------- insertTile ----------

    [Fact]
    public void InsertMakesTheFirstTileTheRoot() => Assert.Equal("a", Shape(InsertTile(null, "a")));

    [Fact]
    public void InsertWithoutTargetFillsTwoColumnsInTurn()
    {
        var root = InsertTile(null, "a");
        root = InsertTile(root, "b");
        Assert.Equal("row(a, b)", Shape(root));
        Assert.Equal([50, 50], SizesOf(root));
        root = InsertTile(root, "c");
        Assert.Equal("row(a, col(b, c))", Shape(root));
        root = InsertTile(root, "d");
        Assert.Equal("row(col(a, d), col(b, c))", Shape(root));
        root = InsertTile(InsertTile(root, "e"), "f");
        Assert.Equal("row(col(a, d, f), col(b, c, e))", Shape(root));
        Assert.Equal([50, 50], SizesOf(root));
        Assert.Equal([33.3, 33.3, 33.3], SizesOf(((SplitNode)root).Children[1]));
    }

    [Fact]
    public void AppendGoesToTheEmptiestColumnKeepingProportions()
    {
        Assert.Equal("row(a, b, col(c, x))", Shape(AppendNode(Split("row", [Tile("a"), Tile("b"), Tile("c")]), Tile("x"))));
        var three = Split("row", [Split("col", [Tile("a"), Tile("b")]), Tile("c"), Split("col", [Tile("d"), Tile("e")])]);
        Assert.Equal("row(col(a, b), col(c, x), col(d, e))", Shape(AppendNode(three, Tile("x"))));
        Assert.Equal("row(col(a, b), x)", Shape(AppendNode(Split("col", [Tile("a"), Tile("b")]), Tile("x"))));
        var resized = AppendNode(Split("row", [Split("col", [Tile("a"), Tile("d")]), Split("col", [Tile("b"), Tile("c")], [70, 30])]), Tile("x"));
        Assert.Equal([46.7, 20, 33.3], SizesOf(((SplitNode)resized).Children[1]));
    }

    [Fact]
    public void AppendPutsANewTileBelowAGroupNotIntoIt()
    {
        var root = Group(Split("row", [Tile("a"), Tile("b")]), "b", "g");
        Assert.Equal("row(a, col(group(b), x))", Shape(AppendNode(root, Tile("x"))));
        Assert.Equal("row(group(a), x)", Shape(AppendNode(Group(Tile("a"), "a", "g"), Tile("x"))));
    }

    [Fact]
    public void InsertPlacesATileOnEachSide()
    {
        var a = Tile("a");
        Assert.Equal("row(b, a)", Shape(InsertTile(a, "b", At("a", "left"))));
        Assert.Equal("row(a, b)", Shape(InsertTile(a, "b", At("a", "right"))));
        Assert.Equal("col(b, a)", Shape(InsertTile(a, "b", At("a", "top"))));
        Assert.Equal("col(a, b)", Shape(InsertTile(a, "b", At("a", "bottom"))));
    }

    [Fact]
    public void InsertSplitsOnlyTheTargetSlotAndFlattens()
    {
        var next = InsertTile(Split("row", [Tile("a"), Tile("b")], [50, 50]), "c", At("b", "right"));
        Assert.Equal("row(a, b, c)", Shape(next));
        Assert.Equal([50, 25, 25], SizesOf(next));
        Assert.Equal("row(a, col(b, c))", Shape(InsertTile(Split("row", [Tile("a"), Tile("b")]), "c", At("b", "bottom"))));
        Assert.Equal("row(a, b)", Shape(InsertTile(Tile("a"), "b", At("zzz", "top"))));
    }

    // ---------- removeTile, restoreNode ----------

    [Fact]
    public void RemoveCollapsesAndRedistributes()
    {
        var (root, removed, _) = RemoveTile(Split("row", [Tile("a"), Tile("b")]), "a");
        Assert.Equal("b", Shape(root));
        Assert.Equal("a", ((TileNode)removed!).SessionId);
        var rest = RemoveTile(Split("row", [Tile("a"), Tile("b"), Tile("c")], [50, 25, 25]), "c").Root;
        Assert.Equal("row(a, b)", Shape(rest));
        Assert.Equal([66.7, 33.3], SizesOf(rest));
        Assert.Null(RemoveTile(Tile("a"), "a").Root);
        var grouped = Split("row", [Tile("a"), new GroupNode { Id = "g", Name = "G", Color = "#f00", Child = Tile("b") }]);
        Assert.Equal("a", Shape(RemoveTile(grouped, "b").Root));
    }

    [Fact]
    public void RemoveRecordsTheNeighborAsAnchor()
    {
        var hint = RemoveTile(Split("row", [Tile("a"), Tile("b"), Tile("c")], [50, 30, 20]), "b").Hint!;
        Assert.Equal((TileKey("a"), "right", 30.0 / 80, (double?)0.3), (hint.Anchor, hint.Side, hint.Ratio, hint.Share));
        var first = RemoveTile(Split("col", [Tile("a"), Tile("b")]), "a").Hint!;
        Assert.Equal((TileKey("b"), "top", 0.5, (double?)0.5), (first.Anchor, first.Side, first.Ratio, first.Share));
    }

    [Fact]
    public void RestorePutsATileBackWithItsShare()
    {
        var (root, removed, hint) = RemoveTile(Split("row", [Tile("a"), Split("col", [Tile("b"), Tile("c")])]), "c");
        Assert.Equal("row(a, b)", Shape(root));
        Assert.Equal("row(a, col(b, c))", Shape(RestoreNode(root, removed!, hint)));
        var three = RemoveTile(Split("row", [Tile("a"), Tile("b"), Tile("c")], [50, 30, 20]), "b");
        Assert.Equal([50, 30, 20], SizesOf(RestoreNode(three.Root, three.Removed!, three.Hint)).Select(x => Math.Round(x)));
        var two = RemoveTile(Split("row", [Tile("a"), Tile("b")]), "b");
        var withoutAnchor = RemoveTile(AppendNode(two.Root, Tile("x")), "a").Root;
        Assert.Equal("row(x, b)", Shape(RestoreNode(withoutAnchor, two.Removed!, two.Hint)));
    }

    // ---------- moveTile ----------

    [Fact]
    public void MoveTiles()
    {
        Assert.Equal("row(b, col(c, a))", Shape(MoveTile(Split("row", [Tile("a"), Tile("b"), Tile("c")]), "a", At("c", "bottom"))));
        var two = Split("row", [Tile("a"), Tile("b")]);
        Assert.Same(two, MoveTile(two, "a", At("a", "left")));
        LayoutNode? root = Split("row", [Tile("a"), Split("col", [Tile("b"), Tile("c")]), Tile("d")]);
        root = MoveTile(root, "b", At("d", "top"));
        root = MoveTile(root, "d", At("a", "left"));
        Assert.Equal(["a", "b", "c", "d"], Leaves(root).Order());
    }

    // ---------- groups ----------

    [Fact]
    public void GroupFramesATileAndGrowsAfterItsLastOne()
    {
        var root = Group(Split("row", [Tile("a"), Tile("b"), Tile("c")]), "a", "g");
        Assert.Equal("row(group(a), b, c)", Shape(root));
        root = AddToGroup(root, "c", "g");
        Assert.Equal("row(group(row(a, c)), b)", Shape(root));
        Assert.Equal("g", GroupOf(root, TileKey("c"))?.Id);
        Assert.Null(GroupOf(root, TileKey("b")));
        Assert.Same(root, Group(root, "a", "h"));
    }

    [Fact]
    public void GroupGrowsAroundANeighboringTile()
    {
        var root = Group(Split("col", [Tile("a"), Tile("b")], [30, 70]), "b", "g");
        root = AddToGroup(root, "a", "g");
        Assert.Equal("group(col(a, b))", Shape(root));
        Assert.Equal([30, 70], SizesOf(((GroupNode)root!).Child));

        root = Group(Split("row", [Tile("c"), Split("col", [Tile("a"), Tile("b"), Tile("d")], [20, 30, 50])]), "a", "g");
        root = Group(root, "b", "h");
        root = AddToGroup(root, "b", "g");
        Assert.Equal("row(c, col(group(col(a, b)), d))", Shape(root));
        var column = ((SplitNode)root!).Children[1];
        Assert.Equal([50, 50], SizesOf(column));
        Assert.Equal([40, 60], SizesOf(((GroupNode)((SplitNode)column).Children[0]).Child));
        Assert.Equal(["g"], Groups(root).Select(g => g.Id));
    }

    [Fact]
    public void RemoveFromGroupAndDissolve()
    {
        var root = AddToGroup(Group(Split("row", [Tile("a"), Tile("b")]), "a", "g"), "b", "g");
        root = RemoveFromGroup(root, "b");
        Assert.Equal("row(group(a), b)", Shape(root));
        root = RemoveFromGroup(root, "a");
        Assert.Empty(Groups(root));
        Assert.Equal(["a", "b"], Leaves(root).Order());
        var three = AddToGroup(Group(Split("row", [Tile("a"), Tile("b"), Tile("c")]), "a", "g"), "b", "g");
        Assert.Equal("row(a, b, c)", Shape(DissolveGroup(three, "g")));
    }

    [Fact]
    public void MoveAWholeGroupAndTilesJoinOrLeaveByWhereTheyLand()
    {
        var root = Group(Split("row", [Tile("a"), Tile("b"), Tile("c")]), "a", "g");
        root = MoveNode(root, "g", At("c", "bottom"));
        Assert.Equal("row(b, col(c, group(a)))", Shape(root));
        root = MoveTile(root, "b", At("a", "right"));
        Assert.Equal("g", GroupOf(root, TileKey("b"))?.Id);
        root = MoveTile(root, "b", At("c", "left"));
        Assert.Null(GroupOf(root, TileKey("b")));
    }

    [Fact]
    public void GroupsNeverNest()
    {
        var root = Group(Group(Split("row", [Tile("a"), Tile("b")]), "a", "g"), "b", "h");
        Assert.Equal("col(group(a), group(b))", Shape(MoveNode(root, "h", At("a", "bottom"))));
        Assert.Same(root, MoveNode(root, "g", At("a", "left")));
    }

    [Fact]
    public void NormalizeAndSizes()
    {
        var flat = Normalize(Split("row", [Tile("a"), Split("row", [Tile("b"), Tile("c")], [50, 50])], [50, 50]));
        Assert.Equal("row(a, b, c)", Shape(flat));
        Assert.Equal([50, 25, 25], SizesOf(flat));
        var root = Split("row", [Tile("a"), Tile("b")]);
        Assert.Equal([25, 75], SizesOf(SetSplitSizes(root, root.Id, [1, 3])));
        Assert.Equal(SizesOf(root), SizesOf(SetSplitSizes(root, root.Id, [1, 2, 3])));
        Assert.Equal(SizesOf(root), SizesOf(SetSplitSizes(root, root.Id, [double.NaN, 1])));
    }

    [Fact]
    public void GridShapesAndLayouts()
    {
        Assert.Equal((2, 2), GridShape(4));
        Assert.Equal((2, 3), GridShape(5));
        Assert.Equal("col(row(a, b), row(c, d))", Shape(GridLayout([Tile("a"), Tile("b"), Tile("c"), Tile("d")])));
        Assert.Equal("col(row(a, b), c)", Shape(GridLayout([Tile("a"), Tile("b"), Tile("c")])));
        Assert.Null(GridLayout([]));
    }

    [Fact]
    public void LayoutOperationsLeaveTheirInputAsItWas()
    {
        var root = Split("row", [Tile("a"), Tile("b"), Tile("c")], [50, 30, 20]);
        var before = StateJsonOf(root);
        _ = RemoveTile(root, "b");
        _ = MoveTile(root, "a", At("c", "bottom"));
        _ = SetSplitSizes(root, root.Id, [1, 1, 1]);
        _ = AddToGroup(Group(root, "a", "g"), "b", "g");
        Assert.Equal(before, StateJsonOf(root));
    }

    static string StateJsonOf(LayoutNode node)
    {
        var state = AppState.CreateDefault();
        state.Windows[0].Id = "w";
        state.Windows[0].Layout = node;
        return StateJson.ToText(state);
    }

    // ---------- groups.ts ----------

    [Fact]
    public void NewGroupDefaults()
    {
        var all = GroupColors.All.Select(c => c.Hex).ToList();
        Assert.Equal(all[0], GroupColors.NextGroupColor([]));
        Assert.Equal(all[1], GroupColors.NextGroupColor([all[0], all[2]]));
        Assert.Equal(all[0], GroupColors.NextGroupColor(all));
        Assert.Equal(all[1], GroupColors.NextGroupColor([.. all, all[0]]));
        string Name(int n) => $"Group {n}";
        Assert.Equal(1, GroupColors.NextGroupNumber([], Name));
        Assert.Equal(2, GroupColors.NextGroupNumber(["Group 1", "Group 3", "API"], Name));
        Assert.True(GroupColors.IsGroupColor(all[3]));
        Assert.False(GroupColors.IsGroupColor("#123456"));
    }

    // ---------- folder-workspaces.ts ----------

    [Fact]
    public void ExpandedWorkspaceKeysMigrate()
    {
        Assert.Equal("project:android", Workspaces.ExpandedWorkspaceKey("chatgpt:c:/owner/ChatGPT/Android"));
        Assert.Equal("project:android", Workspaces.ExpandedWorkspaceKey("anthropic:c:/owner/Anthropic/Android"));
        Assert.Equal("project:android", Workspaces.ExpandedWorkspaceKey("d:/other/ANDROID"));
        Assert.Equal("project:android", Workspaces.ExpandedWorkspaceKey("project:android"));
    }

    [Fact]
    public void RebaseFolder()
    {
        const string from = @"C:\Users\me\Documents\ChatGPT\Windows", to = @"C:\Users\me\Documents\Anthropic\Windows";
        Assert.Equal($@"{to}\Foo/Bar", Workspaces.RebaseFolder($@"{from}\Foo/Bar", from, to));
        Assert.Equal($@"{to}\Foo", Workspaces.RebaseFolder("c:/users/ME/documents/chatgpt/windows/Foo", from, to));
        Assert.Equal($@"{to}\foo", Workspaces.RebaseFolder($@"{from}\foo", $@"{from}\", $@"{to}\"));
        Assert.Equal(to, Workspaces.RebaseFolder(from, from, to));
        Assert.Equal(to, Workspaces.RebaseFolder($@"{from}\", from, to));
        Assert.Equal(to, Workspaces.RebaseFolder("", from, to));
        Assert.Equal(@"D:\Projects\x", Workspaces.RebaseFolder(@"D:\Projects\x", from, to));
        const string sibling = @"C:\Users\me\Documents\ChatGPT2\Windows\x";
        Assert.Equal(sibling, Workspaces.RebaseFolder(sibling, @"C:\Users\me\Documents\ChatGPT", to));
        Assert.Equal($@"{from}2\x", Workspaces.RebaseFolder($@"{from}2\x", from, to));
    }

    // ---------- snap.ts ----------

    static readonly Snap.GridAxis Grid12 = new(0, 1200, 12);
    static double[] Round2(IEnumerable<double> sizes) => sizes.Select(s => Math.Round(s * 100, MidpointRounding.AwayFromZero) / 100).ToArray();

    [Fact]
    public void SnapPutsTheDraggedSeparatorOnTheNearestLine()
    {
        var whole = new Snap.SplitAxis(0, 1200, 0);
        Assert.Equal([33.33, 66.67], Round2(Snap.SnapSizes([37, 63], [50, 50], whole, Grid12)));
        Assert.Equal([50, 50], Round2(Snap.SnapSizes([45.9, 54.1], [50, 50], whole, Grid12)));
        Assert.Equal([33.33, 66.67], Round2(Snap.SnapSizes([30, 70], [50, 50], new Snap.SplitAxis(600, 600, 0), Grid12)));
        var first = Snap.SnapSizes([26, 74], [50, 50], new Snap.SplitAxis(0, 1206, 6), Grid12)[0];
        Assert.Equal(297, Math.Round(first / 100 * 1200));
        Assert.Equal([33.33, 50, 16.67], Round2(Snap.SnapSizes([33.33, 47.17, 19.5], [33.33, 33.33, 33.34], whole, Grid12)));
        Assert.Equal([10, 0.5, 89.5], Round2(Snap.SnapSizes([10, 0.5, 89.5], [10, 20, 70], whole, Grid12)));
    }

    // ---------- view-mode.ts ----------

    [Fact]
    public void NextShownAndCycle()
    {
        Assert.Equal("b", ViewModes.NextShown(["a", "b"], ["b", "a"], "b"));
        Assert.Equal("c", ViewModes.NextShown(["a", "b"], ["a", "b", "c"], "a"));
        // The shown thread closed: none is shown until one is picked.
        Assert.Null(ViewModes.NextShown(["a", "b", "c"], ["a", "c"], "b"));
        Assert.Null(ViewModes.NextShown(["a", "b", "c"], ["a", "b"], "c"));
        Assert.Equal("a", ViewModes.NextShown([], ["a", "b"], null));
        Assert.Null(ViewModes.NextShown(["a"], [], "a"));
        Assert.Equal("a", ViewModes.Cycle(["a", "b", "c"], "c", 1));
        Assert.Equal("c", ViewModes.Cycle(["a", "b", "c"], "a", -1));
        Assert.Equal("a", ViewModes.Cycle(["a", "b"], null, 1));
        Assert.Null(ViewModes.Cycle([], "a", 1));
    }

    // ---------- actions.ts ----------

    static WindowState WithTiles(params string[] ids) => ids.Aggregate(WindowState.Empty("w1"), (w, id) => LayoutActions.AddTile(w, id));
    static WindowState Do(WindowState w, LayoutAction a) => LayoutActions.Apply(w, a);

    [Fact]
    public void CollapseAndExpand()
    {
        var win = WithTiles("a", "b", "c");
        var collapsed = Do(win, new LayoutAction.TileCollapse("b"));
        Assert.Equal(["a", "c"], Leaves(collapsed.Layout));
        Assert.Equal(["b"], collapsed.Dock.SelectMany(LayoutActions.DockSessions));
        Assert.True(LayoutActions.WindowHasSession(collapsed, "b"));
        var expanded = Do(collapsed, new LayoutAction.TileExpand("b"));
        Assert.Equal(["a", "b", "c"], Leaves(expanded.Layout));
        Assert.Empty(expanded.Dock);
        var one = WithTiles("a");
        Assert.Same(one, Do(one, new LayoutAction.TileCollapse("x")));
        Assert.Same(one, Do(one, new LayoutAction.TileExpand("x")));
    }

    [Fact]
    public void DropSessionAndEmptyWindows()
    {
        var win = Do(WithTiles("a", "b"), new LayoutAction.TileCollapse("b"));
        win = LayoutActions.DropSession(LayoutActions.DropSession(win, "b"), "a");
        Assert.Null(win.Layout);
        Assert.Empty(win.Dock);
        Assert.True(LayoutActions.IsWindowEmpty(WindowState.Empty("w")));
        var collapsed = Do(WithTiles("a"), new LayoutAction.TileCollapse("a"));
        Assert.Null(collapsed.Layout);
        Assert.False(LayoutActions.IsWindowEmpty(collapsed));
        Assert.True(LayoutActions.IsWindowEmpty(LayoutActions.DropSession(collapsed, "a")));
        var tiles = LayoutActions.AddTile(WindowState.Empty("tiles"), "a");
        var docked = Do(LayoutActions.AddTile(WindowState.Empty("collapsed"), "b"), new LayoutAction.TileCollapse("b"));
        Assert.Equal([tiles, docked], LayoutActions.WindowsToRestore([WindowState.Empty("x"), tiles, WindowState.Empty("y"), docked]));
        Assert.Equal(["x"], LayoutActions.WindowsToRestore([WindowState.Empty("x"), WindowState.Empty("y")]).Select(w => w.Id));
    }

    static WindowState Grouped()
    {
        var win = WithTiles("a", "b", "c");
        win = LayoutActions.Copy(win, GroupTile(win.Layout, "a", "G", "#f00", "g"), win.Dock);
        return LayoutActions.Copy(win, AddToGroup(win.Layout, "b", "g"), win.Dock);
    }

    [Fact]
    public void CollapseAWholeGroupAndRestoreIt()
    {
        var collapsed = Do(Grouped(), new LayoutAction.GroupCollapse("g"));
        Assert.Equal(["c"], Leaves(collapsed.Layout));
        Assert.Single(collapsed.Dock);
        Assert.Equal(["a", "b", "c"], LayoutActions.WindowSessions(collapsed).Order());
        Assert.True(LayoutActions.IsCollapsed(collapsed, "a"));
        Assert.Equal(["g"], LayoutActions.WindowGroups(collapsed).Select(g => g.Id));
        var expanded = Do(collapsed, new LayoutAction.GroupExpand("g"));
        Assert.Equal(["a", "b", "c"], Leaves(expanded.Layout));
        Assert.Equal("g", GroupOf(expanded.Layout, TileKey("b"))?.Id);
        Assert.Empty(expanded.Dock);

        var two = WithTiles("a", "b");
        two = LayoutActions.Copy(two, GroupTile(two.Layout, "a", "G", "#f00", "g"), two.Dock);
        var last = Do(two, new LayoutAction.TileCollapse("a"));
        Assert.Equal("g", Assert.IsType<DockGroup>(Assert.Single(last.Dock)).Node.Id);
    }

    [Fact]
    public void RenameAGroupVisibleOrCollapsed()
    {
        var renamed = Do(Grouped(), new LayoutAction.GroupRename("g", "  API  "));
        Assert.Equal("API", LayoutActions.WindowGroups(renamed)[0].Name);
        var again = Do(Do(renamed, new LayoutAction.GroupCollapse("g")), new LayoutAction.GroupRename("g", "Backend"));
        Assert.Equal("Backend", LayoutActions.WindowGroups(again)[0].Name);
        Assert.Same(again, Do(again, new LayoutAction.GroupRename("g", "   ")));
        Assert.Equal(80, LayoutActions.WindowGroups(Do(again, new LayoutAction.GroupRename("g", new string('x', 500))))[0].Name.Length);
        var recolored = LayoutActions.PatchGroup(again, "g", color: "#5b9cf0");
        Assert.Equal("#5b9cf0", LayoutActions.WindowGroups(recolored)[0].Color);
    }

    [Fact]
    public void TakeAndPutNodes()
    {
        var collapsed = Do(Grouped(), new LayoutAction.GroupCollapse("g"));
        var one = LayoutActions.DropSession(collapsed, "a");
        Assert.Equal(["b", "c"], LayoutActions.WindowSessions(one).Order());
        Assert.Empty(LayoutActions.DropSession(one, "b").Dock);

        var docked = Do(WithTiles("a", "b"), new LayoutAction.TileCollapse("b"));
        var (rest, node) = LayoutActions.TakeNode(docked, TileKey("b"));
        Assert.Equal("b", Assert.IsType<TileNode>(node).SessionId);
        Assert.Empty(rest.Dock);
        var taken = LayoutActions.TakeNode(collapsed, TileKey("a"));
        Assert.Equal("a", Assert.IsType<TileNode>(taken.Node).SessionId);
        Assert.Equal("b", Shape(Assert.IsType<DockGroup>(Assert.Single(taken.Win.Dock)).Node.Child));
        Assert.Empty(LayoutActions.TakeNode(taken.Win, TileKey("b")).Win.Dock);

        var three = Do(WithTiles("a", "b", "c"), new LayoutAction.TileCollapse("b"));
        var next = Do(three, new LayoutAction.NodeMove(TileKey("b"), At("a", "bottom")));
        Assert.Empty(next.Dock);
        Assert.Equal(["a", "b", "c"], LayoutActions.WindowSessions(next).Order());
        var atEnd = Do(three, new LayoutAction.NodeMove(TileKey("b"), null));
        Assert.Contains("b", Leaves(atEnd.Layout));
        Assert.Same(three, Do(three, new LayoutAction.NodeMove(TileKey("a"), null)));

        var (from, group) = LayoutActions.TakeNode(Grouped(), "g");
        Assert.Equal(["c"], Leaves(from.Layout));
        var to = LayoutActions.PutNode(WithTiles("x"), group!, At("x", "bottom"));
        Assert.Equal("g", GroupOf(to.Layout, TileKey("a"))?.Id);
        Assert.Same(group, LayoutActions.PutNode(WindowState.Empty("w2"), group!, null).Layout);
    }

    [Fact]
    public void ViewModeDefaultsToTiled()
    {
        Assert.Equal("tiled", LayoutActions.ViewModeOf(WindowState.Empty("w1")));
        Assert.Equal("single", LayoutActions.ViewModeOf(Do(WindowState.Empty("w1"), new LayoutAction.WindowViewMode("single"))));
        var w = WindowState.Empty("w1");
        Assert.Same(w, Do(w, new LayoutAction.WindowViewMode("zoomed")));
    }

    static string KeyOf(WindowState w) => LayoutActions.SettleGrid(w, null).Key;

    [Fact]
    public void SettleGridRegridsWhenTilesComeOrGo()
    {
        var win = WithTiles("a", "b", "c");
        Assert.Same(win, LayoutActions.SettleGrid(win, null).Win);
        var added = LayoutActions.AddTile(win, "d");
        var settled = LayoutActions.SettleGrid(added, KeyOf(win)).Win;
        Assert.Equal("col(row(a, d), row(b, c))", Shape(settled.Layout));
        var closed = LayoutActions.DropSession(settled, "d");
        Assert.NotSame(closed, LayoutActions.SettleGrid(closed, KeyOf(settled)).Win);
        var docked = Do(settled, new LayoutAction.TileCollapse("a"));
        Assert.NotSame(docked, LayoutActions.SettleGrid(docked, KeyOf(settled)).Win);
        var expanded = Do(docked, new LayoutAction.TileExpand("a"));
        Assert.NotSame(expanded, LayoutActions.SettleGrid(expanded, KeyOf(docked)).Win);

        var two = WithTiles("a", "b");
        var project = LayoutActions.Copy(two, two.Layout, two.Dock);
        project.ActiveProject = "p";
        Assert.NotSame(project, LayoutActions.SettleGrid(project, KeyOf(two)).Win);
        var single = Do(two, new LayoutAction.WindowViewMode("single"));
        Assert.NotSame(two, LayoutActions.SettleGrid(two, KeyOf(single)).Win);
    }

    [Fact]
    public void SettleGridLeavesResizesAndSingleWindows()
    {
        var win = WithTiles("a", "b", "c");
        var resized = Do(win, new LayoutAction.SplitResize(((SplitNode)win.Layout!).Id, [0.5, 0.3, 0.2]));
        Assert.Same(resized, LayoutActions.SettleGrid(resized, KeyOf(win)).Win);
        var single = Do(win, new LayoutAction.WindowViewMode("single"));
        var added = LayoutActions.AddTile(single, "d");
        Assert.Same(added, LayoutActions.SettleGrid(added, KeyOf(single)).Win);
    }

    // ---------- the shell's geometry ----------

    [Fact]
    public void GeometryLaysOutA2x2GridAsTheCssDoes()
    {
        // The layout area of a 1280x800 window with the sidebar open: 1010x760 inside 6px padding.
        var layout = Split("row", [Split("col", [Tile("a"), Tile("b")]), Split("col", [Tile("c"), Tile("d")])]);
        var places = LayoutGeometry.Compute(layout, new Box(276, 46, 998, 748));
        Assert.Equal(["a", "b", "c", "d"], places.Tiles.Select(t => t.SessionId));
        Assert.Equal(new Box(276, 46, 496, 371), places.Tiles[0].Box);
        Assert.Equal(new Box(778, 46, 496, 371), places.Tiles[2].Box);
        Assert.Equal(new Box(276, 423, 496, 371), places.Tiles[1].Box);
        Assert.Equal(3, places.Separators.Count);
        // The row's separator between the columns, and one in each column.
        Assert.Contains(places.Separators, s => s.Box == new Box(772, 46, 6, 748));
        Assert.Contains(places.Separators, s => s.Box == new Box(276, 417, 496, 6));
    }

    [Fact]
    public void GeometryFramesGroups()
    {
        var group = new GroupNode { Id = "g", Name = "Backend", Color = "#5b9cf0", Child = Split("col", [Tile("a"), Tile("b")]) };
        var places = LayoutGeometry.Compute(Split("row", [group, Tile("c")]), new Box(0, 0, 1006, 600));
        Assert.Equal(new Box(0, 0, 500, 600), Assert.Single(places.Groups).Box);
        // Border 2, header 28, padding 0 4px 4px.
        Assert.Equal(new Box(6, 30, 488, 279), places.Tiles[0].Box);
        Assert.Equal("g", places.Tiles[0].Group?.Id);
        Assert.Null(places.Tiles[2].Group);
        // A group dropped next to a grouped tile lands at the group's edge bands only; a tile anywhere.
        Assert.Equal(new DropTarget("g", "left"), LayoutGeometry.TargetAt(places, "tile:c", false, 5, 300, out _));
        Assert.Equal(new DropTarget("tile:a", "top"), LayoutGeometry.TargetAt(places, "tile:c", false, 250, 60, out var preview));
        Assert.Equal(new Box(6, 30, 488, 139.5), preview);
        Assert.Null(LayoutGeometry.TargetAt(places, "x", true, 250, 60, out _));
    }

    [Fact]
    public void GeometryDragKeepsMinimumSizes()
    {
        var box = new Box(0, 0, 1006, 600);
        Assert.Equal([30, 70], LayoutGeometry.Drag([50, 50], 0, box, true, 303).Select(x => Math.Round(x)));
        Assert.Equal(16, Math.Round(LayoutGeometry.Drag([50, 50], 0, box, true, 0)[0]));
        Assert.Equal("right", LayoutGeometry.SideAt(box, 1000, 300));
    }
}
