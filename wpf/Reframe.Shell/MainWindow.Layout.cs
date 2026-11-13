using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using Reframe.Shell.Core;
using WindowState = Reframe.Shell.Core.WindowState;

namespace Reframe.Shell;

/// <summary>A session's tile (Tile.tsx): the focus ring, the frame with its header and the terminal (or, without a
/// running session, the ended card). Kept for as long as the session is in the window, collapsed ones too, as the
/// terminal's HwndHost does not survive being taken out of the visual tree and put back (a native crash).</summary>
sealed class TileView
{
    public required string SessionId { get; init; }
    /// <summary>The 1px accent ring outside the tile while it has focus (.tile:focus-within's box-shadow).</summary>
    public required Border Ring { get; init; }
    public required Border Frame { get; init; }
    public required Border Head { get; init; }
    public required StatusDot Dot { get; init; }
    public required TextBlock Label { get; init; }
    public required TextBlock Cwd { get; init; }
    public required VectorIcon ZoomIcon { get; init; }
    public required Button Zoom { get; init; }
    public TerminalControl? Control { get; init; }
    public HostTerminalConnection? Terminal { get; init; }
    public TerminalBehaviour? Behaviour { get; init; }
}

/// <summary>A drag of a tile's or a group's header (dnd.ts): the node, where it began, whether it moved yet.</summary>
sealed record HeaderDrag(string Key, bool Group, Point Start, UIElement Source)
{
    public bool Moving { get; set; }
    public DropTarget? Target { get; set; }
}

public partial class MainWindow
{
    /// <summary>The layout area (.layout: 6px padding) and the canvas its tiles, group frames and separators are placed on.</summary>
    readonly Canvas surface = new() { ClipToBounds = false };
    readonly StackPanel dockItems = new();
    readonly Border dockStrip = new();
    readonly Dictionary<string, TileView> tiles = [];
    readonly Dictionary<string, Border> frames = [];
    readonly Dictionary<string, Thumb> separators = [];
    readonly Border dropPreview = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    /// <summary>Each window's grid key as last saved (LayoutActions.SettleGrid).</summary>
    readonly Dictionary<string, string> gridKeys = [];
    /// <summary>A tile filling the window while the layout's tiles are what they were when it was zoomed.</summary>
    (string SessionId, string Layout)? zoom;
    /// <summary>The selected thread: the one a single window shows (ViewModes.NextShown keeps it).</summary>
    string? selected;
    /// <summary>A single window whose shown thread closed shows none (<see cref="pick"/>) until one is picked.</summary>
    bool blank;
    readonly FrameworkElement pick = PickState();
    /// <summary>The tile the user types in (its accent ring).</summary>
    string? focused;
    LayoutPlaces places = new([], [], []);
    HeaderDrag? headerDrag;
    (string SplitId, int Index, List<double> Before, Box SplitBox, bool Across)? separatorDrag;

    /// <summary>This window's entry of the state (a capture takes its fixture's first window).</summary>
    WindowState Win
    {
        get
        {
            if (windowId is not null && state.Windows.FirstOrDefault(w => w.Id == windowId) is { } own) return own;
            if (state.Windows.Count == 0) state.Windows.Add(Reframe.Shell.Core.WindowState.Empty(Guid.NewGuid().ToString()));
            windowId = state.Windows[0].Id;
            return state.Windows[0];
        }
        set
        {
            var index = state.Windows.FindIndex(w => w.Id == value.Id);
            if (index >= 0) state.Windows[index] = value;
        }
    }

    string LayoutKey(WindowState win) => string.Join("|", Layout.Leaves(win.Layout));

    /// <summary>The layout column: the dock strip of collapsed tiles, then the layout area (or the empty state).</summary>
    FrameworkElement LayoutColumn()
    {
        // .dock: 34px, 6px 4px padding, surface, border-right; items 4px apart.
        dockStrip.Width = 34;
        dockStrip.Padding = new Thickness(4, 6, 4, 6);
        dockStrip.Background = B("surfaceBrush");
        dockStrip.BorderBrush = B("borderBrush");
        dockStrip.BorderThickness = new Thickness(0, 0, 1, 0);
        dockStrip.Child = dockItems;
        dockStrip.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(dockStrip, "Collapsed sessions");
        DockPanel.SetDock(dockStrip, Dock.Left);

        dropPreview.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xd9, 0x77, 0x57));
        dropPreview.BorderBrush = B("accentBrush");
        dropPreview.BorderThickness = new Thickness(2);
        dropPreview.CornerRadius = new CornerRadius(6);
        Panel.SetZIndex(dropPreview, 20);
        surface.Children.Add(dropPreview);
        surface.SizeChanged += (_, _) => Relayout();
        AutomationProperties.SetName(surface, "Layout");

        var area = new Grid();
        area.Children.Add(new Border { Padding = new Thickness(6), Child = surface });
        area.Children.Add(empty);
        area.Children.Add(pick);
        var column = new DockPanel();
        column.Children.Add(dockStrip);
        column.Children.Add(area);
        return column;
    }

    // ---------- state changes ----------

    /// <summary>Changes the window (mutateWindow): keeps a tiled window in its grid, the selection and zoom in step,
    /// writes the state and lays the window out again.</summary>
    void Dispatch(Func<WindowState, WindowState> change)
    {
        var before = Win;
        var next = change(before);
        if (ReferenceEquals(next, before)) return;
        var (settled, key) = LayoutActions.SettleGrid(next, gridKeys.GetValueOrDefault(next.Id));
        gridKeys[next.Id] = key;
        var shownBefore = selected;
        selected = ViewModes.NextShown(Layout.Leaves(before.Layout), Layout.Leaves(settled.Layout), selected);
        if (shownBefore is not null && selected is null) blank = true;
        Win = settled;
        Saved();
        Relayout();
    }

    void Act(LayoutAction action) => Dispatch(w => LayoutActions.Apply(w, action));

    /// <summary>The state was (re)loaded: the window's grid key as seen first, its first thread selected.</summary>
    void LayoutLoaded()
    {
        FontSizeLoaded();
        gridKeys[Win.Id] = LayoutActions.GridKey(Win);
        selected ??= Layout.Leaves(Win.Layout).FirstOrDefault();
        Relayout();
    }

    /// <summary>"Group n" with the next free colour (newGroupLook), over every window's groups.</summary>
    (string Name, string Color) NewGroupLook()
    {
        var existing = state.Windows.SelectMany(LayoutActions.WindowGroups).ToList();
        static string NameFor(int n) => $"Group {n}";
        return (NameFor(GroupColors.NextGroupNumber([.. existing.Select(g => g.Name)], NameFor)),
            GroupColors.NextGroupColor([.. existing.Select(g => g.Color)]));
    }

    // ---------- layout ----------

    /// <summary>Places everything as the window's layout, view mode and zoom say (App.tsx's main.layout).</summary>
    void Relayout()
    {
        var win = Win;
        var windowSessions = LayoutActions.WindowSessions(win);
        foreach (var id in tiles.Keys.Where(id => !windowSessions.Contains(id)).ToList()) DisposeTile(id);
        var leaves = Layout.Leaves(win.Layout);
        foreach (var id in windowSessions) EnsureTile(id);

        var single = LayoutActions.ViewModeOf(win) == "single";
        ApplyViewMode(LayoutActions.ViewModeOf(win));
        if (selected is not null || !single) blank = false;
        var shown = single ? (selected is not null && leaves.Contains(selected) ? selected : blank ? null : leaves.FirstOrDefault()) : null;
        if (zoom is { } z && (z.Layout != LayoutKey(win) || !leaves.Contains(z.SessionId))) zoom = null;
        var one = shown ?? zoom?.SessionId;
        var area = new Box(0, 0, surface.ActualWidth, surface.ActualHeight);
        places = one is not null ? new([new TilePlace(one, area, null)], [], [])
            : single ? new([], [], []) : LayoutGeometry.Compute(win.Layout, area);

        foreach (var view in tiles.Values)
        {
            var place = places.Tiles.FirstOrDefault(p => p.SessionId == view.SessionId);
            if (place is null)
            {
                view.Ring.Visibility = Visibility.Hidden;
                continue;
            }
            Put(view.Ring, place.Box with { X = place.Box.X - 1, Y = place.Box.Y - 1, Width = place.Box.Width + 2, Height = place.Box.Height + 2 });
            view.Ring.Visibility = Visibility.Visible;
            var filling = view.SessionId == one;
            view.ZoomIcon.Kind = filling ? "unzoom" : "zoom";
            var name = filling ? "Show all tiles again" : "Fill the window";
            view.Zoom.ToolTip = $"{name} (Ctrl+Shift+Z)";
            AutomationProperties.SetName(view.Zoom, name);
            ShowFocus(view);
        }

        // Group frames, by group id (kept while the group is shown, so a drag of its header goes on).
        var shownGroups = places.Groups.Select(g => g.Group.Id).ToHashSet();
        foreach (var id in frames.Keys.Where(id => !shownGroups.Contains(id)).ToList())
        {
            surface.Children.Remove(frames[id]);
            frames.Remove(id);
        }
        foreach (var g in places.Groups)
        {
            if (!frames.TryGetValue(g.Group.Id, out var frame))
            {
                frame = new Border();
                Panel.SetZIndex(frame, 0);
                surface.Children.Add(frame);
                frames[g.Group.Id] = frame;
            }
            FillGroupFrame(frame, g.Group);
            Put(frame, g.Box);
        }

        // Separators, by split and index (kept while their split is, so a drag goes on).
        var shownSeparators = places.Separators.Select(s => $"{s.Split.Id}:{s.Index}").ToHashSet();
        foreach (var id in separators.Keys.Where(id => !shownSeparators.Contains(id)).ToList())
        {
            surface.Children.Remove(separators[id]);
            separators.Remove(id);
        }
        foreach (var s in places.Separators)
        {
            var id = $"{s.Split.Id}:{s.Index}";
            if (!separators.TryGetValue(id, out var thumb))
            {
                thumb = Separator(s.Split.Id, s.Index, s.Split.Dir == "row");
                surface.Children.Add(thumb);
                separators[id] = thumb;
            }
            Put(thumb, s.Box);
        }

        UpdateDockStrip(win);
        var any = win.Layout is not null;
        surface.Visibility = any ? Visibility.Visible : Visibility.Hidden;
        empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        pick.Visibility = any && single && one is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A single window showing no thread: the next one is the user's choice.</summary>
    static FrameworkElement PickState() => new TextBlock
    {
        Text = "Choose a session from the list.", Style = S("Text13"), Foreground = B("mutedBrush"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
    };

    static void Put(FrameworkElement element, Box box)
    {
        Canvas.SetLeft(element, box.X);
        Canvas.SetTop(element, box.Y);
        element.Width = Math.Max(0, box.Width);
        element.Height = Math.Max(0, box.Height);
    }

    // ---------- tiles ----------

    string LabelOf(string sessionId) =>
        state.Sessions.TryGetValue(sessionId, out var r) ? StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd) : sessionId;

    void EnsureTile(string sessionId)
    {
        if (tiles.ContainsKey(sessionId)) return;
        var record = state.Sessions.GetValueOrDefault(sessionId);
        var label = LabelOf(sessionId);
        var cwd = record?.Cwd ?? "";
        var provider = record?.Launch.Provider ?? "chatgpt";

        TerminalControl? control = null;
        HostTerminalConnection? terminal = null;
        TerminalBehaviour? behaviour = null;
        var notice = new TextBlock { Style = S("Text12"), Foreground = B("textBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        // .terminal-notice (rgba(40,43,51,.95), radius 6, 4px 10px, 12px): in the header, as nothing can be over the terminal.
        var noticePill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xf2, 0x28, 0x2b, 0x33)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 2, 10, 2),
            Child = notice, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, MaxWidth = 360, IsHitTestVisible = false,
        };
        var noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); noticePill.Visibility = Visibility.Collapsed; };
        void ShowNotice(string text)
        {
            notice.Text = text;
            noticePill.Visibility = Visibility.Visible;
            noticeTimer.Stop();
            noticeTimer.Start();
        }
        if (shell.ConnectionOf(sessionId) is { IsOpen: true } host && hostSessions.ContainsKey(sessionId) && shell.ErrorOf(sessionId) is null)
        {
            control = new TerminalControl { AutoResize = true };
            terminal = new HostTerminalConnection(host.CreateView(sessionId), control);
            behaviour = new TerminalBehaviour(control, terminal, fontSize.Size, () => WslOf(sessionId), ShowNotice);
            var connected = false;
            var c = control;
            var t = terminal;
            control.Loaded += (_, _) =>
            {
                if (connected) return;
                connected = true;
                c.Connection = t;
            };
        }

        // .tile-header: 30px, 0 4px padding, 6px gaps, surface, border-bottom.
        var header = new DockPanel { LastChildFill = true };
        void Left(UIElement element, double gap = 6)
        {
            if (element is FrameworkElement f && header.Children.Count > 0) f.Margin = new Thickness(gap, 0, 0, 0);
            DockPanel.SetDock(element, Dock.Left);
            header.Children.Add(element);
        }
        void Right(UIElement element)
        {
            ((FrameworkElement)element).Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(element, Dock.Right);
            header.Children.Add(element);
        }
        var collapse = TileButton("collapse", "Collapse to the side bar", "Collapse to the side bar (Ctrl+Shift+M)");
        collapse.Click += (_, _) => Act(new LayoutAction.TileCollapse(sessionId));
        Left(collapse);
        Left(new VectorIcon { Kind = "grip", Size = 14, Foreground = B("mutedBrush"), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var dot = new StatusDot { Status = StatusOf(sessionId), VerticalAlignment = VerticalAlignment.Center };
        Left(dot);
        Left(new VectorIcon
        {
            Kind = provider == "anthropic" ? "anthropic" : "chatgpt", Size = 16, VerticalAlignment = VerticalAlignment.Center,
            Foreground = B(provider == "anthropic" ? "providerAnthropicBrush" : "providerChatgptBrush"),
        });
        var title = new TextBlock
        {
            Text = label, Style = S("Text13"), Foreground = B("textBrush"), FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Double-click to rename",
        };
        Left(title);
        var close = TileButton("close", "End session", "End session");
        close.Click += (_, _) => CloseTile(sessionId);
        Right(close);
        var zoomIcon = new VectorIcon { Kind = "zoom", Size = 14 };
        var zoomButton = new Button { Style = S("IconButton"), Content = zoomIcon, VerticalAlignment = VerticalAlignment.Center };
        zoomButton.Click += (_, _) => ToggleZoom(sessionId);
        Right(zoomButton);
        Right(noticePill);
        // .tile-cwd: cut off at the start, read left to right.
        var cwdText = new TextBlock
        {
            Text = RelativeTime.HomeRelative(cwd, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), Style = S("Text12"),
            Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = cwd,
        };
        header.Children.Add(cwdText);

        var head = new Border
        {
            Height = 30, Padding = new Thickness(4, 0, 4, 0), Background = B("surfaceBrush"), BorderBrush = B("borderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(5, 5, 0, 0), Child = header,
            ToolTip = "Drag to move", Cursor = Cursors.SizeAll,
        };
        // .tile-label: max-width 40% of the header.
        head.SizeChanged += (_, _) => title.MaxWidth = Math.Max(0, head.ActualWidth * 0.4);
        DockPanel.SetDock(head, Dock.Top);
        head.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu(TileMenuOf(sessionId), head, null, sessionId);
            e.Handled = true;
        };
        HeaderDragSource(head, Layout.TileKey(sessionId), false);

        var body = new DockPanel();
        body.Children.Add(head);
        // .tile-terminal: inset 4px 2px 2px 6px; without a running session, .tile-overlay's card.
        // The find bar sits above the terminal (not over it: the terminal is a native window WPF cannot draw on).
        if (behaviour is not null)
        {
            DockPanel.SetDock(behaviour.FindBar, Dock.Top);
            body.Children.Add(behaviour.FindBar);
        }
        body.Children.Add(control is not null ? new Border { Margin = new Thickness(6, 4, 2, 2), Child = control } : EndedCard(sessionId));
        var frame = new Border
        {
            BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = B("terminalBrush"), Child = body, SnapsToDevicePixels = true,
        };
        var ring = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = frame, SnapsToDevicePixels = true };
        AutomationProperties.SetName(ring, label);
        Panel.SetZIndex(ring, 1);
        var view = new TileView
        {
            SessionId = sessionId, Ring = ring, Frame = frame, Head = head, Dot = dot, Label = title, Cwd = cwdText,
            ZoomIcon = zoomIcon, Zoom = zoomButton, Control = control, Terminal = terminal, Behaviour = behaviour,
        };
        // Files (and images from a browser) dropped on the tile are typed in as their paths, as in reframe.
        if (behaviour is not null)
        {
            frame.AllowDrop = true;
            frame.DragOver += (_, e) =>
            {
                if (headerDrag is not null) return;
                e.Effects = TerminalBehaviour.CanDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            frame.Drop += (_, e) =>
            {
                if (headerDrag is null && behaviour.Drop(e.Data)) e.Handled = true;
            };
        }
        frame.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (frame.IsKeyboardFocusWithin) focused = sessionId;
            else if (focused == sessionId) focused = null;
            ShowFocus(view);
        };
        body.PreviewMouseDown += (_, e) =>
        {
            selected = sessionId;
            // A press on a button (×, zoom, collapse, the ended card's) stays with it: moving the focus into the
            // terminal's native window took the press away, so the click was lost and the header flickered.
            for (var at = e.OriginalSource as DependencyObject; at is not null && at != body; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is ButtonBase) return;
            if (control is not null && FindHost(control) is { } host) FocusTerminal(host);
        };
        tiles[sessionId] = view;
        surface.Children.Add(ring);
        ring.Visibility = Visibility.Hidden;
    }

    /// <summary>.tile-overlay and its card (Tile.tsx): the session ended, or why it could not start, with Resume (Take
    /// over), Start a new session here and Close as the error allows. Beside the terminal, not over it (a native window).</summary>
    FrameworkElement EndedCard(string sessionId)
    {
        var wsl = state.Sessions.TryGetValue(sessionId, out var record) ? WslPaths.Parse(record.Cwd)?.Distro : null;
        var card = TileCard.For(shell.ErrorOf(sessionId), wsl);
        // .tile-overlay-card p: centred, 12 px above the buttons.
        var text = new TextBlock { Text = card.Text, Style = S("Text13"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        Button Add(string label, Style style, Action click)
        {
            var button = new Button { Style = style, Content = new TextBlock { Text = label, Style = S("Text13") }, Margin = new Thickness(buttons.Children.Count > 0 ? 8 : 0, 0, 0, 0) };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => click();
            buttons.Children.Add(button);
            return button;
        }
        if (card.Primary is { } primary) Add(primary, S("PrimaryButton"), () => { if (Writable) shell.RestartSession(WindowId, sessionId, false); });
        if (card.StartFresh) Add(Strings.T("tile.startFresh"), S("ButtonBaseStyle"), () => { if (Writable) shell.RestartSession(WindowId, sessionId, true); });
        Add(card.Close, S("ButtonBaseStyle"), () => CloseTile(sessionId));
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        var frame = new Border
        {
            MaxWidth = 420, Padding = new Thickness(20, 16, 20, 16), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Background = B("surfaceBrush"), Child = panel,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(frame, card.Text);
        return new Border { Background = new SolidColorBrush(Color.FromArgb(0xc7, 0x17, 0x18, 0x1c)), Padding = new Thickness(16), Child = frame };
    }

    /// <summary>.tile:focus-within: the accent border with a 1px accent ring, and the lighter header.</summary>
    void ShowFocus(TileView view)
    {
        var on = focused == view.SessionId;
        view.Frame.BorderBrush = on ? B("accentBrush") : B("borderBrush");
        view.Ring.BorderBrush = on ? B("accentBrush") : Brushes.Transparent;
        view.Head.Background = on ? B("surface-2Brush") : B("surfaceBrush");
    }

    void DisposeTile(string sessionId)
    {
        if (!tiles.Remove(sessionId, out var view)) return;
        view.Terminal?.Close(); // detaches the view only; the session goes on
        surface.Children.Remove(view.Ring);
        if (focused == sessionId) focused = null;
    }

    /// <summary>A tile's × (and the card's Close): endSession, the session ended in the host and out of the window,
    /// kept in the list.</summary>
    void CloseTile(string sessionId)
    {
        if (!Writable) return;
        shell.EndSession(WindowId, sessionId);
    }

    void ToggleZoom(string sessionId)
    {
        var win = Win;
        // A single window's tile counts as zoomed: its button goes back to the tiled view.
        if (LayoutActions.ViewModeOf(win) == "single")
        {
            Act(new LayoutAction.WindowViewMode("tiled"));
            return;
        }
        zoom = zoom?.SessionId == sessionId ? null : (sessionId, LayoutKey(win));
        Relayout();
    }

    void UpdateTileStatus(string sessionId)
    {
        if (tiles.TryGetValue(sessionId, out var view)) view.Dot.Status = StatusOf(sessionId);
        foreach (var dot in dockItems.Children.OfType<FrameworkElement>().Where(d => d.Tag as string == sessionId))
            if (dot is Border { Child: StackPanel panel } && panel.Children[0] is StatusDot s) s.Status = StatusOf(sessionId);
    }

    void SelectTile(string sessionId)
    {
        selected = sessionId;
        Relayout();
        if (tiles.TryGetValue(sessionId, out var view) && view.Control is { } control && FindHost(control) is { } host) FocusTerminal(host);
    }

    // ---------- separators ----------

    Thumb Separator(string splitId, int index, bool across)
    {
        // .separator: 6px, transparent; hover and drag: border-strong.
        var template = new ControlTemplate(typeof(Thumb));
        var bar = new FrameworkElementFactory(typeof(Border), "Bar");
        bar.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = bar;
        foreach (var property in new[] { UIElement.IsMouseOverProperty, Thumb.IsDraggingProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, B("border-strongBrush"), "Bar"));
            template.Triggers.Add(trigger);
        }
        var thumb = new Thumb { Template = template, Cursor = across ? Cursors.SizeWE : Cursors.SizeNS, Focusable = false };
        AutomationProperties.SetName(thumb, "Resize");
        Panel.SetZIndex(thumb, 2);
        thumb.DragStarted += (_, _) =>
        {
            var place = places.Separators.FirstOrDefault(s => s.Split.Id == splitId && s.Index == index);
            if (place is null || !Writable) return;
            separatorDrag = (splitId, index, Layout.ScaleTo100(place.Split.Sizes), place.SplitBox, across);
        };
        thumb.DragDelta += (_, _) =>
        {
            if (separatorDrag is not { } drag) return;
            var p = Mouse.GetPosition(surface);
            var sizes = LayoutGeometry.Drag(drag.Before, drag.Index, drag.SplitBox, drag.Across, drag.Across ? p.X : p.Y);
            // Snapped to the layout's 12-step grid (.layout, padding included), unless Alt is held.
            if (state.Preferences.SnapToGrid && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                sizes = Snap.SnapSizes(sizes, drag.Before,
                    new Snap.SplitAxis(drag.Across ? drag.SplitBox.X : drag.SplitBox.Y, drag.Across ? drag.SplitBox.Width : drag.SplitBox.Height, LayoutGeometry.SeparatorSize),
                    new Snap.GridAxis(-6, (drag.Across ? surface.ActualWidth : surface.ActualHeight) + 12, Snap.GridSteps));
            var win = Win;
            Win = LayoutActions.Apply(win, new LayoutAction.SplitResize(drag.SplitId, sizes));
            Relayout();
        };
        thumb.DragCompleted += (_, _) =>
        {
            if (separatorDrag is not { } drag) return;
            separatorDrag = null;
            var sizes = (Layout.FindNode(Win.Layout, drag.SplitId) as SplitNode)?.Sizes;
            if (sizes is not null) Act(new LayoutAction.SplitResize(drag.SplitId, [.. sizes]));
        };
        return thumb;
    }

    // ---------- groups ----------

    /// <summary>GroupFrame.tsx: the coloured 2px frame (7% of the colour inside) and its header: collapse, grip,
    /// colour, name, count, options.</summary>
    void FillGroupFrame(Border frame, GroupNode group)
    {
        var color = (Color)ColorConverter.ConvertFromString(group.Color);
        frame.BorderBrush = new SolidColorBrush(color);
        frame.BorderThickness = new Thickness(2);
        frame.CornerRadius = new CornerRadius(8);
        frame.Background = new SolidColorBrush(Color.FromArgb(0x12, color.R, color.G, color.B));
        frame.Padding = new Thickness(4, 0, 4, 4);
        frame.SnapsToDevicePixels = true;
        AutomationProperties.SetName(frame, $"Group {group.Name}");
        if (frame.Tag is string key && key == $"{group.Name}|{group.Color}|{Layout.Leaves(group).Count}") return;
        frame.Tag = $"{group.Name}|{group.Color}|{Layout.Leaves(group).Count}";

        var header = new DockPanel { Height = 28, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, LastChildFill = false };
        void Left(FrameworkElement element)
        {
            if (header.Children.Count > 0) element.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(element, Dock.Left);
            header.Children.Add(element);
        }
        var collapse = TileButton("collapse", "Collapse the group to the side bar", "Collapse the group to the side bar");
        collapse.Click += (_, _) => Act(new LayoutAction.GroupCollapse(group.Id));
        Left(collapse);
        Left(new VectorIcon { Kind = "grip", Size = 14, Foreground = B("mutedBrush"), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var swatch = new Button
        {
            Width = 12, Height = 12, ToolTip = "Color", Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Template = SwatchTemplate(new SolidColorBrush(color)),
        };
        AutomationProperties.SetName(swatch, "Color");
        swatch.Click += (_, _) => ShowGroupMenu(ContextMenus.ColorMenu(group), swatch, group.Id);
        Left(swatch);
        Left(new TextBlock
        {
            Text = group.Name, Style = S("Text13"), Foreground = B("textBrush"), FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Double-click to rename",
        });
        Left(new TextBlock { Text = Layout.Leaves(group).Count.ToString(), Style = S("Text12"), Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        var more = TileButton("more", "Group options", "Group options");
        more.Click += (_, _) => ShowGroupMenu(ContextMenus.GroupMenu(group), more, group.Id);
        DockPanel.SetDock(more, Dock.Right);
        header.Children.Add(more);
        header.MouseRightButtonUp += (_, e) =>
        {
            ShowGroupMenu(ContextMenus.GroupMenu(group), header, group.Id);
            e.Handled = true;
        };
        header.ToolTip = "Drag to move the group";
        header.Cursor = Cursors.SizeAll;
        HeaderDragSource(header, group.Id, true);
        frame.Child = header;
    }

    static ControlTemplate SwatchTemplate(Brush fill)
    {
        var template = new ControlTemplate(typeof(Button));
        var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        dot.SetValue(System.Windows.Shapes.Shape.FillProperty, fill);
        template.VisualTree = dot;
        return template;
    }

    void ShowGroupMenu(List<MenuSpec> items, FrameworkElement target, string groupId)
    {
        var menu = ShellMenus.Build(items, _ => Writable, action => RunGroup(action, groupId));
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    void RunGroup(string action, string groupId)
    {
        var group = LayoutActions.WindowGroups(Win).FirstOrDefault(g => g.Id == groupId);
        if (group is null) return;
        switch (action)
        {
            case "renameGroup":
                if (RenameDialog.Ask(this, group.Name) is { } name) Act(new LayoutAction.GroupRename(groupId, name));
                break;
            case "dissolve":
                Dispatch(w => LayoutActions.Copy(w, Layout.DissolveGroup(w.Layout, groupId), w.Dock));
                break;
            case "endAll":
                // endGroup: each ends as its tile's × ends it.
                foreach (var id in Layout.Leaves(group).ToList()) shell.EndSession(WindowId, id);
                break;
            default:
                if (action.StartsWith("color:", StringComparison.Ordinal) && GroupColors.IsGroupColor(action[6..]))
                    Dispatch(w => LayoutActions.PatchGroup(w, groupId, color: action[6..]));
                break;
        }
    }

    /// <summary>The tile menu's group items and the list menu's "Open in a new group".</summary>
    bool RunGroupAction(string action, string? sessionId)
    {
        if (sessionId is null) return false;
        switch (action)
        {
            case "newGroup":
                var (name, color) = NewGroupLook();
                Dispatch(w => LayoutActions.Copy(w, Layout.GroupTile(w.Layout, sessionId, name, color), w.Dock));
                return true;
            case "removeFromGroup":
                Dispatch(w => LayoutActions.Copy(w, Layout.RemoveFromGroup(w.Layout, sessionId), w.Dock));
                return true;
            case "group":
                if (!CanShow(sessionId)) return true;
                var look = NewGroupLook();
                Dispatch(w =>
                {
                    var group = new GroupNode { Id = Guid.NewGuid().ToString(), Name = look.Name, Color = look.Color, Child = Layout.Tile(sessionId) };
                    return LayoutActions.PutNode(LayoutActions.DropSession(w, sessionId), group, null);
                });
                if (!Runs(sessionId)) shell.Resume(WindowId, sessionId);
                return true;
        }
        if (action.StartsWith("addToGroup:", StringComparison.Ordinal))
        {
            var groupId = action["addToGroup:".Length..];
            Dispatch(w => LayoutActions.Copy(w, Layout.AddToGroup(w.Layout, sessionId, groupId), w.Dock));
            return true;
        }
        return false;
    }

    // ---------- dock strip ----------

    /// <summary>DockStrip.tsx: the collapsed tiles and groups (another project's hidden while one is active); a click
    /// puts them back where they were.</summary>
    void UpdateDockStrip(WindowState win)
    {
        string[] urgency = ["waiting", "error", "busy", "starting", "background", "idle", "ended"];
        var entries = win.ActiveProject is { } project
            ? win.Dock.Where(e => LayoutActions.DockSessions(e).Any(id =>
                state.Sessions.GetValueOrDefault(id)?.Cwd is { } cwd && Workspaces.WorkspaceKey(cwd) == project)).ToList()
            : win.Dock;
        dockItems.Children.Clear();
        foreach (var entry in entries)
        {
            Border item;
            if (entry is DockGroup g)
            {
                var statuses = Layout.Leaves(g.Node).Select(StatusOf).ToList();
                var status = urgency.FirstOrDefault(statuses.Contains) ?? "ended";
                var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(g.Node.Color));
                item = DockItem($"Show group {g.Node.Name} again ({statuses.Count})", status, g.Node.Name, statuses.Count, color);
                item.Tag = g.Node.Id;
                item.MouseLeftButtonUp += (_, _) => Act(new LayoutAction.GroupExpand(g.Node.Id));
            }
            else
            {
                var id = ((DockTile)entry).SessionId;
                var label = LabelOf(id);
                item = DockItem($"Show {label} again", StatusOf(id), label, null, null);
                item.Tag = id;
                item.MouseLeftButtonUp += (_, _) => Act(new LayoutAction.TileExpand(id));
            }
            dockItems.Children.Add(item);
        }
        dockStrip.Visibility = dockItems.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>.dock-item: 8px 0 padding, border, surface-2, radius 6; the status dot and the label written bottom to
    /// top, 6px apart; a group's in its colour with a 3px left edge and its count.</summary>
    Border DockItem(string tooltip, string status, string label, int? count, Brush? groupColor)
    {
        var panel = new StackPanel();
        panel.Children.Add(new StatusDot { Status = status, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new Unbounded
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = label, Style = S("Text12"), Foreground = B("textBrush"), MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 6, 0, 0), LayoutTransform = new RotateTransform(-90),
            },
        });
        if (count is { } n)
            panel.Children.Add(new TextBlock { Text = n.ToString(), Style = S("Text11"), Foreground = B("mutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        var item = new Border
        {
            Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(0, 8, 0, 8), CornerRadius = new CornerRadius(6),
            BorderBrush = groupColor ?? B("borderBrush"), BorderThickness = groupColor is null ? new Thickness(1) : new Thickness(3, 1, 1, 1),
            Background = B("surface-2Brush"), Child = panel, Cursor = Cursors.Hand, ToolTip = tooltip,
        };
        AutomationProperties.SetName(item, tooltip);
        if (groupColor is null)
        {
            item.MouseEnter += (_, _) => item.BorderBrush = B("border-strongBrush");
            item.MouseLeave += (_, _) => item.BorderBrush = B("borderBrush");
        }
        return item;
    }

    // ---------- dragging tiles and groups ----------

    /// <summary>A header drags its tile or group (dnd.ts). WPF cannot draw over the terminals' native windows, so while
    /// a drag is on they are hidden (the tiles show their background) and the drop preview is drawn where they were.</summary>
    void HeaderDragSource(FrameworkElement head, string key, bool group)
    {
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (!Writable || e.ClickCount > 1) return;
            headerDrag = new HeaderDrag(key, group, e.GetPosition(surface), head);
            head.CaptureMouse();
        };
        head.MouseMove += (_, e) =>
        {
            if (headerDrag is not { } drag || drag.Source != head) return;
            var p = e.GetPosition(surface);
            if (!drag.Moving)
            {
                if (Math.Abs(p.X - drag.Start.X) < 4 && Math.Abs(p.Y - drag.Start.Y) < 4) return;
                drag.Moving = true;
                SetTerminalsShown(false);
                Dragged(drag)?.SetValue(OpacityProperty, 0.5);
            }
            drag.Target = LayoutGeometry.TargetAt(places, drag.Key, drag.Group, p.X, p.Y, out var preview);
            if (drag.Target is null) dropPreview.Visibility = Visibility.Collapsed;
            else
            {
                Put(dropPreview, preview);
                dropPreview.Visibility = Visibility.Visible;
            }
        };
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (headerDrag is not { } drag || drag.Source != head) return;
            // Its size as the new window's (tearOut), taken before the drag ends.
            var size = Dragged(drag) is FrameworkElement dragged ? new Size(dragged.ActualWidth, dragged.ActualHeight) : (Size?)null;
            head.ReleaseMouseCapture();
            if (!drag.Moving) return;
            if (drag.Target is { } target) Act(new LayoutAction.NodeMove(drag.Key, target));
            // Let go outside the window: to another window of the shell's, or into a window of its own (dnd.ts).
            else if (!IsInside(e.GetPosition(this))) shell.DropOutside(WindowId, drag.Key, ScreenPoint(e.GetPosition(this)), size);
        };
        head.LostMouseCapture += (_, _) => EndHeaderDrag(head);
    }

    UIElement? Dragged(HeaderDrag drag) =>
        drag.Group ? frames.GetValueOrDefault(drag.Key) : tiles.GetValueOrDefault(drag.Key["tile:".Length..])?.Ring;

    void EndHeaderDrag(FrameworkElement head)
    {
        if (headerDrag is not { } drag || drag.Source != head) return;
        headerDrag = null;
        dropPreview.Visibility = Visibility.Collapsed;
        if (!drag.Moving) return;
        foreach (var view in tiles.Values) view.Ring.Opacity = 1;
        foreach (var frame in frames.Values) frame.Opacity = 1;
        SetTerminalsShown(true);
    }

    void SetTerminalsShown(bool shown)
    {
        foreach (var view in tiles.Values)
            if (view.Control is { } control) control.Visibility = shown ? Visibility.Visible : Visibility.Hidden;
    }

    // ---------- keys ----------

    /// <summary>The layout's shortcuts (shortcuts.ts): Ctrl+Shift+G view mode, Z zoom, M collapse the focused tile (or
    /// bring the one collapsed last back), F find, the text size; the others are in MainWindow.Windows.cs.</summary>
    void LayoutKeys(KeyEventArgs e)
    {
        var target = focused ?? selected;
        switch (Shortcuts.For(TerminalBehaviour.PressOf(e)))
        {
            case Shortcut.Named { Kind: "toggleViewMode" }:
                Act(new LayoutAction.WindowViewMode(LayoutActions.ViewModeOf(Win) == "single" ? "tiled" : "single"));
                break;
            case Shortcut.Named { Kind: "zoom" } when target is not null:
                ToggleZoom(target);
                break;
            case Shortcut.Named { Kind: "toggleCollapse" } when target is not null && Writable:
                Act(new LayoutAction.TileCollapse(target));
                break;
            case Shortcut.Named { Kind: "toggleCollapse" } when Writable && Win.Dock.LastOrDefault() is { } last:
                if (last is DockTile tile)
                {
                    Act(new LayoutAction.TileExpand(tile.SessionId));
                    FocusTile(tile.SessionId);
                }
                else if (last is DockGroup group) Act(new LayoutAction.GroupExpand(group.Node.Id));
                break;
            case Shortcut.Named { Kind: "find" } when target is not null && tiles.GetValueOrDefault(target)?.Behaviour is { } behaviour:
                behaviour.OpenFind();
                break;
            case Shortcut.TextSize size:
                ChangeTextSize(size.Change);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------- terminal text size ----------

    /// <summary>The terminals' font size (CSS px): changed at once in every tile, saved as preferences.fontSize as
    /// reframe saves it (shortcuts.ts changeFontSize/echoFontSize).</summary>
    FontSizeState fontSize = new(StateValues.FontSizeNormal, null);

    void ChangeTextSize(TextSizeChange change)
    {
        if (Shortcuts.ChangeFontSize(fontSize, change) is not { } next) return;
        fontSize = next;
        ApplyFontSize();
        if (!Writable) return;
        state.Preferences.FontSize = next.Size;
        Saved();
    }

    /// <summary>The saved size, as loaded (or written back by another window): taken unless a change is on its way.</summary>
    void FontSizeLoaded()
    {
        fontSize = Shortcuts.EchoFontSize(fontSize, state.Preferences.FontSize);
        ApplyFontSize();
    }

    void ApplyFontSize()
    {
        foreach (var tile in tiles.Values) tile.Behaviour?.SetFontSize(fontSize.Size);
    }

    /// <summary>A session in WSL (its folder is in a distribution) gets Linux paths; drives under /mnt/ (wsl.conf's
    /// automount root is not read here).</summary>
    PasteText.Wsl? WslOf(string sessionId) =>
        state.Sessions.TryGetValue(sessionId, out var r) && WslPaths.Parse(r.Cwd) is { } wsl ? new PasteText.Wsl(wsl.Distro, WslPaths.DefaultMountRoot) : null;
}
