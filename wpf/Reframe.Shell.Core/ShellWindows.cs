namespace Reframe.Shell.Core;

/// <summary>A tray menu window entry (tray.ts TrayWindowItem).</summary>
public sealed record TrayWindowItem(string Id, string Label, bool Visible);

/// <summary>
/// reframe's windows as src/main/app.ts and windows.ts handle them: which windows a start shows, where a new or torn
/// out window goes, the close-window policy (preferences.closeWindow) and the tray's menu (src/main/tray.ts).
/// </summary>
public static class ShellWindows
{
    /// <summary>windows.ts DEFAULT_SIZE.</summary>
    public const double DefaultWidth = 1280, DefaultHeight = 800;
    /// <summary>windows.ts minWidth/minHeight.</summary>
    public const double MinWidth = 480, MinHeight = 320;

    /// <summary>windowsToShow: the windows that were open (not closed into the tray); the first one if all were closed.</summary>
    public static List<WindowState> WindowsToShow(IReadOnlyList<WindowState> windows)
    {
        var open = windows.Where(w => !w.Hidden).ToList();
        return open.Count > 0 ? open : windows.Take(1).ToList();
    }

    /// <summary>visibleBounds (windows.ts): saved bounds are kept only if the window's title strip is still on a display's
    /// work area; otherwise only the size is (the position is left to the system: null).</summary>
    public static (double Width, double Height, double? X, double? Y)? VisibleBounds(Bounds? bounds, IReadOnlyList<Box> workAreas)
    {
        if (bounds is null) return null;
        var title = new Box(bounds.X + 40, bounds.Y, Math.Max(1, bounds.Width - 80), 30);
        var onScreen = workAreas.Any(a =>
            Math.Min(title.X + title.Width, a.X + a.Width) - Math.Max(title.X, a.X) > 0
            && Math.Min(title.Y + title.Height, a.Y + a.Height) - Math.Max(title.Y, a.Y) > 0);
        return onScreen ? (bounds.Width, bounds.Height, bounds.X, bounds.Y) : (bounds.Width, bounds.Height, null, null);
    }

    /// <summary>newWindow: an empty window 40 px down and right of <paramref name="near"/>.</summary>
    public static WindowState NewWindow(string id, Bounds? near)
    {
        var win = WindowState.Empty(id);
        if (near is not null) win.Bounds = new Bounds { X = near.X + 40, Y = near.Y + 40, Width = near.Width, Height = near.Height };
        return win;
    }

    /// <summary>tearOut's new window: room for the frame, toolbar and padding around the tile, its title bar under the
    /// pointer (DIPs).</summary>
    public static WindowState TearOutWindow(string id, LayoutNode node, double x, double y, double? tileWidth, double? tileHeight)
    {
        static double Clamp(double v, double min, double max) => Math.Round(Math.Min(max, Math.Max(min, v)));
        var win = WindowState.Empty(id);
        win.Bounds = new Bounds
        {
            X = Math.Round(x - 60), Y = Math.Round(y - 16),
            Width = Clamp((tileWidth ?? 800) + 24, 480, 2400),
            Height = Clamp((tileHeight ?? 500) + 90, 320, 1600),
        };
        win.Layout = node;
        return win;
    }

    /// <summary>closeWindow: what closing a window does before anything is asked: "keep" (hide) for a window without
    /// sessions or while the system goes down, else the preference ("ask", "end" or "keep").</summary>
    public static string ClosePolicy(string preference, int sessions, bool systemEnding) =>
        sessions == 0 || systemEnding ? "keep" : preference;

    /// <summary>closeIfEmpty: a secondary window goes for good once nothing is left in it; the first one always stays.</summary>
    public static bool ShouldDiscard(IReadOnlyList<WindowState> windows, string windowId) =>
        windows.Count > 0 && windows[0].Id != windowId && windows.FirstOrDefault(w => w.Id == windowId) is { } win && LayoutActions.IsWindowEmpty(win);

    /// <summary>The tray's window entry: "Window n", with up to three session labels after a dash.</summary>
    public static string TrayWindowLabel(int index, IReadOnlyList<string> labels)
    {
        var names = labels.Count > 3 ? string.Join(", ", labels.Take(3)) + ", …" : string.Join(", ", labels);
        return Strings.T("tray.window", ("n", index + 1)) + (names.Length > 0 ? " – " + names : "");
    }

    public static List<TrayWindowItem> TrayWindows(AppState state) =>
        [.. state.Windows.Select((w, i) => new TrayWindowItem(w.Id, TrayWindowLabel(i,
            [.. LayoutActions.WindowSessions(w).Select(id => state.Sessions.GetValueOrDefault(id)).OfType<SessionRecord>()
                .Select(r => StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd))]), !w.Hidden))];

    /// <summary>tray.ts's menu: the windows (checked while shown), New session, New window, About, Terminate all sessions
    /// (only while one runs) and Quit. The update entry is left out: the shell has no updater (deployed by script).</summary>
    public static List<MenuSpec> TrayMenu(IReadOnlyList<TrayWindowItem> windows, bool anyRunning) =>
    [
        .. windows.Select(w => new MenuSpec("checkbox", w.Label, Checked: w.Visible, Action: "window:" + w.Id)),
        MenuSpec.Separator,
        new("normal", Strings.T("tray.newSession"), Action: "newSession"),
        new("normal", Strings.T("tray.newWindow"), Action: "newWindow"),
        MenuSpec.Separator,
        new("normal", Strings.T("tray.about"), Action: "about"),
        new("normal", Strings.T("tray.terminateAll"), Enabled: anyRunning, Action: "terminateAll"),
        new("normal", Strings.T("tray.quit"), Action: "quit"),
    ];

    /// <summary>The tray's tooltip: "Reframe", or how many sessions need the user (waitingText).</summary>
    public static string TrayTooltip(int waiting) => waiting switch
    {
        0 => Strings.T("tray.tooltip"),
        1 => Strings.T("title.waitingOne"),
        _ => Strings.T("title.waiting", ("n", waiting)),
    };

    /// <summary>nextWaiting (src/shared/attention.ts): the first waiting session after <paramref name="after"/> in
    /// <paramref name="order"/>, going round.</summary>
    public static string? NextWaiting(IReadOnlyList<string> order, IReadOnlySet<string> waiting, string? after)
    {
        var start = after is null ? -1 : order.ToList().IndexOf(after);
        for (var step = 1; step <= order.Count; step++)
        {
            var id = order[((start + step) % order.Count + order.Count) % order.Count];
            if (waiting.Contains(id)) return id;
        }
        return null;
    }

    /// <summary>neighbor (src/renderer/focus-nav.ts): the tile next to <paramref name="fromId"/> in a direction: of the
    /// tiles on that side the nearest; among equally near ones, the one level with the current tile's centre.</summary>
    public static string? Neighbor(IReadOnlyList<(string Id, Box Box)> boxes, string fromId, string direction)
    {
        var found = boxes.Where(b => b.Id == fromId).ToList();
        if (found.Count == 0) return null;
        var from = found[0].Box;
        var horizontal = direction is "left" or "right";
        var center = horizontal ? from.Y + from.Height / 2 : from.X + from.Width / 2;
        static double Outside(double v, double min, double max) => v < min ? min - v : v > max ? v - max : 0;
        (string Id, double Gap, double Offset)? best = null;
        foreach (var (id, box) in boxes)
        {
            if (id == fromId) continue;
            var gap = direction switch
            {
                "left" => from.X - (box.X + box.Width),
                "right" => box.X - (from.X + from.Width),
                "up" => from.Y - (box.Y + box.Height),
                _ => box.Y - (from.Y + from.Height),
            };
            if (gap < -1) continue; // not on that side (a pixel of overlap is rounding)
            var offset = horizontal ? Outside(center, box.Y, box.Y + box.Height) : Outside(center, box.X, box.X + box.Width);
            var nearer = best is not { } b || gap < b.Gap - 1 || (Math.Abs(gap - b.Gap) <= 1 && offset < b.Offset);
            if (nearer) best = (id, gap, offset);
        }
        return best?.Id;
    }
}

/// <summary>The close-window question as app.ts askToClose puts it (Electron's dialog.showMessageBox: a task dialog with
/// the message as main instruction, the detail as content, End and Keep as command links, Cancel a common button,
/// Keep the default, and the remember check box).</summary>
public sealed record CloseWindowQuestion(string Title, string Message, string Detail, string End, string Keep, string Cancel, string Remember)
{
    public static CloseWindowQuestion For(int count) => new(
        Strings.T("closeWindow.title"),
        count == 1 ? Strings.T("closeWindow.messageOne") : Strings.T("closeWindow.message", ("count", count)),
        Strings.T("closeWindow.detail"),
        Strings.T("closeWindow.end"), Strings.T("closeWindow.keep"), Strings.T("dialog.cancel"), Strings.T("closeWindow.remember"));
}
