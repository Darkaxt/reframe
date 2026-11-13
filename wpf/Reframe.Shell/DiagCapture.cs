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

    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo { public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY; }

    /// <summary>Lets the window grow past the screen (a CI runner's is 1024x768), so the client area really is
    /// Width x Height: Windows otherwise caps the window at the screen size (WM_GETMINMAXINFO), and WPF with it.</summary>
    public static void AllowLargerThanScreen(Window window) => window.SourceInitialized += (_, _) =>
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

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Sizes the native window so that its client area is Width x Height device pixels (set directly: WPF's own
    /// sizing stops at the screen size it read at start).</summary>
    public static void SizeClient(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var outer);
        GetClientRect(hwnd, out var inner);
        int dx = (outer.Right - outer.Left) - (inner.Right - inner.Left), dy = (outer.Bottom - outer.Top) - (inner.Bottom - inner.Top);
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, Width + dx, Height + dy, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static readonly List<object> Captured = [];

    /// <summary>Lets layout and rendering settle (and the terminal draw what the host replays).</summary>
    public static async Task SettleAsync(Window window, int milliseconds)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(milliseconds);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Saves the window's client area as <paramref name="state"/>.png in the capture folder.</summary>
    public static void Save(Window window, string dir, string state)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetClientRect(hwnd, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bitmap);
        try
        {
            var ok = PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            SelectObject(dc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            Directory.CreateDirectory(dir);
            using (var file = File.Create(Path.Combine(dir, state + ".png"))) encoder.Save(file);
            Captured.Add(new { state, width = w, height = h, printWindow = ok });
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
