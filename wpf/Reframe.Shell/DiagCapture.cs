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

    /// <summary>Shows <paramref name="root"/> in a borderless popup of exactly Width x Height at 0,0 (not a Window: a
    /// window is kept to the screen's size, and a CI runner's screen is 1024x768), never activated.</summary>
    public static HwndSource Host(FrameworkElement root)
    {
        const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        root.Width = Width;
        root.Height = Height;
        // The window's own text settings (MainWindow.xaml), which a popup does not have.
        var frame = new System.Windows.Controls.Border { Background = (System.Windows.Media.Brush)Application.Current.FindResource("bgBrush"), Child = root };
        frame.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, Application.Current.FindResource("FontFamily.Ui"));
        frame.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 13.0);
        frame.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Application.Current.FindResource("textBrush"));
        var source = new HwndSource(new HwndSourceParameters("Reframe Shell capture")
        {
            WindowStyle = WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            PositionX = 0, PositionY = 0, Width = Width, Height = Height,
        })
        {
            SizeToContent = SizeToContent.Manual,
            RootVisual = frame,
        };
        return source;
    }

    static readonly List<object> Captured = [];

    /// <summary>Lets layout and rendering settle (and the terminal draw what the host replays).</summary>
    public static async Task SettleAsync(Dispatcher dispatcher, int milliseconds)
    {
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(milliseconds);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Saves <paramref name="element"/>'s bounds in the popup as <paramref name="state"/>.png in the capture
    /// folder (the whole client area for the root). An <paramref name="overlay"/> (a dialog's backdrop) is drawn again
    /// over the terminals, which PrintWindow gives without it.</summary>
    public static void Save(HwndSource source, FrameworkElement element, string dir, string state, FrameworkElement? overlay = null)
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
            // PrintWindow gets what the desktop composed, which on a CI runner (1024x768 screen) can miss the part of
            // the 1280x800 popup off the screen (main-empty and tabs came out with black bands). So WPF renders the whole
            // popup itself, and only the native children (the terminals, which it cannot render) come from PrintWindow.
            var hosts = new List<System.Windows.Rect>();
            NativeChildren(source.RootVisual, source.RootVisual, hosts);
            var ok = hosts.Count > 0 && PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            SelectObject(dc, old);
            var printed = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var scene = new System.Windows.Media.DrawingVisual();
            var size = new System.Windows.Rect(0, 0, w / dpi.DpiScaleX, h / dpi.DpiScaleY);
            using (var draw = scene.RenderOpen())
            {
                draw.DrawRectangle(new System.Windows.Media.VisualBrush(source.RootVisual) { Stretch = System.Windows.Media.Stretch.None,
                    AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, size);
                if (ok)
                    foreach (var area in hosts)
                    {
                        draw.PushClip(new System.Windows.Media.RectangleGeometry(area));
                        draw.DrawImage(printed, size);
                        draw.Pop();
                    }
                if (ok && overlay is not null)
                {
                    // Only over the terminals: elsewhere the root's own drawing has the overlay already.
                    var native = new System.Windows.Media.GeometryGroup();
                    foreach (var area in hosts) native.Children.Add(new System.Windows.Media.RectangleGeometry(area));
                    draw.PushClip(native);
                    var over = overlay.TransformToAncestor(source.RootVisual).TransformBounds(new System.Windows.Rect(overlay.RenderSize));
                    draw.DrawRectangle(new System.Windows.Media.VisualBrush(overlay) { Stretch = System.Windows.Media.Stretch.None,
                        AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, over);
                    draw.Pop();
                }
            }
            var full = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
            full.Render(scene);
            BitmapSource image = crop.X == 0 && crop.Y == 0 && crop.Width == w && crop.Height == h ? full : new CroppedBitmap(full, crop);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(dir);
            using (var file = File.Create(Path.Combine(dir, state + ".png"))) encoder.Save(file);
            Captured.Add(new { state, width = crop.Width, height = crop.Height, client = $"{w}x{h}", printWindow = ok, native = hosts.Count });
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Saves an element WPF draws entirely (an open context menu, in its own popup window) as
    /// <paramref name="state"/>.png, rendered by WPF at its size.</summary>
    public static void SaveElement(FrameworkElement element, string dir, string state)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        int w = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;
        var image = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(dir);
        using (var file = File.Create(Path.Combine(dir, state + ".png"))) encoder.Save(file);
        Captured.Add(new { state, width = w, height = h, client = "popup", printWindow = false, native = 0 });
    }

    /// <summary>The bounds (in the popup) of the visible native children under <paramref name="parent"/>.</summary>
    static void NativeChildren(System.Windows.Media.Visual root, System.Windows.Media.Visual parent, List<System.Windows.Rect> found)
    {
        if (parent is HwndHost host)
        {
            if (host.IsVisible) found.Add(host.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(host.RenderSize)));
            return;
        }
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (System.Windows.Media.VisualTreeHelper.GetChild(parent, i) is System.Windows.Media.Visual child) NativeChildren(root, child, found);
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
