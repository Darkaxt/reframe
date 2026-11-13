using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Reframe.Shell;

/// <summary>The window's taskbar button drawing the user's attention (windows.ts flash, badge and isAttentive).</summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
    const uint FLASHW_STOP = 0, FLASHW_TRAY = 2, FLASHW_TIMERNOFG = 12;

    /// <summary>isAttentive: the user is looking at this window right now.</summary>
    public bool Attentive => IsVisible && WindowState != System.Windows.WindowState.Minimized && IsActive;

    /// <summary>flash: the taskbar button flashes until the window comes to the front (flashFrame(true)).</summary>
    public void Flash() => FlashFrame(FLASHW_TRAY | FLASHW_TIMERNOFG);

    /// <summary>flashFrame(false) on focus.</summary>
    void StopFlash() => FlashFrame(FLASHW_STOP);

    void FlashFrame(uint flags)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = flags, uCount = uint.MaxValue };
        FlashWindowEx(ref info);
    }

    static ImageSource? attentionBadge;

    /// <summary>attentionBadge (images.ts): a 16 px circle of radius 6 in the waiting dot's colour (#e0b04a).</summary>
    static ImageSource AttentionBadge()
    {
        if (attentionBadge is not null) return attentionBadge;
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x + 0.5 - size / 2.0, 2) + Math.Pow(y + 0.5 - size / 2.0, 2));
                var alpha = Math.Max(0, Math.Min(1, 6.5 - distance));
                var i = (y * size + x) * 4;
                pixels[i] = (byte)Math.Round(0x4a * alpha);
                pixels[i + 1] = (byte)Math.Round(0xb0 * alpha);
                pixels[i + 2] = (byte)Math.Round(0xe0 * alpha);
                pixels[i + 3] = (byte)Math.Round(255 * alpha);
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return attentionBadge = bitmap;
    }

    /// <summary>badge: the overlay on the taskbar button while <paramref name="count"/> sessions in it need the user.</summary>
    public void Badge(int count, string description)
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = count > 0 ? AttentionBadge() : null;
        TaskbarItemInfo.Description = description;
    }

    void WatchAttention() => Activated += (_, _) => StopFlash();
}
