using System.Text.RegularExpressions;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Windows, tray and close policy (S8): src/main/tray.ts, app.ts and windows.ts; focus-nav.test.ts and
/// attention.test.ts's nextWaiting, ported.</summary>
public class ShellWindowsTests
{
    static (string, Box) B(string id, double x, double y, double w, double h) => (id, new Box(x, y, w, h));

    // ---------- focus-nav.test.ts ----------

    static readonly List<(string, Box)> Stacked = [B("a", 0, 0, 500, 600), B("b", 504, 0, 500, 196), B("c", 504, 200, 500, 196), B("d", 504, 404, 500, 196)];

    [Fact]
    public void Neighbor_moves_within_a_column_and_across_to_the_tall_tile()
    {
        Assert.Equal("c", ShellWindows.Neighbor(Stacked, "b", "down"));
        Assert.Equal("c", ShellWindows.Neighbor(Stacked, "d", "up"));
        Assert.Equal("a", ShellWindows.Neighbor(Stacked, "c", "left"));
        Assert.Equal("a", ShellWindows.Neighbor(Stacked, "d", "left"));
    }

    [Fact]
    public void Neighbor_goes_from_the_tall_tile_to_the_one_level_with_its_center() =>
        Assert.Equal("c", ShellWindows.Neighbor(Stacked, "a", "right"));

    [Fact]
    public void Neighbor_prefers_the_tile_straight_across_in_a_grid()
    {
        var grid = new List<(string, Box)> { B("a", 0, 0, 500, 300), B("b", 0, 304, 500, 300), B("c", 504, 0, 500, 300), B("d", 504, 304, 500, 300) };
        Assert.Equal("b", ShellWindows.Neighbor(grid, "a", "down"));
        Assert.Equal("c", ShellWindows.Neighbor(grid, "a", "right"));
        Assert.Equal("c", ShellWindows.Neighbor(grid, "d", "up"));
    }

    [Fact]
    public void Neighbor_stays_put_at_the_edge_or_for_an_unknown_tile()
    {
        Assert.Null(ShellWindows.Neighbor(Stacked, "a", "left"));
        Assert.Null(ShellWindows.Neighbor(Stacked, "b", "up"));
        Assert.Null(ShellWindows.Neighbor(Stacked, "zzz", "up"));
    }

    // ---------- attention.test.ts nextWaiting ----------

    [Fact]
    public void NextWaiting_goes_round_and_knows_when_none_is()
    {
        string[] order = ["a", "b", "c", "d"];
        var waiting = new HashSet<string> { "b", "d" };
        Assert.Equal("b", ShellWindows.NextWaiting(order, waiting, null));
        Assert.Equal("d", ShellWindows.NextWaiting(order, waiting, "b"));
        Assert.Equal("d", ShellWindows.NextWaiting(order, waiting, "c"));
        Assert.Equal("b", ShellWindows.NextWaiting(order, waiting, "d"));
        Assert.Equal("c", ShellWindows.NextWaiting(order, new HashSet<string> { "c" }, "c"));
        Assert.Null(ShellWindows.NextWaiting(order, new HashSet<string>(), "a"));
        Assert.Null(ShellWindows.NextWaiting([], new HashSet<string> { "a" }, null));
        Assert.Equal("a", ShellWindows.NextWaiting(order, new HashSet<string> { "a" }, "x"));
    }

    // ---------- windows ----------

    static WindowState WithTile(string id, string session, bool hidden = false)
    {
        var w = LayoutActions.AddTile(WindowState.Empty(id), session);
        w.Hidden = hidden;
        return w;
    }

    [Fact]
    public void A_start_shows_the_open_windows_or_the_first_when_all_were_closed()
    {
        var a = WithTile("a", "s1", hidden: true);
        var b = WithTile("b", "s2");
        Assert.Equal(["b"], ShellWindows.WindowsToShow([a, b]).Select(w => w.Id));
        b.Hidden = true;
        Assert.Equal(["a"], ShellWindows.WindowsToShow([a, b]).Select(w => w.Id));
    }

    [Fact]
    public void Saved_bounds_are_kept_only_while_the_title_bar_is_on_a_display()
    {
        Box[] screen = [new(0, 0, 1920, 1040)];
        Assert.Equal((800d, 600d, (double?)100, (double?)50), ShellWindows.VisibleBounds(new Bounds { X = 100, Y = 50, Width = 800, Height = 600 }, screen));
        Assert.Equal((800d, 600d, (double?)null, (double?)null), ShellWindows.VisibleBounds(new Bounds { X = 3000, Y = 50, Width = 800, Height = 600 }, screen));
        // A title bar just above the work area is off it.
        Assert.Equal((800d, 600d, (double?)null, (double?)null), ShellWindows.VisibleBounds(new Bounds { X = 100, Y = -30, Width = 800, Height = 600 }, screen));
        Assert.Null(ShellWindows.VisibleBounds(null, screen));
    }

    [Fact]
    public void New_and_torn_out_windows_are_placed_as_app_ts_places_them()
    {
        var near = new Bounds { X = 10, Y = 20, Width = 1000, Height = 700 };
        var created = ShellWindows.NewWindow("n", near);
        Assert.Equal((50d, 60d, 1000d, 700d), (created.Bounds!.X, created.Bounds.Y, created.Bounds.Width, created.Bounds.Height));
        Assert.Null(ShellWindows.NewWindow("n", null).Bounds);

        var torn = ShellWindows.TearOutWindow("t", Layout.Tile("s"), 500.4, 300, 640, 400);
        Assert.Equal((440d, 284d, 664d, 490d), (torn.Bounds!.X, torn.Bounds.Y, torn.Bounds.Width, torn.Bounds.Height));
        Assert.Equal(["s"], Layout.Leaves(torn.Layout));
        var clamped = ShellWindows.TearOutWindow("t", Layout.Tile("s"), 0, 0, 100, 5000).Bounds!;
        Assert.Equal((480d, 1600d), (clamped.Width, clamped.Height));
        var unsized = ShellWindows.TearOutWindow("t", Layout.Tile("s"), 0, 0, null, null).Bounds!;
        Assert.Equal((824d, 590d), (unsized.Width, unsized.Height));
    }

    [Fact]
    public void Closing_asks_only_for_a_window_with_sessions_and_never_while_the_system_goes_down()
    {
        Assert.Equal("keep", ShellWindows.ClosePolicy("ask", 0, false));
        Assert.Equal("keep", ShellWindows.ClosePolicy("end", 0, false));
        Assert.Equal("ask", ShellWindows.ClosePolicy("ask", 2, false));
        Assert.Equal("end", ShellWindows.ClosePolicy("end", 1, false));
        Assert.Equal("keep", ShellWindows.ClosePolicy("ask", 3, true));
    }

    [Fact]
    public void Only_an_empty_secondary_window_is_discarded()
    {
        var first = WindowState.Empty("first");
        var second = WindowState.Empty("second");
        var full = WithTile("full", "s");
        Assert.False(ShellWindows.ShouldDiscard([first, second], "first"));
        Assert.True(ShellWindows.ShouldDiscard([first, second], "second"));
        Assert.False(ShellWindows.ShouldDiscard([first, full], "full"));
        Assert.False(ShellWindows.ShouldDiscard([first], "gone"));
    }

    // ---------- tray ----------

    [Fact]
    public void The_tray_menu_is_tray_ts_s_with_terminate_all_and_quit_apart()
    {
        var state = AppState.CreateDefault();
        state.Windows.Clear();
        var w1 = WithTile("w1", "s1");
        // The labels follow the layout's leaf order (AddTile's splits), as windowSessions does.
        foreach (var s in new[] { "s2", "s3", "s4" }) w1 = LayoutActions.AddTile(w1, s);
        state.Windows.Add(w1);
        state.Windows.Add(WithTile("w2", "s5", hidden: true));
        state.Windows.Add(WindowState.Empty("w3"));
        foreach (var (id, name) in new[] { ("s1", "alpha"), ("s2", "beta"), ("s3", "gamma"), ("s4", "delta"), ("s5", "eps") })
            state.Sessions[id] = new SessionRecord { Id = id, ClaudeSessionId = id, Cwd = @"C:\x", Name = name };

        var menu = ShellWindows.TrayMenu(ShellWindows.TrayWindows(state), anyRunning: false);
        var flat = menu.Select(m => m.Type == "separator" ? "---" : $"{m.Type} {m.Label}{(m.Enabled ? "" : " disabled")}{(m.Checked == true ? " checked" : "")}");
        Assert.Equal(new[]
        {
            "checkbox Window 1 – alpha, delta, beta, … checked", "checkbox Window 2 – eps", "checkbox Window 3 checked", "---",
            "normal New session", "normal New window", "---",
            "normal About Reframe", "normal Terminate all sessions disabled", "normal Quit",
        }, flat);
        Assert.Equal(["window:w1", "window:w2", "window:w3", null, "newSession", "newWindow", null, "about", "terminateAll", "quit"], menu.Select(m => m.Action));
        Assert.True(ShellWindows.TrayMenu([], anyRunning: true).Single(m => m.Action == "terminateAll").Enabled);
    }

    [Fact]
    public void The_tray_tooltip_says_how_many_sessions_need_the_user()
    {
        Assert.Equal("Reframe", ShellWindows.TrayTooltip(0));
        Assert.Equal("Reframe – a session needs you", ShellWindows.TrayTooltip(1));
        Assert.Equal("Reframe – 3 sessions need you", ShellWindows.TrayTooltip(3));
    }

    [Fact]
    public void The_close_question_has_app_ts_s_texts()
    {
        var one = CloseWindowQuestion.For(1);
        Assert.Equal("End the session in this window?", one.Message);
        Assert.Equal("End the 2 sessions in this window?", CloseWindowQuestion.For(2).Message);
        Assert.Equal(("Close window", "End sessions", "Keep in the tray", "Cancel", "Don't ask again (Settings can change it)"),
            (one.Title, one.End, one.Keep, one.Cancel, one.Remember));
    }

    [Fact]
    public void The_new_texts_are_i18n_ts_s_english_ones()
    {
        var source = File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "src", "shared", "i18n.ts"));
        // The English block comes first; its entries are 'key': 'text' or "text", possibly on the next line.
        var english = source[..source.IndexOf("'tray.window': 'Fenster", StringComparison.Ordinal)];
        foreach (var key in new[]
        {
            "dialog.cancel", "tray.tooltip", "title.waitingOne", "title.waiting", "tray.window", "tray.newSession", "tray.newWindow",
            "tray.terminateAll", "tray.quit", "tray.about", "fullscreen.hint", "fullscreen.exit", "closeWindow.title",
            "closeWindow.messageOne", "closeWindow.message", "closeWindow.detail", "closeWindow.end", "closeWindow.keep", "closeWindow.remember",
        })
        {
            var match = Regex.Match(english, $@"'{Regex.Escape(key)}':\s*(['""])(.*?)\1,");
            Assert.True(match.Success, key);
            Assert.Equal(match.Groups[2].Value.Replace("\\'", "'"), Strings.En[key]);
        }
    }
}
