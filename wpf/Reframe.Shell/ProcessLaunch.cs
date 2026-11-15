using System.Runtime.InteropServices;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>How a process was started: its executable and command line, read with query-only access (no other right on
/// it), for a claude taken over from another terminal to go on the same way here.</summary>
static class ProcessLaunch
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);

    /// <summary>The launch options of process <paramref name="pid"/> (Takeover.LaunchOf); null when it cannot be read.</summary>
    public static LaunchOptions? Of(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            return Takeover.LaunchOf(Image(process), CommandLine(process));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static string? Image(IntPtr process)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>The command line (Windows 8.1+): a UNICODE_STRING followed by its text, in one buffer.</summary>
    static string? CommandLine(IntPtr process)
    {
        NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length > 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text, length / 2) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
