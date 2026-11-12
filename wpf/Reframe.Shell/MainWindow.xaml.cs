using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;
using Reframe.Shell.Core;

namespace Reframe.Shell;

sealed class SessionItem(HostSession session) : INotifyPropertyChanged
{
    public HostSession Session { get; } = session;
    string name = session.SessionId;
    string status = session.ExitCode is null ? "running" : $"ended ({session.ExitCode})";

    public string Name { get => name; set => Set(ref name, value); }
    public string Status { get => status; set => Set(ref status, value); }
    /// <summary>The registry's name, kept so that a later title does not lose to it (and the other way round).</summary>
    public bool HasTitle { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public partial class MainWindow : Window
{
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
    /// <summary>reframe's normal font size (FONT_SIZE.normal in src/shared/state.ts).</summary>
    const short TerminalFontSize = 13;

    readonly ObservableCollection<SessionItem> sessions = [];
    HostConnection? connection;
    HostServices? services;
    IDisposable? shellLock;

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = sessions;
        Loaded += async (_, _) => await AttachAsync();
        Closed += async (_, _) =>
        {
            // Leaves the host with every session running (never "shutdown").
            if (connection is not null) await connection.DisposeAsync();
            shellLock?.Dispose();
        };
    }

    static uint Rgb(uint rgb) => ((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff);

    async Task AttachAsync()
    {
        StatusText.Text = $"Profile {App.ProfileDir}: attaching...";
        var result = await ShellAttach.AttachAsync(App.ProfileDir, App.TakeOver);
        StatusText.Text = $"Profile {App.ProfileDir}: {result.Message}";
        if (result.Connection is null) return;
        connection = result.Connection;
        shellLock = result.Lock;
        connection.Closed += replaced => Dispatcher.InvokeAsync(() =>
            StatusText.Text = replaced ? "Another client took the profile over: detached." : "The pty host connection closed.");
        services = new HostServices(connection);
        services.StatusChanged += status => Dispatcher.InvokeAsync(() => Apply(status));
        foreach (var session in connection.Welcome.Sessions) sessions.Add(new SessionItem(session));
        try
        {
            foreach (var status in await services.ListSessionsAsync()) Apply(status);
        }
        catch (HostServiceException e)
        {
            StatusText.Text += $" (sessions: {e.Message})";
        }
        foreach (var item in sessions.ToList()) _ = LoadTitleAsync(item, item.Session.ClaudeSessionId);
    }

    void Apply(SessionStatus status)
    {
        var item = sessions.FirstOrDefault(s => s.Session.SessionId == status.SessionId);
        if (item is null) return;
        item.Status = status.Status;
        if (!item.HasTitle && !string.IsNullOrEmpty(status.Name)) item.Name = status.Name;
        if (status.ClaudeSessionId is not null) _ = LoadTitleAsync(item, status.ClaudeSessionId);
    }

    async Task LoadTitleAsync(SessionItem item, string? claudeSessionId)
    {
        if (services is null || item.Session.ClaudeHome is null || claudeSessionId is null) return;
        try
        {
            var title = await services.TitleAsync(item.Session.ClaudeHome, claudeSessionId);
            if (string.IsNullOrEmpty(title)) return;
            item.Name = title;
            item.HasTitle = true;
        }
        catch (HostServiceException) { }
    }

    void SessionList_Open(object sender, MouseButtonEventArgs e) => OpenSelected();

    void SessionList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelected();
    }

    void OpenSelected()
    {
        if (connection is not { IsOpen: true } || SessionList.SelectedItem is not SessionItem item) return;
        var open = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == item);
        if (open is not null)
        {
            Tabs.SelectedItem = open;
            return;
        }
        var control = new TerminalControl { AutoResize = true };
        var terminal = new HostTerminalConnection(connection.CreateView(item.Session.SessionId), control);
        var connected = false;
        control.Loaded += (_, _) =>
        {
            control.SetTheme(Theme, FontFamilyName, TerminalFontSize, Color.FromRgb(0x17, 0x18, 0x1c));
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

        var header = new DockPanel();
        var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = item.Name });
        var tab = new TabItem { Header = header, Content = control, Tag = item };
        close.Click += (_, _) =>
        {
            terminal.Close(); // detaches the view only; the session goes on
            Tabs.Items.Remove(tab);
        };
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
    }
}
