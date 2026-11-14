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

/// <summary>
/// The main window as App.tsx lays it out: the toolbar (also the title bar), the session sidebar, the dock strip and
/// the layout area with the window's tiles (MainWindow.Layout.cs), or the empty state. reframe's state is read and, as the primary
/// client, written through the host's <c>state</c> service (StateStore; never as an observer, while reframe owns it);
/// the context menus of rows and tiles (src/main/menus.ts) rename, pin and hide. Sessions are opened as views of the
/// host's sessions; Settings, About and the quota are in MainWindow.Settings.cs. The connection, the state and the other
/// windows are the ShellController's (one per profile); this window is the state's window <see cref="WindowId"/>, its
/// bounds, full screen and visibility in MainWindow.Windows.cs; New session and session control in MainWindow.Sessions.cs.
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    /// <summary>
    /// The system frame, as Electron's frameless window with a title bar overlay keeps it: shadow, 1px border and, on
    /// Windows 11, rounded corners (WindowChrome's 1px glass frame keeps DWM's non-client rendering; Windows 10 ignores
    /// the corner preference).
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    readonly Grid root = new();
    readonly SessionSidebar sidebar = new();
    readonly FrameworkElement empty;
    readonly Button sessionsButton, singleButton, tiledButton, infoButton;
    readonly StackPanel captionButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };

    readonly ShellController shell;
    string status => shell.Status;
    HostConnection? connection => shell.Connection;
    HostServices? services => shell.Services;
    AppState state { get => shell.State; set => shell.State = value; }
    /// <summary>Menus as built for the capture (menus.json), by state id.</summary>
    readonly System.Text.Json.Nodes.JsonObject capturedMenus = [];
    Dictionary<string, HostSession> hostSessions => shell.HostSessions;
    VisualFixture? fixture { get => shell.Fixture; set => shell.Fixture = value; }
    readonly string home = ClaudeRegistry.Home();
    string query = "";
    HashSet<string>? expanded;
    SessionList? list;
    bool refreshing, refreshAgain;

    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    internal MainWindow(ShellController shell, string? windowId)
    {
        this.shell = shell;
        this.windowId = windowId;
        InitializeComponent();
        if (windowId is not null) WatchForeignDrags();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);

        // .toolbar: 40px, surface, border-bottom; buttons 8px apart, 8px in from the left; the window buttons' room at the right.
        var toolbar = new Grid { Background = B("surfaceBrush") };
        toolbar.Children.Add(new Border { BorderBrush = B("borderBrush"), BorderThickness = new Thickness(0, 0, 0, 1) });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        var newSessionButton = ToolbarButton("plus", "New session", "New session (Ctrl+Shift+T)");
        newSessionButton.Click += (_, _) => OpenNewSession();
        left.Children.Add(newSessionButton);
        sessionsButton = ToolbarButton("list", "Sessions", "Sessions (Ctrl+Shift+L)");
        sessionsButton.Click += (_, _) => ToggleSidebar();
        left.Children.Add(sessionsButton);
        left.Children.Add(OverviewButton());
        var newWindowButton = ToolbarButton("window", "New window", "New window (Ctrl+Shift+N)");
        newWindowButton.Click += (_, _) => NewWindow();
        left.Children.Add(newWindowButton);
        var segmented = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(segmented, "View (Ctrl+Shift+G)");
        singleButton = ToolbarButton("window", "Single window", "Single window (Ctrl+Shift+G)");
        singleButton.Tag = new CornerRadius(6, 0, 0, 6);
        tiledButton = ToolbarButton("grid", "Tiled view", "Tiled view (Ctrl+Shift+G)");
        tiledButton.Tag = new CornerRadius(0, 6, 6, 0);
        tiledButton.Margin = new Thickness(-1, 0, 0, 0);
        singleButton.Click += (_, _) => Act(new LayoutAction.WindowViewMode("single"));
        tiledButton.Click += (_, _) => Act(new LayoutAction.WindowViewMode("tiled"));
        segmented.Children.Add(singleButton);
        segmented.Children.Add(tiledButton);
        left.Children.Add(segmented);
        toolbar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8 + 138, 1) };
        // Shown in full screen only (App.tsx: ExitFullscreenIcon, before the gear).
        exitFullscreenButton = IconButton("exit-fullscreen", "Exit full screen", "Exit full screen (F11)");
        exitFullscreenButton.Click += (_, _) => ToggleFullscreen();
        exitFullscreenButton.Margin = new Thickness(0, 0, 8, 0);
        exitFullscreenButton.Visibility = Visibility.Collapsed;
        right.Children.Add(exitFullscreenButton);
        var gearButton = IconButton("gear", "Settings", "Settings (Ctrl+,)");
        gearButton.Click += (_, _) => OpenSettings();
        right.Children.Add(gearButton);
        infoButton = IconButton("info", "About Reframe", "About Reframe");
        infoButton.Click += (_, _) => OpenAbout();
        infoButton.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(infoButton);
        toolbar.Children.Add(right);
        captionButtons.Children.Add(Caption("", "Minimize", "CaptionButton", () => WindowState = System.Windows.WindowState.Minimized));
        captionButtons.Children.Add(Caption("", "Maximize", "CaptionButton",
            () => WindowState = WindowState == System.Windows.WindowState.Maximized ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized));
        captionButtons.Children.Add(Caption("", "Close", "CaptionCloseButton", Close));
        toolbar.Children.Add(captionButtons);
        root.Children.Add(toolbar);
        var report = BatchReport();
        Grid.SetRow(report, 1);
        root.Children.Add(report);

        // .workspace: the sidebar (with its resizer), then the dock strip and the layout (the tab control), or the empty state.
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(workspace, 2);
        workspace.Children.Add(sidebar);
        var resizer = new Thumb { Width = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -3, 0), Cursor = Cursors.SizeWE };
        resizer.Template = ResizerTemplate();
        resizer.DragDelta += (_, e) => sidebar.Width = Math.Clamp(sidebar.Width + e.HorizontalChange, 180, 600);
        Panel.SetZIndex(resizer, 2);
        AutomationProperties.SetName(resizer, "Resize the session list");
        workspace.Children.Add(resizer);
        empty = EmptyState();
        var layoutColumn = LayoutColumn();
        Grid.SetColumn(layoutColumn, 1);
        workspace.Children.Add(layoutColumn);
        AddOverview(workspace);
        root.Children.Add(workspace);
        PreviewKeyDown += (_, e) => LayoutKeys(e);
        PreviewKeyDown += (_, e) => SettingsKeys(e);
        PreviewKeyDown += (_, e) => WindowKeys(e);
        ApplyViewMode("tiled");
        sessionsButton.BorderBrush = B("accentBrush");

        sidebar.Open += OpenChat;
        sidebar.NewSessionIn += project => OpenNewSession(new NewSessionPreset(project.Cwd, project.Locations?.FirstOrDefault()?.Provider ?? "chatgpt", project.Locations));
        sidebar.RowMenu += (chat, target) => ShowMenu(ListMenuOf(chat), target, chat);
        sidebar.SelectionMenu += ShowSelectionMenu;
        sidebar.RestoreArchive += RestoreArchiveAsync;
        sidebar.StartWsl += distro => _ = shell.StartWslAsync(distro);
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

        shell.StatusChanged += ShowStatus;
        if (App.CaptureDir is null)
        {
            Content = root;
            Loaded += async (_, _) => await StartAsync();
            WatchWindow();
            WatchAttention();
        }
    }


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
    FrameworkElement EmptyState()
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
        button.Click += (_, _) => OpenNewSession();
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

    // ---------- data ----------

    async Task StartAsync()
    {
        ShowStatus();
        ShowSidebar(Win.SidebarOpen == true);
        LayoutLoaded();
        await RefreshAsync();
        StartQuota();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
    }

    void SetStatus(string text) => shell.SetStatus(text);

    void ShowStatus() => infoButton.ToolTip = $"About Reframe\n{status}";

    /// <summary>The host's status of a session changed (sessions service).</summary>
    public void HostStatusChanged(string sessionId)
    {
        UpdateTileStatus(sessionId);
        _ = RefreshAsync();
    }

    /// <summary>What the sessions use now (<c>monitor.sample</c>): each tile's meters; a tile not in it shows none.</summary>
    public void ShowUsage(IReadOnlyDictionary<string, SessionUsage> usage)
    {
        lastUsage = usage;
        foreach (var (id, tile) in tiles) tile.Meters.Show(usage.GetValueOrDefault(id));
    }

    /// <summary>The state changed (this or another window, Settings): listed again.</summary>
    public void ListChanged() => _ = RefreshAsync();

    /// <summary>sessionView's status: the host's (background: idle with background work), else ended.</summary>
    string StatusOf(string sessionId) => shell.StatusOf(sessionId);

    ListSources Sources()
    {
        // A capture lists its fixture with the statuses the shell gives them (a failed start's "error" too).
        if (fixture is not null) return fixture.Sources() with { StatusOf = r => StatusOf(r.Id) };
        var (wsl, wslStopped) = shell.Wsl(ListChanged);
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
            Wsl = wsl,
            WslStopped = wslStopped,
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

    /// <summary>A list row's click (openSession): a tile of the window is selected, a collapsed one put back, any
    /// other session of reframe's added to the window (only one the host runs, or a capture's).</summary>
    void OpenChat(ChatRow chat)
    {
        if (chat.Ref.Kind == ListItemKind.App && chat.Ref.SessionId is { } id) ShowSession(id);
        else if (chat.Ref.Kind != ListItemKind.App && Writable) _ = OpenListItemAsync(chat.Ref);
    }

    /// <summary>openSession for a past chat or a claude running elsewhere: adopted (taken over after asking), then
    /// placed and resumed.</summary>
    async Task OpenListItemAsync(ListItemRef item)
    {
        if (await shell.RecordForAsync(WindowId, item) is { } id) ShowSession(id);
    }

    /// <summary>Brings a reframe session forward: its tile selected, expanded from the dock, or added to the layout.</summary>
    void ShowSession(string id)
    {
        var win = Win;
        if (Layout.Leaves(win.Layout).Contains(id))
        {
            // openSession: an ended one is resumed where it is.
            if (!Runs(id) && Writable) shell.Resume(WindowId, id);
            SelectTile(id);
        }
        else if (LayoutActions.IsCollapsed(win, id))
        {
            if (Writable) Act(new LayoutAction.TileExpand(id));
        }
        else if (CanShow(id) && Writable)
        {
            // placeAndResume: one that does not run is opened again and resumed.
            Dispatch(w => LayoutActions.AddTile(w, id));
            if (!Runs(id)) shell.Resume(WindowId, id);
            SelectTile(id);
        }
    }

    /// <summary>Any of reframe's sessions can be shown (one that does not run is resumed).</summary>
    bool CanShow(string sessionId) => state.Sessions.ContainsKey(sessionId);

    static Button TileButton(string icon, string name, string tooltip)
    {
        var button = new Button { Style = S("IconButton"), Content = new VectorIcon { Kind = icon, Size = 14 }, ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, name);
        return button;
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

    // ---------- context menus and state writes ----------

    /// <summary>Whether the shell may change reframe's state: as the primary client with a writable store (a capture
    /// changes only its fixture).</summary>
    bool Writable => shell.Writable;

    bool Runs(string sessionId) => shell.Runs(sessionId);

    static bool InWsl(string cwd) =>
        cwd.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase) || cwd.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase);

    /// <summary>chatActions' view of a list entry (or a tile's session).</summary>
    ChatMenuContext MenuContext(ListItemRef item, string key)
    {
        var record = item.Kind == ListItemKind.App && item.SessionId is { } id ? state.Sessions.GetValueOrDefault(id) : null;
        var ended = record is null || (!Runs(record.Id) && record.Lifecycle == "ended");
        var wsl = item.Kind == ListItemKind.Found && !string.IsNullOrEmpty(item.Distro) || record is not null && InWsl(record.Cwd);
        var inactive = ended && !wsl && item.Kind != ListItemKind.Running
            && !shell.HistoryBusy.Contains(record?.ClaudeSessionId ?? item.ClaudeSessionId ?? "");
        return new ChatMenuContext(item.Kind, record, key, ShellActions.IsPinned(state, key), ended, wsl, inactive, shell.ChatBatchRunning);
    }

    List<MenuSpec> ListMenuOf(ChatRow chat) =>
        ContextMenus.ListMenu(1, chat.Ref.Kind == ListItemKind.App, ContextMenus.ChatActions(MenuContext(chat.Ref, chat.Key)));

    List<MenuSpec> TileMenuOf(string sessionId)
    {
        var layout = Win.Layout;
        var own = Layout.GroupOf(layout, sessionId);
        return ContextMenus.TileMenu(ContextMenus.ChatActions(MenuContext(new ListItemRef(ListItemKind.App, sessionId), ChatKeyOf(sessionId))),
            own is not null, [.. Layout.Groups(layout).Where(g => g.Id != own?.Id)]);
    }

    /// <summary>The actions the shell does so far; the others (a new window's group: windows) show disabled.</summary>
    bool Supported(string action) =>
        action is "open" or "properties" || (Writable && (action is "takeOver" or "rename" or "pin" or "hide" or "newGroup" or "removeFromGroup" or "group"
            or "changeModel" or "archive" or "delete"
            || action.StartsWith("addToGroup:", StringComparison.Ordinal) || action.StartsWith("provider:", StringComparison.Ordinal)));

    ContextMenu BuildMenu(IEnumerable<MenuSpec> items, ChatRow? chat, string? sessionId) =>
        ShellMenus.Build(items, Supported, action => Run(action, chat, sessionId));

    void ShowMenu(List<MenuSpec> items, FrameworkElement target, ChatRow? chat, string? sessionId = null)
    {
        var menu = BuildMenu(items, chat, sessionId);
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    void Run(string action, ChatRow? chat, string? sessionId)
    {
        sessionId ??= chat?.Ref.Kind == ListItemKind.App ? chat.Ref.SessionId : null;
        var key = chat?.Key ?? (sessionId is null ? "" : ChatKeyOf(sessionId));
        switch (action)
        {
            case "open" or "takeOver" when chat is not null:
                OpenChat(chat);
                break;
            case "rename" when sessionId is not null && state.Sessions.TryGetValue(sessionId, out var record):
                var current = StateValues.SessionLabel(record.Name, record.ClaudeName, record.Cwd);
                if (RenameDialog.Ask(this, current) is { } name && ShellActions.Rename(state, sessionId, name))
                {
                    if (tiles.TryGetValue(sessionId, out var view)) view.Label.Text = LabelOf(sessionId);
                    Saved();
                }
                break;
            case "pin" when key != "":
                if (ShellActions.TogglePin(state, key)) Saved();
                break;
            case "hide":
                Hide(chat, sessionId, key);
                break;
            case "properties":
                ShowProperties(chat, sessionId);
                break;
            default:
                if (!RunHistoryAction(action, chat, sessionId)) RunGroupAction(action, sessionId);
                break;
        }
    }

    /// <summary>hideFromList: asks (Electron's native question box), then adds the thread to the hidden ones.</summary>
    void Hide(ChatRow? chat, string? sessionId, string key)
    {
        var record = sessionId is null ? null : state.Sessions.GetValueOrDefault(sessionId);
        bool Busy() => record is not null && (Runs(record.Id) || record.Lifecycle != "ended");
        if (Busy())
        {
            MessageBox.Show(this, "End this chat in Reframe before hiding it.", "Reframe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var claudeSessionId = record?.ClaudeSessionId ?? chat?.Ref.ClaudeSessionId ?? SidebarChats.ParseChatKey(key)?.Id;
        if (claudeSessionId is null) return;
        var label = chat?.Label ?? (record is null ? claudeSessionId : StateValues.SessionLabel(record.Name, record.ClaudeName, record.Cwd));
        var cwd = record?.Cwd ?? chat?.Chat.Cwd ?? "";
        var answer = MessageBox.Show(this,
            $"Hide “{label}” from Reframe?\n\nReframe will not list it, offer to take it over or resume it, or notify about it. The session itself is not changed. To show it again, use Settings › Hidden threads › Restore.",
            "Hide thread?", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (answer != MessageBoxResult.OK || Busy()) return;
        var thread = new HiddenThread
        {
            ClaudeSessionId = claudeSessionId, Label = label, Cwd = cwd,
            Distro = chat?.Ref.Distro is { Length: > 0 } distro ? distro : null,
            HiddenAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };
        if (ShellActions.Hide(state, thread)) Saved();
    }

    /// <summary>Writes the changed state (as reframe's save()) and lists again.</summary>
    void Saved() => shell.Saved();

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
                shell.CaptureWindow = this;
            }
            if (name is not ("empty" or "overview")) await shell.AttachAsync();
            await RefreshAsync();
            await DiagCapture.SettleAsync(Dispatcher, 800);
            selected = fixture?.Selected;
            LayoutLoaded();
            sidebar.Select(null);
            await DiagCapture.SettleAsync(Dispatcher, fixture?.Hosted.Count > 0 ? 3500 : 500);

            switch (name)
            {
                case "tabs":
                    DiagCapture.Save(popup, root, dir, "group");
                    if (frames.Values.FirstOrDefault() is { } frame) DiagCapture.Save(popup, frame, dir, "group-frame");
                    DiagCapture.Save(popup, root, dir, "collapsed");
                    if (dockStrip.IsVisible) DiagCapture.Save(popup, dockStrip, dir, "dock-strip");
                    // Ctrl+Shift+Z in docs-refresh, then Ctrl+Shift+G: a single window showing it.
                    focused = "vt-docs-refresh";
                    ToggleZoom("vt-docs-refresh");
                    await DiagCapture.SettleAsync(Dispatcher, 800);
                    DiagCapture.Save(popup, root, dir, "zoomed");
                    ToggleZoom("vt-docs-refresh");
                    focused = null;
                    selected = "vt-docs-refresh";
                    Act(new LayoutAction.WindowViewMode("single"));
                    await DiagCapture.SettleAsync(Dispatcher, 1200);
                    DiagCapture.Save(popup, root, dir, "tabs");
                    break;
                case "sessions":
                    DiagCapture.Save(popup, root, dir, "main-sessions");
                    DiagCapture.Save(popup, root, dir, "grid-2x2");
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
                    await SaveMenusAsync(dir);
                    await CaptureFullscreenAsync(popup, dir);
                    sidebar.Scroll.ScrollToTop();
                    Diag.SetFocused(sidebar.Search, true);
                    sidebar.Search.Text = "check";
                    await RefreshAsync();
                    await DiagCapture.SettleAsync(Dispatcher, 500);
                    DiagCapture.Save(popup, root, dir, "sidebar-search");
                    // WPF only: the find bar above checkout-fix's terminal, its current match selected (Ctrl+Shift+F).
                    if (tiles.GetValueOrDefault("vs-checkout-fix")?.Behaviour is { } finder)
                    {
                        finder.OpenFind("checkout");
                        await DiagCapture.SettleAsync(Dispatcher, 2500);
                        DiagCapture.Save(popup, root, dir, "find");
                        finder.CloseFind();
                    }
                    await CaptureErrorTileAsync(popup, dir);
                    break;
                case "hidden":
                    await CaptureNewSessionAsync(popup, dir);
                    await CaptureTakeoverAsync(popup, dir);
                    await CaptureHiddenAsync(popup, dir);
                    break;
                case "overview":
                    await CaptureOverviewAsync(popup, dir, fixtureFile!);
                    break;
                case "history":
                    await CaptureHistoryAsync(popup, dir);
                    break;
                case "wsl":
                    await CaptureWslAsync(popup, dir);
                    break;
                default:
                    DiagCapture.Save(popup, root, dir, Win.Layout is not null ? "main-sessions" : "main-empty");
                    if (name == "empty") await CaptureDialogsAsync(popup, dir);
                    break;
            }
        }
        finally
        {
            DiagCapture.WriteManifest(dir, status);
            await shell.DetachAsync();
        }
    }

    /// <summary>The context menus of e2e/visual-reference.spec.ts's menu states: their structure (menus.json, which the
    /// report compares with Electron's) and each menu as the shell draws it, open beside its row.</summary>
    async Task SaveMenusAsync(string dir)
    {
        var menus = new List<(string State, List<MenuSpec> Items, FrameworkElement? Target)>();
        foreach (var (stateId, label) in new[]
        {
            ("menu-list-idle", "checkout-fix"), ("menu-list-ended", "release-notes"), ("menu-list-found", "Add dark mode to the account settings"),
            ("menu-list-running", "ci-pipeline"), ("menu-list-pinned", "Pinned: plan the Q3 roadmap"),
        })
            if (sidebar.ChatOf(label) is { } chat) menus.Add((stateId, ListMenuOf(chat), sidebar.RowOf(label)));
        if (state.Sessions.ContainsKey("vs-checkout-fix")) menus.Add(("menu-tile", TileMenuOf("vs-checkout-fix"), null));
        foreach (var (stateId, items, target) in menus)
        {
            capturedMenus[stateId] = MenuSpec.ToJson(items);
            var menu = BuildMenu(items, null, null);
            menu.Placement = PlacementMode.Absolute;
            menu.HorizontalOffset = 300;
            menu.VerticalOffset = 100;
            menu.IsOpen = true;
            await DiagCapture.SettleAsync(Dispatcher, 300);
            DiagCapture.SaveElement(menu, dir, stateId);
            menu.IsOpen = false;
        }
        // The tray's menu (native: its structure only), with the fixture's windows and sessions.
        capturedMenus["menu-tray"] = MenuSpec.ToJson(shell.TrayMenu());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "menus.json"), capturedMenus.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    async Task SaveRowAsync(HwndSource popup, string dir, string stateId, string label)
    {
        if (sidebar.RowOf(label) is not { } row) return;
        row.BringIntoView();
        await DiagCapture.SettleAsync(Dispatcher, 200);
        DiagCapture.Save(popup, row, dir, stateId);
    }
}
