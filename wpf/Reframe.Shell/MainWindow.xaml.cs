using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>An open session: its tab (a dock item while another is shown) and its tile.</summary>
sealed class OpenSession
{
    public required string SessionId { get; init; }
    public required TabItem Tab { get; init; }
    public required StatusDot TileDot { get; init; }
    public required StatusDot DockDot { get; init; }
    public required HostTerminalConnection Terminal { get; init; }
    public required FrameworkElement Tile { get; init; }
}

/// <summary>
/// The main window as App.tsx lays it out: the toolbar (also the title bar), the session sidebar, the dock strip and
/// the layout area with the shown session's tile, or the empty state. Read-only for now: reframe's state is read
/// (never written), sessions are opened as views of the host's sessions, and New session, New window, the view mode,
/// settings and about are not wired yet.
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hwnd);

    /// <summary>reframe's terminal look (src/renderer/terminals.ts): its background, foreground and selection, and
    /// xterm.js's default 16 colours, which reframe keeps. Win32 COLORREF is 0x00BBGGRR.</summary>
    static readonly TerminalTheme Theme = new()
    {
        DefaultBackground = Rgb(0x17181c),
        DefaultForeground = Rgb(0xd7dae0),
        DefaultSelectionBackground = Rgb(0x3a3f4b),
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable =
        [
            Rgb(0x000000), Rgb(0xcd3131), Rgb(0x0dbc79), Rgb(0xe5e510), Rgb(0x2472c8), Rgb(0xbc3fbc), Rgb(0x11a8cd), Rgb(0xe5e5e5),
            Rgb(0x666666), Rgb(0xf14c4c), Rgb(0x23d18b), Rgb(0xf5f543), Rgb(0x3b8eea), Rgb(0xd670d6), Rgb(0x29b8db), Rgb(0xe5e5e5),
        ],
    };
    const string FontFamilyName = "Cascadia Mono";
    /// <summary>reframe's normal font size, 13 CSS px (FONT_SIZE.normal in src/shared/state.ts), in the points the
    /// terminal control takes (13 px = 9.75 pt).</summary>
    const short TerminalFontSize = 10;

    readonly Grid root = new();
    readonly SessionSidebar sidebar = new();
    readonly TabControl tabs = new();
    readonly FrameworkElement empty;
    readonly Button sessionsButton, singleButton, tiledButton, infoButton;
    readonly StackPanel captionButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    readonly Dictionary<string, OpenSession> open = [];

    string status = "";
    HostConnection? connection;
    HostServices? services;
    IDisposable? shellLock;
    AppState state = AppState.CreateDefault();
    readonly Dictionary<string, HostSession> hostSessions = [];
    readonly Dictionary<string, SessionStatus> hostStatus = [];
    VisualFixture? fixture;
    readonly string home = ClaudeRegistry.Home();
    string query = "";
    HashSet<string>? expanded;
    SessionList? list;
    bool refreshing, refreshAgain;

    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition());
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);

        // .toolbar: 40px, surface, border-bottom; buttons 8px apart, 8px in from the left; the window buttons' room at the right.
        var toolbar = new Grid { Background = B("surfaceBrush") };
        toolbar.Children.Add(new Border { BorderBrush = B("borderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(ToolbarButton("plus", "New session", "New session (Ctrl+Shift+T)"));
        sessionsButton = ToolbarButton("list", "Sessions", "Sessions (Ctrl+Shift+L)");
        sessionsButton.Click += (_, _) => ShowSidebar(sidebar.Visibility != Visibility.Visible);
        left.Children.Add(sessionsButton);
        left.Children.Add(ToolbarButton("window", "New window", "New window (Ctrl+Shift+N)"));
        var segmented = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(segmented, "View (Ctrl+Shift+G)");
        singleButton = ToolbarButton("window", "Single window", "Single window (Ctrl+Shift+G)");
        singleButton.Tag = new CornerRadius(6, 0, 0, 6);
        tiledButton = ToolbarButton("grid", "Tiled view", "Tiled view (Ctrl+Shift+G)");
        tiledButton.Tag = new CornerRadius(0, 6, 6, 0);
        tiledButton.Margin = new Thickness(-1, 0, 0, 0);
        segmented.Children.Add(singleButton);
        segmented.Children.Add(tiledButton);
        left.Children.Add(segmented);
        toolbar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8 + 138, 1) };
        right.Children.Add(IconButton("gear", "Settings", "Settings (Ctrl+,)"));
        infoButton = IconButton("info", "About Reframe", "About Reframe");
        infoButton.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(infoButton);
        toolbar.Children.Add(right);
        captionButtons.Children.Add(Caption("", "Minimize", "CaptionButton", () => WindowState = System.Windows.WindowState.Minimized));
        captionButtons.Children.Add(Caption("", "Maximize", "CaptionButton",
            () => WindowState = WindowState == System.Windows.WindowState.Maximized ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized));
        captionButtons.Children.Add(Caption("", "Close", "CaptionCloseButton", Close));
        toolbar.Children.Add(captionButtons);
        root.Children.Add(toolbar);

        // .workspace: the sidebar (with its resizer), then the dock strip and the layout (the tab control), or the empty state.
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(workspace, 1);
        workspace.Children.Add(sidebar);
        var resizer = new Thumb { Width = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -3, 0), Cursor = Cursors.SizeWE };
        resizer.Template = ResizerTemplate();
        resizer.DragDelta += (_, e) => sidebar.Width = Math.Clamp(sidebar.Width + e.HorizontalChange, 180, 600);
        Panel.SetZIndex(resizer, 2);
        AutomationProperties.SetName(resizer, "Resize the session list");
        workspace.Children.Add(resizer);
        tabs.Style = S("DockTabs");
        AutomationProperties.SetName(tabs, "Open sessions");
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs) return;
            UpdateDock();
            ShowSelectedTile();
            if (tabs.SelectedItem is TabItem { Tag: OpenSession shown }) sidebar.Select(ChatKeyOf(shown.SessionId));
        };
        Grid.SetColumn(tabs, 1);
        workspace.Children.Add(tabs);
        empty = EmptyState();
        Grid.SetColumn(empty, 1);
        workspace.Children.Add(empty);
        root.Children.Add(workspace);
        UpdateDock();
        ApplyViewMode("tiled");
        sessionsButton.BorderBrush = B("accentBrush");

        sidebar.Open += OpenChat;
        sidebar.ProjectToggled += id =>
        {
            if (expanded is null) return;
            if (!expanded.Remove(id)) expanded.Add(id);
            Render();
        };
        sidebar.QueryChanged += text =>
        {
            query = text;
            _ = RefreshAsync();
        };

        if (App.CaptureDir is null)
        {
            Content = root;
            Loaded += async (_, _) => await StartAsync();
            StateChanged += (_, _) => root.Margin = WindowState == System.Windows.WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        }
        Closed += async (_, _) =>
        {
            // Leaves the host with every session running (never "shutdown").
            if (connection is not null) await connection.DisposeAsync();
            shellLock?.Dispose();
        };
    }

    static uint Rgb(uint rgb) => ((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff);

    static StackPanel IconAndText(string icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new VectorIcon { Kind = icon, Size = 14, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, Style = S("Text13"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    static Button ToolbarButton(string icon, string text, string tooltip)
    {
        var button = new Button { Style = S("ToolbarButton"), Content = IconAndText(icon, text), ToolTip = tooltip, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(button, text);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    static Button IconButton(string icon, string name, string tooltip)
    {
        var button = new Button { Style = S("ToolbarIcon"), Content = new VectorIcon { Kind = icon, Size = 14 }, ToolTip = tooltip };
        AutomationProperties.SetName(button, name);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    static Button Caption(string glyph, string name, string style, Action click)
    {
        var button = new Button { Style = S(style), Content = glyph, ToolTip = name };
        AutomationProperties.SetName(button, name);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => click();
        return button;
    }

    static ControlTemplate ResizerTemplate()
    {
        // SidebarResizer: invisible, the accent while the mouse is on it.
        var template = new ControlTemplate(typeof(Thumb));
        var border = new FrameworkElementFactory(typeof(Border), "Bar");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, B("accentBrush"), "Bar"));
        template.Triggers.Add(hover);
        return template;
    }

    /// <summary>.empty: "No sessions yet", the hint and the accent New session button, centred in the layout.</summary>
    static FrameworkElement EmptyState()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "No sessions yet", FontSize = 18, FontWeight = FontWeights.Bold, LineHeight = 25.2, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Start a Claude Code session to get going.", Style = S("Text13"), Foreground = B("mutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
        });
        var button = new Button { Style = S("PrimaryButton"), Content = IconAndText("plus", "New session"), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(button, "New session");
        panel.Children.Add(button);
        return new Border { Padding = new Thickness(6), Child = panel };
    }

    void ShowSidebar(bool show)
    {
        sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        sessionsButton.BorderBrush = show ? B("accentBrush") : B("border-strongBrush");
    }

    /// <summary>The view mode's buttons (.is-active: the accent border, drawn over its neighbour's).</summary>
    void ApplyViewMode(string mode)
    {
        singleButton.BorderBrush = mode == "single" ? B("accentBrush") : B("border-strongBrush");
        tiledButton.BorderBrush = mode == "tiled" ? B("accentBrush") : B("border-strongBrush");
        Panel.SetZIndex(singleButton, mode == "single" ? 1 : 0);
        Panel.SetZIndex(tiledButton, mode == "tiled" ? 1 : 0);
    }

    Grid? TileHost() => tabs.Template.FindName("Tiles", tabs) as Grid;

    void ShowSelectedTile()
    {
        foreach (var session in open.Values)
            session.Tile.Visibility = tabs.SelectedItem == session.Tab ? Visibility.Visible : Visibility.Hidden;
    }

    void UpdateDock()
    {
        tabs.Tag = tabs.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var any = tabs.Items.Count > 0;
        tabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- data ----------

    async Task StartAsync()
    {
        await AttachAsync();
        await RefreshAsync();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
    }

    void SetStatus(string text)
    {
        status = text;
        infoButton.ToolTip = $"About Reframe\n{text}";
    }

    async Task AttachAsync()
    {
        SetStatus($"Profile {App.ProfileDir}: attaching...");
        var result = await ShellAttach.AttachAsync(App.ProfileDir, App.TakeOver);
        SetStatus($"Profile {App.ProfileDir}: {result.Message}");
        if (result.Connection is null) return;
        connection = result.Connection;
        shellLock = result.Lock;
        connection.Closed += replaced => Dispatcher.InvokeAsync(() =>
            SetStatus(replaced ? "Another client took the profile over: detached." : "The pty host connection closed."));
        if (connection.IsObserver) SetStatus(status + " Terminals are read-only (start with --take-over to type in them).");
        services = new HostServices(connection);
        services.StatusChanged += changed => Dispatcher.InvokeAsync(() =>
        {
            hostStatus[changed.SessionId] = changed;
            if (open.TryGetValue(changed.SessionId, out var shown)) shown.TileDot.Status = shown.DockDot.Status = StatusOf(changed.SessionId);
            _ = RefreshAsync();
        });
        foreach (var session in connection.Welcome.Sessions) hostSessions[session.SessionId] = session;
        try
        {
            foreach (var listed in await services.ListSessionsAsync()) hostStatus[listed.SessionId] = listed;
        }
        catch (HostServiceException e)
        {
            SetStatus(status + $" (sessions: {e.Message})");
        }
        if (fixture is not null) return;
        try
        {
            state = (await StateLoader.LoadAsync(services, Path.Combine(App.ProfileDir, "state.json"))).State;
        }
        catch (Exception e) when (e is HostServiceException or IOException)
        {
            SetStatus(status + $" (state: {e.Message})");
        }
        // The host's sessions that reframe's state does not know (started by a test or another client) are listed too.
        foreach (var session in hostSessions.Values.Where(s => !state.Sessions.ContainsKey(s.SessionId)))
        {
            hostStatus.TryGetValue(session.SessionId, out var known);
            state.Sessions[session.SessionId] = new SessionRecord
            {
                Id = session.SessionId,
                ClaudeSessionId = session.ClaudeSessionId ?? known?.ClaudeSessionId ?? session.SessionId,
                Cwd = known?.Cwd ?? "",
                Name = known?.Name ?? session.SessionId,
                LastActiveAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            };
        }
    }

    /// <summary>sessionView's status: the host's (background: idle with background work), else ended.</summary>
    string StatusOf(string sessionId) =>
        fixture?.Statuses.GetValueOrDefault(sessionId)
        ?? (hostStatus.TryGetValue(sessionId, out var known) ? known.Status : hostSessions.ContainsKey(sessionId) ? "starting" : "ended");

    ListSources Sources()
    {
        if (fixture is not null) return fixture.Sources();
        ITranscriptSource transcripts = connection is not null && services?.Offers("transcripts") == true
            ? new HostTranscriptSource(connection, home)
            : new MemoryTranscriptSource([]);
        return new ListSources
        {
            State = state,
            StatusOf = r => StatusOf(r.Id),
            RunsInReframe = hostSessions.ContainsKey,
            Running = ClaudeRegistry.Running(home),
            Transcripts = transcripts,
        };
    }

    double Now => fixture?.Now ?? DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>Lists again (sessionList), one request at a time.</summary>
    async Task RefreshAsync()
    {
        if (refreshing)
        {
            refreshAgain = true;
            return;
        }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                try
                {
                    list = await SessionListBuilder.BuildAsync(Sources(), query, SessionListBuilder.Page);
                }
                catch (HostServiceException e)
                {
                    SetStatus(status + $" (list: {e.Message})");
                }
                Render();
            } while (refreshAgain);
        }
        finally
        {
            refreshing = false;
        }
    }

    void Render()
    {
        if (list is null) return;
        expanded ??= [.. list.ExpandedProjects];
        sidebar.Show(SidebarView.Build(list with { ExpandedProjects = [.. expanded] }, Now));
    }

    string ChatKeyOf(string sessionId) =>
        state.Sessions.TryGetValue(sessionId, out var record) ? SidebarChats.ChatKey(record.ClaudeSessionId) : "";

    // ---------- sessions ----------

    void OpenChat(ChatRow chat)
    {
        if (chat.Ref.Kind != ListItemKind.App || chat.Ref.SessionId is not { } id) return;
        OpenTab(id, chat.Label, chat.Chat.Cwd, chat.Provider);
    }

    /// <summary>Shows a host session in a tab (its tile), creating it the first time. Nothing without a host session.</summary>
    void OpenTab(string sessionId, string label, string cwd, string? provider)
    {
        if (connection is not { IsOpen: true } || !hostSessions.ContainsKey(sessionId)) return;
        if (open.TryGetValue(sessionId, out var existing))
        {
            tabs.SelectedItem = existing.Tab;
            if (FindHost(existing.Tile) is { } shownHost) FocusTerminal(shownHost);
            return;
        }
        var control = new TerminalControl { AutoResize = true };
        var terminal = new HostTerminalConnection(connection.CreateView(sessionId), control);
        var connected = false;
        control.Loaded += (_, _) =>
        {
            control.SetTheme(Theme, FontFamilyName, TerminalFontSize, Color.FromRgb(0x17, 0x18, 0x1c));
            if (FindHost(control) is { } host) FocusTerminal(host);
            if (connected) return; // a tab switch unloads and loads the control again
            connected = true;
            control.Connection = terminal;
        };
        var paste = new MenuItem { Header = "Paste" };
        paste.Click += (_, _) => terminal.Paste();
        control.ContextMenu = new ContextMenu { Items = { paste } };
        control.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                terminal.Paste();
                e.Handled = true;
            }
        };

        var initial = StatusOf(sessionId);
        var tileDot = new StatusDot { Status = initial, VerticalAlignment = VerticalAlignment.Center };
        var dockDot = new StatusDot { Status = initial, HorizontalAlignment = HorizontalAlignment.Center };
        var tab = new TabItem { Header = DockHeader(label, dockDot), ToolTip = label };
        AutomationProperties.SetName(tab, label);
        var close = TileButton("close", $"Close {label}", $"Close {label}");
        close.Click += (_, _) =>
        {
            terminal.Close(); // detaches the view only; the session goes on
            if (open.Remove(sessionId, out var closed)) TileHost()?.Children.Remove(closed.Tile);
            tabs.Items.Remove(tab);
            UpdateDock();
        };
        // The tiles stay in one panel and are only hidden: a tab's content would be unloaded on every switch, and
        // the terminal's HwndHost does not survive being destroyed and rebuilt (a native crash).
        var tile = Tile(label, cwd, provider, tileDot, close, control);
        tile.Visibility = Visibility.Hidden;
        tabs.ApplyTemplate();
        TileHost()?.Children.Add(tile);
        var session = new OpenSession { SessionId = sessionId, Tab = tab, TileDot = tileDot, DockDot = dockDot, Terminal = terminal, Tile = tile };
        tab.Tag = session;
        open[sessionId] = session;
        tabs.Items.Add(tab);
        tabs.SelectedItem = tab;
        ShowSelectedTile();
        UpdateDock();
    }

    /// <summary>.dock-item: the status dot and the label written bottom to top.</summary>
    static FrameworkElement DockHeader(string label, StatusDot dot)
    {
        var panel = new StackPanel();
        panel.Children.Add(dot);
        panel.Children.Add(new TextBlock
        {
            Text = label, Style = S("Text12"), MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, LayoutTransform = new RotateTransform(-90),
        });
        return panel;
    }

    static Button TileButton(string icon, string name, string tooltip)
    {
        var button = new Button { Style = S("IconButton"), Content = new VectorIcon { Kind = icon, Size = 14 }, ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>Tile.tsx: the header (collapse, grip, status, provider, label, folder, zoom, close) over the terminal.</summary>
    FrameworkElement Tile(string label, string cwd, string? provider, StatusDot dot, Button close, TerminalControl terminal)
    {
        var header = new DockPanel { LastChildFill = true };
        void Left(UIElement element)
        {
            DockPanel.SetDock(element, Dock.Left);
            header.Children.Add(element);
        }
        void Right(UIElement element)
        {
            DockPanel.SetDock(element, Dock.Right);
            header.Children.Add(element);
        }
        Left(TileButton("collapse", $"Collapse {label}", "Collapse to the side"));
        Left(new VectorIcon { Kind = "grip", Size = 14, Foreground = B("mutedBrush"), Opacity = 0.6, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        dot.Margin = new Thickness(6, 0, 0, 0);
        Left(dot);
        var icon = new VectorIcon
        {
            Kind = provider == "anthropic" ? "anthropic" : "chatgpt", Size = 16, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = B(provider == "anthropic" ? "providerAnthropicBrush" : "providerChatgptBrush"),
        };
        Left(icon);
        Left(new TextBlock
        {
            Text = label, Style = S("Text13"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 400, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        close.Margin = new Thickness(6, 0, 0, 0);
        Right(close);
        var zoom = TileButton("zoom", $"Zoom {label}", "Zoom");
        zoom.Margin = new Thickness(6, 0, 0, 0);
        Right(zoom);
        header.Children.Add(new TextBlock
        {
            Text = RelativeTime.HomeRelative(cwd, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), Style = S("Text12"),
            Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });

        var head = new Border
        {
            Height = 30, Padding = new Thickness(4, 0, 4, 0), Background = B("surfaceBrush"), BorderBrush = B("borderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(5, 5, 0, 0), Child = header,
        };
        DockPanel.SetDock(head, Dock.Top);
        var body = new DockPanel();
        body.Children.Add(head);
        // .tile-terminal: inset 4px 2px 2px 6px.
        body.Children.Add(new Border { Margin = new Thickness(6, 4, 2, 2), Child = terminal });
        var tile = new Border
        {
            BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = B("terminalBrush"), Child = body, SnapsToDevicePixels = true,
        };
        // .tile:focus-within: the accent border and a lighter header.
        tile.IsKeyboardFocusWithinChanged += (_, _) =>
        {
            tile.BorderBrush = tile.IsKeyboardFocusWithin ? B("accentBrush") : B("borderBrush");
            head.Background = tile.IsKeyboardFocusWithin ? B("surface-2Brush") : B("surfaceBrush");
        };
        return tile;
    }

    /// <summary>Moves keyboard focus into the terminal once its native window exists, so typing goes to the session
    /// (not to the list item that opened it). TerminalControl hosts the terminal in an HwndHost, which WPF focus alone
    /// does not reach, so the native window gets Win32 focus as well.</summary>
    void FocusTerminal(HwndHost host) => _ = Dispatcher.InvokeAsync(() =>
    {
        Keyboard.Focus(host);
        if (host.Handle != IntPtr.Zero) SetFocus(host.Handle);
    }, DispatcherPriority.Input);

    static HwndHost? FindHost(DependencyObject? parent)
    {
        if (parent is null) return null;
        if (parent is HwndHost self) return self;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindHost(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    // ---------- diagnostics ----------

    /// <summary>--diag-capture: renders the fixture's states under the state ids of e2e/visual-reference.spec.ts (the
    /// root in a 1280x800 popup, whatever the screen's size), saves them and returns. Without a fixture: the live
    /// profile's main window as main-empty or main-sessions.</summary>
    public async Task CaptureAsync(string dir, string? fixtureFile)
    {
        StatusDot.Still = true;
        using var popup = DiagCapture.Host(root);
        try
        {
            var name = fixtureFile is null ? null : Path.GetFileNameWithoutExtension(fixtureFile);
            if (fixtureFile is not null)
            {
                fixture = VisualFixture.Load(fixtureFile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DateTimeOffset.Now.ToUnixTimeMilliseconds());
                state = fixture.State;
                ApplyViewMode(fixture.State.Windows.FirstOrDefault()?.ViewMode ?? "tiled");
            }
            if (name != "empty") await AttachAsync();
            await RefreshAsync();
            await DiagCapture.SettleAsync(Dispatcher, 800);
            var tabIds = fixture?.Tabs ?? [];
            foreach (var id in tabIds)
                if (state.Sessions.TryGetValue(id, out var record))
                    OpenTab(id, StateValues.SessionLabel(record.Name, record.ClaudeName, record.Cwd), record.Cwd, record.Launch.Provider ?? "chatgpt");
            if (fixture?.Selected is { } selected && open.TryGetValue(selected, out var shown)) tabs.SelectedItem = shown.Tab;
            sidebar.Select(null);
            await DiagCapture.SettleAsync(Dispatcher, tabIds.Count > 0 ? 3500 : 500);

            switch (name)
            {
                case "tabs":
                    DiagCapture.Save(popup, root, dir, "tabs");
                    if (tabs.Template.FindName("Dock", tabs) is FrameworkElement dock && dock.IsVisible) DiagCapture.Save(popup, dock, dir, "dock-strip");
                    break;
                case "sessions":
                    DiagCapture.Save(popup, root, dir, "main-sessions");
                    DiagCapture.Save(popup, sidebar, dir, "sidebar");
                    foreach (var (stateId, label) in new[]
                    {
                        ("sidebar-row-idle", "checkout-fix"), ("sidebar-row-busy", "rate-limiter"), ("sidebar-row-waiting", "db-migration"),
                        ("sidebar-row-ended", "release-notes"), ("sidebar-row-found", "ci-pipeline"), ("sidebar-row-pinned", "Pinned: plan the Q3 roadmap"),
                    })
                        await SaveRowAsync(popup, dir, stateId, label);
                    if (fixture is not null && fixture.Statuses.ContainsKey("vs-checkout-fix"))
                    {
                        // WPF only: the statuses the fixture has no session in (Electron's are taken from other runs).
                        foreach (var (stateId, rowStatus) in new[] { ("sidebar-row-starting", "starting"), ("sidebar-row-background", "background") })
                        {
                            fixture.Statuses["vs-checkout-fix"] = rowStatus;
                            await RefreshAsync();
                            await SaveRowAsync(popup, dir, stateId, "checkout-fix");
                        }
                        fixture.Statuses["vs-checkout-fix"] = "idle";
                    }
                    sidebar.Scroll.ScrollToTop();
                    Diag.SetFocused(sidebar.Search, true);
                    sidebar.Search.Text = "check";
                    await RefreshAsync();
                    await DiagCapture.SettleAsync(Dispatcher, 500);
                    DiagCapture.Save(popup, root, dir, "sidebar-search");
                    break;
                default:
                    DiagCapture.Save(popup, root, dir, open.Count > 0 ? "main-sessions" : "main-empty");
                    break;
            }
        }
        finally
        {
            DiagCapture.WriteManifest(dir, status);
            if (connection is not null) await connection.DisposeAsync();
            connection = null;
            shellLock?.Dispose();
            shellLock = null;
        }
    }

    async Task SaveRowAsync(HwndSource popup, string dir, string stateId, string label)
    {
        if (sidebar.RowOf(label) is not { } row) return;
        row.BringIntoView();
        await DiagCapture.SettleAsync(Dispatcher, 200);
        DiagCapture.Save(popup, row, dir, stateId);
    }
}
