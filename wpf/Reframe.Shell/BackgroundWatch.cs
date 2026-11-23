using System.Runtime.InteropServices;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Reads the process table for <see cref="BackgroundWork"/>: Toolhelp32 for pids, parents and exes, each
/// process's start time, and the command line only of the given claudes' descendants. No WMI; call it off the UI
/// thread. A process that cannot be read (gone, protected) is skipped.</summary>
public static class BackgroundWatch
{
    /// <summary>What each claude pid runs in the background (absent: nothing, or it could not be read).</summary>
    public static Dictionary<int, (string Label, DateTime Since)> Describe(IReadOnlyCollection<int> claudePids)
    {
        var result = new Dictionary<int, (string, DateTime)>();
        var table = Snapshot();
        if (table.Count == 0) return result;
        var children = table.ToLookup(p => p.Parent);
        // The claudes and their descendants: the only processes whose start time and command line are read.
        var wanted = new HashSet<int>();
        var queue = new Queue<int>(claudePids);
        foreach (var pid in claudePids) wanted.Add(pid);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (child.Pid != child.Parent && wanted.Add(child.Pid)) queue.Enqueue(child.Pid);
        var processes = new List<ProcessEntry>();
        foreach (var (pid, parent, exe) in table)
        {
            if (!wanted.Contains(pid)) continue;
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (!GetProcessTimes(handle, out var created, out _, out _, out _)) continue;
                var commandLine = claudePids.Contains(pid) ? null : CommandLine(handle);
                processes.Add(new ProcessEntry(pid, parent, exe, commandLine, DateTime.FromFileTimeUtc(created)));
            }
            finally { CloseHandle(handle); }
        }
        foreach (var pid in claudePids)
            if (BackgroundWork.Describe(pid, processes) is { } work) result[pid] = work;
        return result;
    }

    static List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    /// <summary>ProcessCommandLineInformation (60): a UNICODE_STRING followed by its buffer.</summary>
    static string? CommandLine(IntPtr handle)
    {
        var size = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, 60, buffer, size, out var needed);
                if (status == 0)
                {
                    var length = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return length == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
                }
                // STATUS_INFO_LENGTH_MISMATCH, BUFFER_OVERFLOW or BUFFER_TOO_SMALL: retry with the size it asked for.
                if (status is unchecked((int)0xC0000004) or unchecked((int)0x80000005) or unchecked((int)0xC0000023) && needed > size) size = needed;
                else return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    const uint Th32csSnapProcess = 0x2;
    const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);
}
