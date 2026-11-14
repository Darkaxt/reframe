using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// A tile's terminal as src/renderer/terminals.ts sets xterm up: its look (font, size, colours, block cursor, a 14px
/// scrollbar that shows only while the mouse is over the terminal), Enter-copy and Shift+Enter, Ctrl+C/V, right-click
/// copy or paste, pastes of text, files and images (images and in-memory drops saved under the drops folder), file
/// drops, the copy notice and the "hold Shift" hint, and find. Nothing is drawn over the terminal (its native window
/// would hide it): the find bar sits above it and the notice in the tile's header.
/// </summary>
sealed class TerminalBehaviour
{
    /// <summary>reframe's terminal look (THEME in terminals.ts) and xterm.js's default 16 colours, which reframe keeps;
    /// xterm's default cursor is a blinking block. Win32 COLORREF is 0x00BBGGRR.</summary>
    public static readonly TerminalTheme Theme = new()
    {
        DefaultBackground = Rgb(0x17181c),
        DefaultForeground = Rgb(0xd7dae0),
        DefaultSelectionBackground = Rgb(0x3a3f4b),
        CursorStyle = CursorStyle.BlinkingBlock,
        ColorTable =
        [
            Rgb(0x000000), Rgb(0xcd3131), Rgb(0x0dbc79), Rgb(0xe5e510), Rgb(0x2472c8), Rgb(0xbc3fbc), Rgb(0x11a8cd), Rgb(0xe5e5e5),
            Rgb(0x666666), Rgb(0xf14c4c), Rgb(0x23d18b), Rgb(0xf5f543), Rgb(0x3b8eea), Rgb(0xd670d6), Rgb(0x29b8db), Rgb(0xe5e5e5),
        ],
    };
    /// <summary>FONT in terminals.ts starts with Cascadia Mono (the control takes one family; Windows has it since 11).</summary>
    public const string FontFamilyName = "Cascadia Mono";
    static readonly Color Background = Color.FromRgb(0x17, 0x18, 0x1c);
    /// <summary>xterm's scrollbar: 14px (what FitAddon leaves for it), its slider only while the pointer is over the terminal.</summary>
    const double ScrollbarWidth = 14;

    static uint Rgb(int rgb) => (uint)(((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff));

    [DllImport("user32.dll")] static extern int ToUnicode(uint vk, uint scan, byte[] state, StringBuilder buffer, int size, uint flags);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);

    const int WM_MOUSEWHEEL = 0x20a, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_MOUSEMOVE = 0x200;
    const int MK_LBUTTON = 0x1, MK_SHIFT = 0x4;

    readonly TerminalControl control;
    readonly HostTerminalConnection terminal;
    readonly Func<PasteText.Wsl?> wsl;
    readonly Action<string> notice;
    readonly Border findBar;
    readonly TextBox findInput;
    readonly TextBlock findCount;
    HwndHost? host;
    bool hinted;
    (double X, double Y)? dragStart;
    int fontPx;
    readonly DispatcherTimer findRefresh;

    /// <summary>xterm's scrollback in terminals.ts. Set before any terminal is created (it applies to those created after).</summary>
    const int Scrollback = 5000;
    static TerminalBehaviour() => TerminalControl.SetDefaultHistorySize(Scrollback);

    public TerminalBehaviour(TerminalControl control, HostTerminalConnection terminal, int fontSize, Func<PasteText.Wsl?> wsl, Action<string> notice)
    {
        this.control = control;
        this.terminal = terminal;
        this.wsl = wsl;
        this.notice = notice;
        fontPx = fontSize;
        control.PreviewKeyDown += KeyDown;
        control.Loaded += (_, _) =>
        {
            ApplyFont();
            if (host is not null) return;
            host = FindHost(control);
            if (host is not null) host.MessageHook += Hook;
            StyleScrollbar();
        };
        (findBar, findInput, findCount) = BuildFindBar();
        // Output moves the text under the marks: once it settles, the search runs again (keeping the view where it is),
        // as xterm's search addon redecorates on writes.
        findRefresh = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => RefreshFind(), control.Dispatcher);
        findRefresh.Stop();
        terminal.TerminalOutput += (_, _) =>
        {
            if (!FindOpen || findInput.Text.Length == 0) return;
            findRefresh.Stop();
            findRefresh.Start();
        };
    }

    /// <summary>The find bar (.tile-find), above the terminal while it is open.</summary>
    public FrameworkElement FindBar => findBar;

    public bool FindOpen => findBar.Visibility == Visibility.Visible;

    /// <summary>The terminals' font size (CSS px, as reframe's preferences keep it).</summary>
    public void SetFontSize(int px)
    {
        if (px == fontPx) return;
        fontPx = px;
        if (control.IsLoaded) ApplyFont();
    }

    /// <summary>Theme and font: the patched control's TerminalSetFont takes fractional points and xterm's row height
    /// (Shortcuts.TerminalFont); SetTheme's whole points are replaced by it at once.</summary>
    void ApplyFont()
    {
        var (pt, cellHeight) = Shortcuts.TerminalFont(fontPx);
        control.SetTheme(Theme, FontFamilyName, (short)Math.Max(1, Math.Round(pt)), Background);
        control.SetFont(FontFamilyName, pt, "", $"{cellHeight}px");
    }

    // ---------- keys ----------

    /// <summary>The character a key makes with the Shift and AltGr state it is pressed with (KeyboardEvent.key), Ctrl left out.</summary>
    public static string CharOf(Key key)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var state = new byte[256];
        GetKeyboardState(state);
        foreach (var ctrl in new[] { 0x11, 0xa2, 0xa3 }) state[ctrl] = 0;
        var buffer = new StringBuilder(8);
        var n = ToUnicode(vk, MapVirtualKey(vk, 0), state, buffer, buffer.Capacity, 4);
        return n > 0 ? buffer.ToString(0, n) : "";
    }

    /// <summary>The physical key as KeyboardEvent.code names it (only those shortcuts.ts looks at).</summary>
    public static string CodeOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => "Key" + key,
        >= Key.D0 and <= Key.D9 => "Digit" + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => "Numpad" + (key - Key.NumPad0),
        Key.Add => "NumpadAdd",
        Key.Subtract => "NumpadSubtract",
        Key.OemPlus => "Equal",
        Key.OemMinus => "Minus",
        Key.OemComma => "Comma",
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.F11 => "F11",
        _ => key.ToString(),
    };

    public static KeyPress PressOf(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        return new KeyPress(CodeOf(key), CharOf(key), m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Windows),
            m.HasFlag(ModifierKeys.Shift), m.HasFlag(ModifierKeys.Alt));
    }

    /// <summary>Enter-copy, Shift+Enter, Ctrl+C with a selection and Ctrl+V (terminals.ts's custom key handler). A handled
    /// key down is not passed on, so no WM_CHAR follows it and nothing reaches the session.</summary>
    void KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        bool shift = m.HasFlag(ModifierKeys.Shift), ctrl = m.HasFlag(ModifierKeys.Control), alt = m.HasFlag(ModifierKeys.Alt);
        if (!e.Handled && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab && host is not null)
        {
            ForwardKey(key, alt, e.IsRepeat);
            e.Handled = true;
            return;
        }
        if (key == Key.Enter)
        {
            // GetSelectedText takes the selection off as it reads it, as the copy does.
            var selected = ctrl || alt ? "" : control.GetSelectedText();
            switch (TerminalKeys.EnterKey(shift, ctrl, alt, selected.Length > 0))
            {
                case EnterAction.Copy:
                    Copy(selected);
                    e.Handled = true;
                    break;
                case EnterAction.Newline:
                    terminal.WriteInput(TerminalKeys.Newline);
                    e.Handled = true;
                    break;
            }
            return;
        }
        if (key is not (Key.C or Key.V) || !ctrl) return;
        var text = key == Key.C && !alt ? control.GetSelectedText() : "";
        switch (TerminalKeys.ClipboardKey(key == Key.C ? 'c' : 'v', ctrl, shift, alt, text.Length > 0))
        {
            case "copy":
                if (text.Length > 0) Copy(text);
                e.Handled = true;
                break;
            case "paste":
                PasteClipboard();
                e.Handled = true;
                break;
        }
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    const int WM_KEYDOWN = 0x100, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSCHAR = 0x106;

    /// <summary>Arrows and Tab go straight to the terminal's window. WPF's keyboard navigation would otherwise take them
    /// (an arrow moves the focus to the next element that way, Tab to the next tab stop) and drop the message before the
    /// terminal sees it. Sent, not posted, so it skips the message loop's filter; Tab also gets the WM_CHAR that
    /// TranslateMessage would have made.</summary>
    void ForwardKey(Key key, bool alt, bool repeat)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var scan = MapVirtualKey(vk, 0);
        var lParam = 1L | ((long)scan << 16) | (key == Key.Tab ? 0 : 1L << 24) | (alt ? 1L << 29 : 0) | (repeat ? 1L << 30 : 0);
        SendMessage(host!.Handle, alt ? WM_SYSKEYDOWN : WM_KEYDOWN, (IntPtr)vk, (IntPtr)lParam);
        if (key == Key.Tab) SendMessage(host.Handle, alt ? WM_SYSCHAR : WM_CHAR, (IntPtr)9, (IntPtr)lParam);
    }

    /// <summary>Copies the selection's text, says so; the selection is gone already (as the Windows console does).</summary>
    void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            notice($"Copied: {TerminalKeys.CopyPreview(text)}");
        }
        catch (COMException)
        {
            notice("Could not copy: the clipboard is busy");
        }
    }

    // ---------- paste and drops ----------

    /// <summary>Pastes text; files copied in a file manager come in as their paths; an image is saved to a file and comes
    /// in as its path (Claude Code reads the clipboard through PowerShell on Windows, slowly, and not at all in WSL), or,
    /// when it cannot be saved outside WSL, as Claude Code's own image-paste key.</summary>
    public void PasteClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return;
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
                terminal.Paste(text);
            else if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                PastePaths(files);
            else if (Clipboard.ContainsImage())
            {
                var session = wsl();
                var file = PasteText.PastesImageAsFile(true, session is not null) ? SaveImage(Clipboard.GetImage(), "clipboard.png") : null;
                if (file is not null) PastePaths([file]);
                else if (session is null) terminal.WriteInput(PasteText.ImagePasteKey(true));
            }
        }
        catch (COMException)
        {
            notice("Could not paste: the clipboard is busy");
        }
    }

    /// <summary>Types file paths into the session, as a paste (so Claude Code attaches images among them).</summary>
    public void PastePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        terminal.Paste(PasteText.FormatPaths(paths, true, wsl()));
        // Back into the terminal (a drop came from elsewhere): WPF focus alone (HwndHost.Focus) took Win32 focus away
        // from the terminal's native window, so a pasted image left the session without the keyboard.
        if (host is { } h)
            h.Dispatcher.InvokeAsync(() =>
            {
                Keyboard.Focus(h);
                if (h.Handle != IntPtr.Zero) SetFocus(h.Handle);
            }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A drop on the tile: files by their paths; an image that exists only in memory (dragged from a browser)
    /// saved under the drops folder first.</summary>
    public bool Drop(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            PastePaths(files);
            return true;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image && SaveImage(image, "dropped.png") is { } file)
        {
            PastePaths([file]);
            return true;
        }
        return false;
    }

    public static bool CanDrop(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap);

    static string? SaveImage(BitmapSource? image, string name)
    {
        if (image is null) return null;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return Drops.Save(name, buffer.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // ---------- mouse ----------

    /// <summary>The terminal's native window: right-click copies the selection or pastes (reframe's paste, not the
    /// control's, which would leave out bracketing, files and images), unless the program reads the mouse and Shift is
    /// up; a drag in such a program says once that Shift selects.</summary>
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var buttons = (int)wParam;
        var shift = (buttons & MK_SHIFT) != 0;
        switch (msg)
        {
            case WM_RBUTTONDOWN when !terminal.Modes.MouseTracking || shift:
                var selected = control.GetSelectedText();
                if (selected.Length > 0) Copy(selected);
                else PasteClipboard();
                handled = true;
                break;
            case WM_LBUTTONDOWN:
                dragStart = Point(lParam);
                break;
            case WM_LBUTTONUP:
                dragStart = null;
                break;
            case WM_MOUSEWHEEL:
                showScrollbar?.Invoke();
                break;
            case WM_MOUSEMOVE:
                showScrollbar?.Invoke();
                if (hinted) break;
                var (x, y) = Point(lParam);
                if (TerminalKeys.DragWithoutShift(dragStart, x, y, (buttons & MK_LBUTTON) != 0, shift, terminal.Modes.MouseTracking))
                {
                    hinted = true;
                    notice("Hold Shift to select text");
                }
                break;
        }
        return IntPtr.Zero;
    }

    static (double, double) Point(IntPtr lParam) => ((short)((long)lParam & 0xffff), (short)(((long)lParam >> 16) & 0xffff));

    // ---------- scrollbar ----------

    /// <summary>The control's WPF scrollbar as xterm draws its own: 14px, no track or arrows, a slider of
    /// rgba(121,121,121,.4) (VS Code's scrollbarSlider) shown while the pointer is over the terminal.</summary>
    void StyleScrollbar()
    {
        if (FindChild<ScrollBar>(control) is not { } bar) return;
        bar.Width = ScrollbarWidth;
        bar.MinWidth = ScrollbarWidth;
        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x66, 0x79, 0x79, 0x79)));
        var thumbTemplate = new ControlTemplate(typeof(Thumb)) { VisualTree = thumb };
        var track = new FrameworkElementFactory(typeof(Track), "PART_Track");
        track.SetValue(Track.IsDirectionReversedProperty, true);
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.AppendChild(track);
        bar.Template = new ControlTemplate(typeof(ScrollBar)) { VisualTree = root };
        bar.ApplyTemplate();
        if (bar.Track is { } t) t.Thumb = new Thumb { Template = thumbTemplate };
        // Shown while the pointer moves over the terminal or the bar, gone a second after it stops (xterm fades it).
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Show(bool on)
        {
            if (bar.Track?.Thumb is { } th) th.Opacity = on ? 1 : 0;
            hide.Stop();
            if (on && !bar.IsMouseOver) hide.Start();
        }
        hide.Tick += (_, _) => Show(bar.IsMouseOver);
        Show(false);
        bar.MouseEnter += (_, _) => Show(true);
        bar.MouseLeave += (_, _) => Show(true);
        showScrollbar = () => Show(true);
    }

    Action? showScrollbar;

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    static HwndHost? FindHost(DependencyObject parent) => parent as HwndHost ?? FindChild<HwndHost>(parent);

    // ---------- find ----------

    (Border, TextBox, TextBlock) BuildFindBar()
    {
        Brush B(string key) => (Brush)Application.Current.FindResource(key);
        Style S(string key) => (Style)Application.Current.FindResource(key);
        // .tile-find-input: 180px, 3px 6px, 1px border, radius 4, the terminal's background, 12px.
        var input = new TextBox
        {
            Width = 180, Padding = new Thickness(5, 2, 5, 2), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1),
            Background = B("terminalBrush"), Foreground = B("textBrush"), CaretBrush = B("textBrush"), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center,
        };
        input.Template = InputTemplate(B("borderBrush"), B("accentBrush"), B("placeholderBrush"));
        input.Tag = Strings.T("find.placeholder");
        AutomationProperties.SetName(input, Strings.T("find.placeholder"));
        var count = new TextBlock { MinWidth = 64, Padding = new Thickness(6, 0, 6, 0), Foreground = B("mutedBrush"), FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Button Icon(string kind, string name, Action click)
        {
            var b = new Button { Style = S("IconButton"), Content = new VectorIcon { Kind = kind, Size = 14 }, ToolTip = name, Margin = new Thickness(2, 0, 0, 0) };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => click();
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(input);
        row.Children.Add(count);
        row.Children.Add(Icon("chevron-up", "Previous match (Shift+Enter)", () => _ = Go(false)));
        row.Children.Add(Icon("chevron-down", "Next match (Enter)", () => _ = Go(true)));
        row.Children.Add(Icon("close", "Close (Esc)", CloseFind));
        var bar = new Border
        {
            Padding = new Thickness(6, 3, 4, 3), BorderBrush = B("border-strongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = B("surface-2Brush"), Child = row, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 18, 2),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(bar, "Find in the terminal");
        input.TextChanged += (_, _) => _ = Search(true, incremental: true);
        input.PreviewKeyDown += (_, e) =>
        {
            // Keys typed here belong to the field, not to the terminal around it.
            if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
            else if (e.Key == Key.Enter) { _ = Go(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
        };
        return (bar, input, count);
    }

    /// <summary>The find field: its frame, the text and, while empty, the placeholder (the Tag) dimmed.</summary>
    static ControlTemplate InputTemplate(Brush border, Brush focus, Brush placeholder)
    {
        var frame = new FrameworkElementFactory(typeof(Border), "Frame");
        frame.SetValue(Border.BorderBrushProperty, border);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var hint = new FrameworkElementFactory(typeof(TextBlock), "Placeholder");
        hint.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(FrameworkElement.TagProperty));
        hint.SetValue(TextBlock.ForegroundProperty, placeholder);
        hint.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 0, 0));
        hint.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        hint.SetValue(UIElement.IsHitTestVisibleProperty, false);
        hint.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(hint);
        var content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        grid.AppendChild(content);
        frame.AppendChild(grid);
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = frame };
        var empty = new Trigger { Property = TextBox.TextProperty, Value = "" };
        empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Placeholder"));
        template.Triggers.Add(empty);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, focus, "Frame"));
        template.Triggers.Add(focused);
        return template;
    }

    /// <summary>Ctrl+Shift+F: opens the find bar with the focus in it (again: back into it).</summary>
    public void OpenFind(string? text = null)
    {
        findBar.Visibility = Visibility.Visible;
        if (text is not null) findInput.Text = text;
        findInput.SelectAll();
        _ = findBar.Dispatcher.InvokeAsync(() => Keyboard.Focus(findInput), DispatcherPriority.Input);
    }

    public void CloseFind()
    {
        findBar.Visibility = Visibility.Collapsed;
        findInput.Text = "";
        findCount.Text = "";
        findRefresh.Stop();
        // xterm clears its marks when the find bar closes.
        control.ClearSearch();
        if (host is not null) { Keyboard.Focus(host); }
    }

    Task Go(bool next) => Search(next, incremental: false);

    /// <summary>Finds the text in the terminal, its scrollback too (case-insensitive, as xterm's search addon), through
    /// the patched control's TerminalSearch: every match is marked, the current one in the focused colour and scrolled
    /// into view. Typing keeps the current match; Enter and Shift+Enter go on and come round.</summary>
    Task Search(bool next, bool incremental)
    {
        var text = findInput.Text;
        if (!control.IsLoaded) return Task.CompletedTask;
        if (text.Length == 0)
        {
            findCount.Text = "";
            control.ClearSearch();
            return Task.CompletedTask;
        }
        ShowCount(control.Search(text, goForward: next, caseSensitive: false, executeSearch: !incremental));
        return Task.CompletedTask;
    }

    void RefreshFind()
    {
        findRefresh.Stop();
        if (!FindOpen || findInput.Text.Length == 0 || !control.IsLoaded) return;
        ShowCount(control.Search(findInput.Text, goForward: true, caseSensitive: false, executeSearch: false, scrollIntoView: false));
    }

    void ShowCount((int Current, int Total) result) =>
        findCount.Text = result.Total == 0 || result.Current < 0 ? "No results" : $"{result.Current + 1} of {result.Total}";
}
