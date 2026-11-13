using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// The notification area icon (src/main/tray.ts), as Electron's Tray is on Windows: Shell_NotifyIcon with the 16 px
/// tray.png (tray@2x.png from 150% scale up), and a popup menu of the system's own (CreatePopupMenu/TrackPopupMenuEx:
/// its look, check marks and greyed items are Windows'). WPF has no tray; this avoids WinForms' NotifyIcon (its
/// ContextMenuStrip draws a menu of its own, unlike the system's) and a third-party package (H.NotifyIcon). A left click shows the
/// first window (tray.on('click')), a right click opens the menu. The icon is put back when Explorer restarts.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const int WM_APP_TRAY = 0x8000 + 1, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 0x1, MF_CHECKED = 0x8;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    readonly HwndSource window;
    readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly IntPtr icon;
    readonly Func<List<MenuSpec>> menu;
    readonly Action<string> run;
    readonly Action click;
    string tooltip;
    bool added;

    public TrayIcon(string tooltip, Func<List<MenuSpec>> menu, Action<string> run, Action click)
    {
        this.tooltip = tooltip;
        this.menu = menu;
        this.run = run;
        this.click = click;
        // A message-only window for the icon's messages (HWND_MESSAGE).
        window = new HwndSource(new HwndSourceParameters("Reframe tray") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        window.AddHook(Hook);
        icon = LoadIcon();
        Add();
    }

    /// <summary>tray.png at 100–149%, tray@2x.png above (Electron's nativeImage with a 2x representation).</summary>
    static IntPtr LoadIcon()
    {
        var large = GetDpiForSystem() >= 144;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(large ? "tray@2x.png" : "tray.png")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        var size = large ? 32 : 16;
        return CreateIconFromResourceEx(data, (uint)data.Length, true, 0x00030000, size, size, 0);
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = window.Handle, uID = 1, uFlags = flags,
        uCallbackMessage = WM_APP_TRAY, hIcon = icon, szTip = tooltip, szInfo = "", szInfoTitle = "",
    };

    void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    /// <summary>tray.setToolTip.</summary>
    public void SetTooltip(string text)
    {
        if (text == tooltip) return;
        tooltip = text;
        var data = Data(NIF_TIP);
        if (added) Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)taskbarCreated)
        {
            Add();
            return IntPtr.Zero;
        }
        if (msg != WM_APP_TRAY) return IntPtr.Zero;
        switch ((int)lParam & 0xFFFF)
        {
            case WM_LBUTTONUP:
                click();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>The menu as built now (tray.ts refreshes it whenever windows or sessions change; built on opening here).</summary>
    void ShowMenu()
    {
        var items = menu();
        var handle = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Type == "separator") AppendMenu(handle, MF_SEPARATOR, UIntPtr.Zero, null);
                else
                    AppendMenu(handle, MF_STRING | (item.Enabled ? 0 : MF_GRAYED) | (item.Checked == true ? MF_CHECKED : 0),
                        (UIntPtr)(uint)(i + 1), item.Label.Replace("&", "&&"));
            }
            GetCursorPos(out var at);
            // The menu closes when clicked elsewhere only if its owner is in front (the documented TrackPopupMenu rule).
            SetForegroundWindow(window.Handle);
            var chosen = TrackPopupMenuEx(handle, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, at.X, at.Y, window.Handle, IntPtr.Zero);
            PostMessage(window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && items[chosen - 1].Action is { } action) run(action);
        }
        finally
        {
            DestroyMenu(handle);
        }
    }

    public void Dispose()
    {
        var data = Data(0);
        if (added) Shell_NotifyIcon(NIM_DELETE, ref data);
        added = false;
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.Dispose();
    }
}
