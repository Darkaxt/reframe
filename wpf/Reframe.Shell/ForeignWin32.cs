using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// win32.ts for the foreign window watcher: window move/size and top-level create/show events of other processes
/// (SetWinEventHook, out of context, so the callbacks come on the thread that set the hooks: the UI's), and the
/// window and process facts it asks for. Points and sizes in DIPs at the system scale, as the shell's window bounds.
/// </summary>
sealed class ForeignWin32 : IForeignWin32, IDisposable
{
    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const uint EVENT_SYSTEM_MOVESIZESTART = 0x000a, EVENT_SYSTEM_MOVESIZEEND = 0x000b, EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_SHOW = 0x8002;
    const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 0x0002, GA_ROOT = 2, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int OBJID_WINDOW = 0, CHILDID_SELF = 0;

    readonly List<IntPtr> hooks = [];
    // Kept alive as long as the hooks: Windows calls them.
    readonly List<WinEventProc> callbacks = [];

    static double Scale => GetDpiForSystem() / 96.0;
    static IntPtr Hwnd(string hwnd) => new(long.Parse(hwnd, CultureInfo.InvariantCulture));
    static string Text(IntPtr hwnd) => hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);

    /// <summary>Follows other windows' moves (<c>start</c> true at their beginning) and top-level windows being created
    /// (<c>created</c> true) or shown.</summary>
    public void Start(Action<bool, string> moveSize, Action<bool, string> topLevel)
    {
        Hook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, (_, e, hwnd, idObject, _, _, _) =>
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            moveSize(e == EVENT_SYSTEM_MOVESIZESTART, Text(hwnd));
        });
        // The range includes EVENT_OBJECT_DESTROY, which is skipped like every other event.
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, (_, e, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero) return;
            if (e != EVENT_OBJECT_CREATE && e != EVENT_OBJECT_SHOW) return;
            if (GetAncestor(hwnd, GA_ROOT) != hwnd) return; // a child window
            topLevel(e == EVENT_OBJECT_CREATE, Text(hwnd));
        });
    }

    void Hook(uint min, uint max, WinEventProc callback)
    {
        var hook = SetWinEventHook(min, max, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == IntPtr.Zero) throw new InvalidOperationException("SetWinEventHook failed");
        hooks.Add(hook);
        callbacks.Add(callback);
    }

    /// <summary>Removes the hooks (quitting).</summary>
    public void Dispose()
    {
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear();
        callbacks.Clear();
    }

    public int ProcessId(string hwnd)
    {
        GetWindowThreadProcessId(Hwnd(hwnd), out var pid);
        return (int)pid;
    }

    public string? ImageName(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? Path.GetFileName(new string(buffer, 0, (int)size)) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public (double Width, double Height)? Size(string hwnd)
    {
        if (!GetWindowRect(Hwnd(hwnd), out var r)) return null;
        var s = Scale;
        return ((r.Right - r.Left) / s, (r.Bottom - r.Top) / s);
    }

    bool IForeignWin32.IsWindow(string hwnd) => IsWindow(Hwnd(hwnd));

    public string Title(string hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowTextW(Hwnd(hwnd), text, text.Capacity);
        return text.ToString();
    }

    public string? Foreground() => GetForegroundWindow() is var w && w != IntPtr.Zero ? Text(w) : null;

    public ScreenPoint Cursor()
    {
        GetCursorPos(out var p);
        var s = Scale;
        return new ScreenPoint(p.X / s, p.Y / s);
    }
}
