using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Reframe.Shell;

/// <summary>
/// Diagnostics for the visual parity report (<c>--diag-capture &lt;dir&gt;</c>, tools/visual): saves the window's client
/// area as &lt;state id&gt;.png. PrintWindow with PW_RENDERFULLCONTENT also gets the terminal, a native child window that
/// RenderTargetBitmap leaves out. The state ids are those of e2e/visual-reference.spec.ts, so the report pairs them.
/// </summary>
static class DiagCapture
{
    /// <summary>The Electron reference's page size (CSS px), so both sides are the same size at 100%.</summary>
    public const int Width = 1280, Height = 800;

    const uint PW_CLIENTONLY = 0x1, PW_RENDERFULLCONTENT = 0x2;

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>Shows <paramref name="root"/> in a borderless, never activated window whose client area is exactly
    /// Width x Height at 0,0, past the screen's size (a CI runner's is 1024x768). A real Window, not a bare HwndSource:
    /// the terminal's HwndHost crashes natively without one.</summary>
    public static HwndSource Host(FrameworkElement root)
    {
        root.Width = Width;
        root.Height = Height;
        // The window's own text settings (MainWindow.xaml).
        var frame = new System.Windows.Controls.Border { Background = (System.Windows.Media.Brush)Application.Current.FindResource("bgBrush"), Child = root };
        frame.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, Application.Current.FindResource("FontFamily.Ui"));
        frame.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 13.0);
        frame.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Application.Current.FindResource("textBrush"));
        var window = new Window
        {
            Title = "Reframe Shell capture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0, Top = 0, Width = Width, Height = Height, Content = frame,
        };
        window.SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                const int WM_GETMINMAXINFO = 0x0024;
                if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                info.MaxTrackX = info.MaxTrackY = 8192;
                Marshal.StructureToPtr(info, lParam, false);
                handled = true;
                return IntPtr.Zero;
            });
        window.Show();
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowRect(handle, out var outer);
        GetClientRect(handle, out var inner);
        int dx = (outer.Right - outer.Left) - (inner.Right - inner.Left), dy = (outer.Bottom - outer.Top) - (inner.Bottom - inner.Top);
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(handle, IntPtr.Zero, 0, 0, Width + dx, Height + dy, SWP_NOZORDER | SWP_NOACTIVATE);
        return HwndSource.FromHwnd(handle)!;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo { public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static readonly List<object> Captured = [];

    /// <summary>Lets layout and rendering settle (and the terminal draw what the host replays).</summary>
    public static async Task SettleAsync(Dispatcher dispatcher, int milliseconds)
    {
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(milliseconds);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Saves <paramref name="element"/>'s bounds in the popup as <paramref name="state"/>.png in the capture
    /// folder (the whole client area for the root).</summary>
    public static void Save(HwndSource source, FrameworkElement element, string dir, string state)
    {
        var hwnd = source.Handle;
        GetClientRect(hwnd, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        var bounds = element.TransformToAncestor(source.RootVisual).TransformBounds(new System.Windows.Rect(element.RenderSize));
        var crop = new Int32Rect((int)Math.Round(bounds.X * dpi.DpiScaleX), (int)Math.Round(bounds.Y * dpi.DpiScaleY),
            (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY));
        crop.Width = Math.Min(crop.Width, w - crop.X);
        crop.Height = Math.Min(crop.Height, h - crop.Y);
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bitmap);
        try
        {
            var ok = PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            SelectObject(dc, old);
            var full = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var image = crop.X == 0 && crop.Y == 0 && crop.Width == w && crop.Height == h ? full : new CroppedBitmap(full, crop);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(dir);
            using (var file = File.Create(Path.Combine(dir, state + ".png"))) encoder.Save(file);
            Captured.Add(new { state, width = crop.Width, height = crop.Height, client = $"{w}x{h}", printWindow = ok });
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Adds what this run captured, with its status line (why a state may be missing), to capture.json (one
    /// folder collects several runs, e.g. one without a host and one with sessions).</summary>
    public static void WriteManifest(string dir, string status)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "capture.json");
        var runs = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!["runs"]!.AsArray() : new JsonArray();
        runs.Add(JsonSerializer.SerializeToNode(new { status, captures = Captured }));
        var manifest = new JsonObject { ["side"] = "wpf", ["runs"] = runs.DeepClone() };
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
