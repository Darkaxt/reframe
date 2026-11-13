using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Reframe.Shell.Core;
using WindowState = Reframe.Shell.Core.WindowState;

namespace Reframe.Shell;

/// <summary>
/// The window as one of reframe's windows (src/main/windows.ts): restored from its state entry (bounds kept only while
/// on a display, maximised, full screen), its bounds and visibility saved as they change, closing handed to the
/// ShellController (the close-window policy), F11 full screen with the "Press F11" hint and the toolbar's exit button,
/// and the window shortcuts of shortcuts.ts: New window, the session list, full screen, Ctrl+1…9, Ctrl+Tab, Ctrl+Shift+
/// arrows and the next session needing the user.
/// </summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>The state window's id (null until a capture has loaded its fixture).</summary>
    string? windowId;
    readonly Button exitFullscreenButton;
    bool fullscreen, fullscreenPending, discarded;
    Rect normalBounds;
    System.Windows.WindowState beforeFullscreen;
    Popup? fullscreenHint;

    public string WindowId => Win.Id;

    static double Scale => GetDpiForSystem() / 96.0;

    /// <summary>windows.ts open(): the saved size (and place, while on a display), maximised and full screen once shown.</summary>
    public void Restore(WindowState win, IReadOnlyList<Box> workAreas)
    {
        MinWidth = ShellWindows.MinWidth;
        MinHeight = ShellWindows.MinHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (ShellWindows.VisibleBounds(win.Bounds, workAreas) is { } b)
        {
            Width = b.Width;
            Height = b.Height;
            if (b.X is { } x && b.Y is { } y)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x;
                Top = y;
            }
        }
        if (win.Maximized) WindowState = System.Windows.WindowState.Maximized;
        fullscreenPending = win.Fullscreen == true;
    }

    void WatchWindow()
    {
        LocationChanged += (_, _) => ReportBounds();
        SizeChanged += (_, _) => ReportBounds();
        StateChanged += (_, _) =>
        {
            root.Margin = WindowState == System.Windows.WindowState.Maximized && !fullscreen ? new Thickness(7) : new Thickness(0);
            ReportBounds();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (discarded) return;
            shell.VisibilityChanged(WindowId, !IsVisible);
            if (IsVisible && fullscreenPending)
            {
                fullscreenPending = false;
                Dispatcher.BeginInvoke(() => SetFullscreen(true));
            }
        };
        Closed += (_, _) =>
        {
            foreach (var id in tiles.Keys.ToList()) DisposeTile(id);
            shell.StatusChanged -= ShowStatus;
        };
    }

    /// <summary>X and Alt+F4 do not close: the ShellController asks or hides (windows.ts 'closeRequested').</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (App.CaptureDir is not null || shell.Quitting || discarded) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => shell.CloseRequested(WindowId));
    }

    /// <summary>Closes the window for good (an empty secondary window).</summary>
    public void Discard()
    {
        discarded = true;
        Close();
    }

    /// <summary>The window's bounds and maximised state, saved soon (full screen keeps the bounds it returns to).</summary>
    void ReportBounds()
    {
        if (!IsLoaded || fullscreen || WindowState == System.Windows.WindowState.Minimized) return;
        var maximized = WindowState == System.Windows.WindowState.Maximized;
        var r = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        if (r.IsEmpty || double.IsNaN(r.X)) return;
        var win = Win;
        if (win.Bounds is { } b && b.X == Math.Round(r.X) && b.Y == Math.Round(r.Y) && b.Width == Math.Round(r.Width) && b.Height == Math.Round(r.Height)
            && win.Maximized == maximized) return;
        win.Bounds = new Bounds { X = Math.Round(r.X), Y = Math.Round(r.Y), Width = Math.Round(r.Width), Height = Math.Round(r.Height) };
        win.Maximized = maximized;
        shell.SaveSoon();
    }

    /// <summary>Where the window is on the screen now (DIPs), maximised or not (getBounds()).</summary>
    public Bounds? CurrentBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        var s = Scale;
        return new Bounds { X = Math.Round(r.Left / s), Y = Math.Round(r.Top / s), Width = Math.Round((r.Right - r.Left) / s), Height = Math.Round((r.Bottom - r.Top) / s) };
    }

    bool IsInside(Point p) => p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight;

    /// <summary>A point of this window on the screen, in DIPs (screen.getCursorScreenPoint).</summary>
    Point ScreenPoint(Point p)
    {
        var screen = PointToScreen(p);
        return new Point(screen.X / Scale, screen.Y / Scale);
    }

    /// <summary>Shows the window and brings it to the front (a second launch, the tray, the next waiting session).</summary>
    public void BringForward()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized) WindowState = System.Windows.WindowState.Normal;
        Activate();
        // Activate alone may only flash the taskbar button; the asking launch let us take the foreground.
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>The ShellController changed this window's layout (tear-out, a tile moved here, a session ended).</summary>
    public void LayoutChanged()
    {
        if (!discarded) Relayout();
    }

    /// <summary>Brings a thread up (showThread): selected (the one a single window shows) and its terminal focused.</summary>
    public void FocusTile(string? sessionId)
    {
        if (sessionId is null || !LayoutActions.WindowHasSession(Win, sessionId)) return;
        if (LayoutActions.IsCollapsed(Win, sessionId) && Writable) Act(new LayoutAction.TileExpand(sessionId));
        SelectTile(sessionId);
    }

    /// <summary>The tray's New session (and Ctrl+Shift+T): the New Session dialog is not ported yet, so the window only
    /// comes forward.</summary>
    public void OpenNewSession() { }

    public void OpenAboutFromTray() => OpenAbout();

    void NewWindow() => shell.NewWindow(WindowId);

    /// <summary>window/sidebar: the session list shown or not, kept per window; opened, its search has the focus.</summary>
    void ToggleSidebar()
    {
        var open = sidebar.Visibility != Visibility.Visible;
        ShowSidebar(open);
        if (Writable) Act(new LayoutAction.WindowSidebar(open));
        if (open) Dispatcher.BeginInvoke(() => Keyboard.Focus(sidebar.Search), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- full screen ----------

    void ToggleFullscreen() => SetFullscreen(!fullscreen);

    /// <summary>F11 (setFullScreen): the window covers its display, without the window buttons; the toolbar shows how
    /// to leave it, and the hint appears for a moment. Saved as the window's <c>fullscreen</c>, its bounds kept.</summary>
    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (on)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
            beforeFullscreen = WindowState;
            normalBounds = new Rect(Left, Top, Width, Height);
            fullscreen = true;
            if (WindowState == System.Windows.WindowState.Maximized) WindowState = System.Windows.WindowState.Normal;
            var s = Scale;
            var m = info.rcMonitor;
            ResizeMode = ResizeMode.NoResize;
            Left = m.Left / s;
            Top = m.Top / s;
            Width = (m.Right - m.Left) / s;
            Height = (m.Bottom - m.Top) / s;
            root.Margin = new Thickness(0);
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            Left = normalBounds.Left;
            Top = normalBounds.Top;
            Width = normalBounds.Width;
            Height = normalBounds.Height;
            WindowState = beforeFullscreen;
            fullscreen = false;
        }
        captionButtons.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        exitFullscreenButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Win.Fullscreen = on;
        shell.SaveSoon();
        if (on) ShowFullscreenHint();
        else if (fullscreenHint is not null) fullscreenHint.IsOpen = false;
    }

    /// <summary>.fullscreen-hint: 14px from the top, centred, 8px 16px, border-strong, radius 8, surface-2, shadow; fades
    /// in, stays and fades out over 3.5 s. A popup, as nothing WPF draws can be over the terminals' native windows.</summary>
    void ShowFullscreenHint()
    {
        var box = FullscreenHintBox();
        box.Opacity = 0;
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (fullscreenHint is not null) fullscreenHint.IsOpen = false;
        var popup = fullscreenHint = new Popup
        {
            Child = box, AllowsTransparency = true, PlacementTarget = root, Placement = PlacementMode.Relative, IsHitTestVisible = false,
            HorizontalOffset = Math.Max(0, (Width - box.DesiredSize.Width) / 2), VerticalOffset = 14,
        };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.5) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Completed += (_, _) => popup.IsOpen = false;
        popup.IsOpen = true;
        box.BeginAnimation(OpacityProperty, fade);
    }

    static Border FullscreenHintBox() => new()
    {
        Padding = new Thickness(16, 8, 16, 8), BorderBrush = B("border-strongBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Background = B("surface-2Brush"),
        Child = new TextBlock { Text = Strings.T("fullscreen.hint"), Style = S("Text13"), Foreground = B("textBrush") },
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.35 },
    };

    /// <summary>Diagnostics (WPF only, Electron's reference has no full screen state): the toolbar in full screen (the
    /// exit button, no window buttons) and the hint on its own.</summary>
    async Task CaptureFullscreenAsync(HwndSource popup, string dir)
    {
        captionButtons.Visibility = Visibility.Collapsed;
        exitFullscreenButton.Visibility = Visibility.Visible;
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "fullscreen");
        captionButtons.Visibility = Visibility.Visible;
        exitFullscreenButton.Visibility = Visibility.Collapsed;
        var hint = FullscreenHintBox();
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        hint.Arrange(new Rect(hint.DesiredSize));
        hint.UpdateLayout();
        DiagCapture.SaveElement(hint, dir, "fullscreen-hint");
    }

    // ---------- shortcuts ----------

    /// <summary>The ids a shortcut moves between (tileIds): a single window's threads, else the tiles shown.</summary>
    List<string> TileIds() => LayoutActions.ViewModeOf(Win) == "single" ? Layout.Leaves(Win.Layout).ToList() : places.Tiles.Select(t => t.SessionId).ToList();

    void WindowKeys(KeyEventArgs e)
    {
        if (e.Handled) return;
        var active = focused ?? selected;
        var ids = TileIds();
        switch (Shortcuts.For(TerminalBehaviour.PressOf(e)))
        {
            case Shortcut.Named { Kind: "newWindow" }:
                NewWindow();
                break;
            case Shortcut.Named { Kind: "newSession" }:
                OpenNewSession();
                break;
            case Shortcut.Named { Kind: "toggleFullscreen" }:
                ToggleFullscreen();
                break;
            case Shortcut.Named { Kind: "toggleSidebar" }:
                ToggleSidebar();
                break;
            case Shortcut.Named { Kind: "nextWaiting" }:
                shell.FocusNextWaiting(WindowId, active);
                break;
            case Shortcut.FocusTile tile:
                if (tile.Index < ids.Count) FocusTile(ids[tile.Index]);
                break;
            case Shortcut.CycleTile cycle:
                FocusTile(ViewModes.Cycle(ids, active, cycle.Step));
                break;
            case Shortcut.FocusDirection direction when LayoutActions.ViewModeOf(Win) == "single":
                FocusTile(ViewModes.Cycle(ids, active, direction.Direction is "left" or "up" ? -1 : 1));
                break;
            case Shortcut.FocusDirection direction:
                if (active is null || !ids.Contains(active)) FocusTile(ids.FirstOrDefault());
                else FocusTile(ShellWindows.Neighbor([.. places.Tiles.Select(t => (t.SessionId, t.Box))], active, direction.Direction));
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
