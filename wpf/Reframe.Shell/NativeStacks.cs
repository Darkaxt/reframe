using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Reframe.Shell;

/// <summary>
/// The stacks of this process's threads, native and managed frames together. Each thread's registers and stack are read
/// as they are (GetThreadContext, ReadProcessMemory: a read that fails ends the walk, it cannot crash; a thread that
/// waits, as a stalled one does, reads consistently), and walked by DbgHelp with the unwind tables of this process's
/// modules and of its JIT-compiled code. Native frames are named by module and offset ("win32u.dll+0x1234", resolved
/// later against the module's symbols), managed frames by type and method (from a snapshot of the process, ClrMD).
/// </summary>
public static class NativeStacks
{
    /// <summary>Each thread's frames, innermost first (threads whose context could not be read are left out).</summary>
    public static List<(uint Thread, List<string> Frames)> Read(int maxFrames = 40) => Read(0, out _, maxFrames);

    /// <summary>As <see cref="Read(int)"/>, and the managed frames of one thread as the snapshot has them. A thread that
    /// runs while it is walked can give a torn native stack; the snapshot's is consistent (taken while the thread was
    /// briefly stopped), so it names what a busy thread did.</summary>
    public static List<(uint Thread, List<string> Frames)> Read(uint managedOf, out List<string>? managed, int maxFrames = 40)
    {
        // The stacks first, as they are now; then the names.
        uint[] ids;
        using (var process = Process.GetCurrentProcess())
            ids = process.Threads.Cast<ProcessThread>().Select(t => (uint)t.Id).ToArray();
        var self = GetCurrentThreadId();
        var walked = ids.Where(id => id != self).Select(id => (Id: id, Ips: Walk(id, maxFrames))).Where(t => t.Ips.Count > 0).ToList();
        using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        managed = runtime.Threads.FirstOrDefault(t => t.OSThreadId == managedOf)?.EnumerateStackTrace().Take(maxFrames)
            .Select(f => f.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : $"[{f.FrameName ?? f.Kind.ToString()}]")
            .ToList();
        var modules = target.EnumerateModules()
            .Select(m => (Base: m.ImageBase, End: m.ImageBase + (ulong)m.ImageSize, Name: Path.GetFileName(m.FileName) ?? "?"))
            .ToArray();
        string Name(ulong ip)
        {
            if (runtime.GetMethodByInstructionPointer(ip) is { } method) return $"{method.Type?.Name}.{method.Name}";
            foreach (var (start, end, name) in modules)
                if (ip >= start && ip < end) return $"{name}+0x{ip - start:x}";
            return $"0x{ip:x}";
        }
        return walked.Select(t => (t.Id, t.Ips.Select(Name).ToList())).ToList();
    }

    /// <summary>The return addresses from the context's instruction pointer outwards.</summary>
    static List<ulong> Walk(uint threadId, int maxFrames)
    {
        var ips = new List<ulong>();
        var thread = OpenThread(ThreadGetContext | ThreadQueryInformation, false, threadId);
        if (thread == IntPtr.Zero) return ips;
        // CONTEXT needs 16-byte alignment.
        var contextMemory = Marshal.AllocHGlobal(ContextSize + 16);
        var frame = Marshal.AllocHGlobal(FrameSize);
        try
        {
            var ctx = (IntPtr)(((long)contextMemory + 15) & ~15L);
            Marshal.Copy(new byte[ContextSize], 0, ctx, ContextSize);
            Marshal.WriteInt32(ctx, ContextFlagsAt, (int)ContextFull);
            if (!GetThreadContext(thread, ctx)) return ips;
            Marshal.Copy(new byte[FrameSize], 0, frame, FrameSize);
            // STACKFRAME64: AddrPC, AddrReturn, AddrFrame, AddrStack (ADDRESS64: Offset, Segment, Mode), flat addresses.
            Marshal.WriteInt64(frame, 0, Marshal.ReadInt64(ctx, RipAt));
            Marshal.WriteInt32(frame, 12, AddrModeFlat);
            Marshal.WriteInt64(frame, 32, Marshal.ReadInt64(ctx, RbpAt));
            Marshal.WriteInt32(frame, 44, AddrModeFlat);
            Marshal.WriteInt64(frame, 48, Marshal.ReadInt64(ctx, RspAt));
            Marshal.WriteInt32(frame, 60, AddrModeFlat);
            ReadMemory read = (IntPtr _, ulong address, IntPtr buffer, uint size, out uint done) =>
            {
                var ok = ReadProcessMemory(CurrentProcess, (IntPtr)(long)address, buffer, (IntPtr)size, out var count);
                done = (uint)count;
                return ok;
            };
            FunctionTableAccess table = (IntPtr _, ulong address) => RtlLookupFunctionEntry(address, out ulong _, IntPtr.Zero);
            GetModuleBase moduleBase = (IntPtr _, ulong address) =>
                RtlLookupFunctionEntry(address, out ulong image, IntPtr.Zero) == IntPtr.Zero ? 0UL : image;
            var seen = new HashSet<(ulong, ulong)>();
            while (ips.Count < maxFrames &&
                   StackWalk64(MachineAmd64, CurrentProcess, IntPtr.Zero, frame, ctx, read, table, moduleBase, IntPtr.Zero))
            {
                var ip = (ulong)Marshal.ReadInt64(frame, 0);
                var sp = (ulong)Marshal.ReadInt64(frame, 48);
                if (ip == 0 || !seen.Add((ip, sp))) break;
                ips.Add(ip);
            }
            GC.KeepAlive(read);
            GC.KeepAlive(table);
            GC.KeepAlive(moduleBase);
        }
        finally
        {
            Marshal.FreeHGlobal(frame);
            Marshal.FreeHGlobal(contextMemory);
            CloseHandle(thread);
        }
        return ips;
    }

    // x64 CONTEXT: 1232 bytes; CONTEXT_FULL; Rsp, Rbp and Rip offsets.
    const int ContextSize = 1232;
    const uint ContextFull = 0x0010000B;
    const int ContextFlagsAt = 0x30;
    const uint ThreadGetContext = 0x0008;
    const uint ThreadQueryInformation = 0x0040;
    const int RspAt = 0x98;
    const int RbpAt = 0xA0;
    const int RipAt = 0xF8;
    // STACKFRAME64 is 264 bytes; room to spare.
    const int FrameSize = 512;
    const int AddrModeFlat = 3;
    const uint MachineAmd64 = 0x8664;
    static readonly IntPtr CurrentProcess = new(-1);

    delegate bool ReadMemory(IntPtr process, ulong address, IntPtr buffer, uint size, out uint read);
    delegate IntPtr FunctionTableAccess(IntPtr process, ulong address);
    delegate ulong GetModuleBase(IntPtr process, ulong address);

    [DllImport("dbghelp.dll")]
    static extern bool StackWalk64(uint machineType, IntPtr process, IntPtr thread, IntPtr stackFrame, IntPtr contextRecord,
        ReadMemory readMemory, FunctionTableAccess functionTableAccess, GetModuleBase getModuleBase, IntPtr translateAddress);

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, out IntPtr read);

    [DllImport("ntdll.dll")]
    static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);
}
