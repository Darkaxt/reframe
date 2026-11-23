using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Reframe.Shell;

/// <summary>
/// Tells in main.log when the UI thread does not answer (the windows do not respond to the user): a thread of its own
/// asks the dispatcher every half second for an Input-priority callback (where the user's keys and clicks wait), and a
/// callback that waited 400 ms or more is logged with how long, what the UI thread was doing (<see cref="Enter"/>),
/// how many terminal outputs waited on the dispatcher, and whether a full garbage collection ran meanwhile. A wait of
/// 5 seconds is logged while it lasts, with the UI thread's managed stack then (read from a snapshot of the process,
/// ClrMD; at most once in ten minutes, with two more 5 and 15 seconds later; only method names are logged). Each logged
/// wait also tells how much of it the UI thread spent running (busy, or idle waiting for messages), and whether a
/// Normal-priority callback got through meanwhile. After 2 seconds the dispatcher's own "process the queue" message is
/// posted again, in case it was lost. One callback every half second is all it costs otherwise.
/// </summary>
static class UiWatchdog
{
    const int IntervalMs = 500;
    const int LogFromMs = 400;
    const int StillFromMs = 5000;

    static volatile string? activity;
    static int outputsQueued;
    static int outputsPeak;
    static int outputsDispatched;

    /// <summary>The UI thread does something worth naming in a stall (dispose when done).</summary>
    public static Scope Enter(string what)
    {
        var before = activity;
        activity = what;
        return new Scope(before);
    }

    public readonly struct Scope(string? before) : IDisposable
    {
        public void Dispose() => activity = before;
    }

    /// <summary>A terminal output was queued on the dispatcher.</summary>
    public static void OutputQueued()
    {
        var now = Interlocked.Increment(ref outputsQueued);
        Interlocked.Increment(ref outputsDispatched);
        int peak;
        while (now > (peak = Volatile.Read(ref outputsPeak)) && Interlocked.CompareExchange(ref outputsPeak, now, peak) != peak) { }
    }

    /// <summary>A queued terminal output ran.</summary>
    public static void OutputRan() => Interlocked.Decrement(ref outputsQueued);

    static uint uiThread;
    static long stackTakenAt = long.MinValue;
    static readonly TimeSpan StackEvery = TimeSpan.FromMinutes(10);

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    const int WakeFromMs = 2000;
    static IntPtr uiThreadHandle;
    static IntPtr dispatcherWindow;
    static uint processQueue;

    /// <summary>The UI thread's processor time so far (100 ns units), or -1.</summary>
    static long UiCpu() => uiThreadHandle != IntPtr.Zero && GetThreadTimes(uiThreadHandle, out _, out _, out var k, out var u) ? k + u : -1;

    /// <summary>Called on the UI thread.</summary>
    public static void Start(Dispatcher dispatcher)
    {
        uiThread = GetCurrentThreadId();
        uiThreadHandle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, uiThread);
        // The dispatcher's own window and the message that makes it run its queue (WPF's "DispatcherProcessQueue"):
        // a stall with the UI thread idle in GetMessage looks like that message was lost; the watchdog posts it again.
        try
        {
            var window = typeof(Dispatcher).GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(dispatcher);
            if (window?.GetType().GetProperty("Handle")?.GetValue(window) is IntPtr handle) dispatcherWindow = handle;
        }
        catch (Exception e) when (e is System.Reflection.TargetInvocationException or MemberAccessException or ArgumentException)
        {
        }
        processQueue = RegisterWindowMessage("DispatcherProcessQueue");
        Trace.WriteLine($"ui: watchdog on (dispatcher window {(dispatcherWindow != IntPtr.Zero ? "found" : "not found")})");
        var thread = new Thread(() => Watch(dispatcher)) { IsBackground = true, Name = "reframe ui watchdog", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    static void Watch(Dispatcher dispatcher)
    {
        using var served = new ManualResetEventSlim();
        using var servedNormal = new ManualResetEventSlim();
        var clock = new Stopwatch();
        while (!dispatcher.HasShutdownStarted)
        {
            Thread.Sleep(IntervalMs);
            served.Reset();
            servedNormal.Reset();
            var cpuBefore = UiCpu();
            var gen2 = GC.CollectionCount(2);
            Interlocked.Exchange(ref outputsPeak, Volatile.Read(ref outputsQueued));
            var dispatchedBefore = Volatile.Read(ref outputsDispatched);
            clock.Restart();
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Input, () => served.Set());
                dispatcher.BeginInvoke(DispatcherPriority.Normal, () => servedNormal.Set());
            }
            catch (InvalidOperationException)
            {
                return;
            }
            // What the UI thread does while the callback waits: the first activity seen, and the last.
            string? first = null, last = null;
            var toldStill = false;
            long normalAt = -1;
            long wokenAt = -1;
            var stacks = 0;
            while (!served.Wait(100))
            {
                if (dispatcher.HasShutdownStarted) return;
                if (normalAt < 0 && servedNormal.IsSet) normalAt = clock.ElapsedMilliseconds;
                var now = activity;
                if (wokenAt < 0 && clock.ElapsedMilliseconds >= WakeFromMs && dispatcherWindow != IntPtr.Zero && processQueue != 0)
                {
                    wokenAt = clock.ElapsedMilliseconds;
                    PostMessage(dispatcherWindow, processQueue, IntPtr.Zero, IntPtr.Zero);
                }
                if (clock.ElapsedMilliseconds >= LogFromMs)
                {
                    first ??= now ?? "-";
                    last = now ?? "-";
                }
                if (!toldStill && clock.ElapsedMilliseconds >= StillFromMs)
                {
                    toldStill = true;
                    Trace.WriteLine($"ui: not responding for {clock.ElapsedMilliseconds} ms so far (doing: {last}; terminal outputs waiting: {Volatile.Read(ref outputsQueued)})");
                    if (Stopwatch.GetElapsedTime(stackTakenAt) >= StackEvery || stackTakenAt == long.MinValue)
                    {
                        stackTakenAt = Stopwatch.GetTimestamp();
                        stacks = 1;
                        Trace.WriteLine($"ui: the UI thread's stack: {UiStack()}");
                    }
                }
                // Two more stacks of the same stall, 5 and 15 seconds later.
                if (stacks is 1 or 2 && clock.ElapsedMilliseconds >= StillFromMs + (stacks == 1 ? 5000 : 15000))
                {
                    stacks++;
                    Trace.WriteLine($"ui: the UI thread's stack at {clock.ElapsedMilliseconds} ms: {UiStack()}");
                }
            }
            var waited = clock.ElapsedMilliseconds;
            if (waited < LogFromMs) continue;
            var doing = first == last ? first : $"{first}, then {last}";
            var gc = GC.CollectionCount(2) > gen2 ? "; a full garbage collection ran" : "";
            if (normalAt < 0 && servedNormal.IsSet) normalAt = waited;
            var cpuAfter = UiCpu();
            var cpu = cpuBefore >= 0 && cpuAfter >= 0 ? $"; UI thread busy {(cpuAfter - cpuBefore) / 10_000} ms of it" : "";
            var normal = normalAt >= 0 ? $"; Normal priority served after {normalAt} ms" : "; Normal priority not served either";
            var woke = wokenAt >= 0 ? $"; the dispatcher's queue message was posted again at {wokenAt} ms" : "";
            Trace.WriteLine($"ui: did not respond for {waited} ms (doing: {doing}; terminal outputs waiting at most: " +
                $"{Volatile.Read(ref outputsPeak)}, {Volatile.Read(ref outputsDispatched) - dispatchedBefore} queued meanwhile{gc}{cpu}{normal}{woke})");
        }
    }

    /// <summary>The UI thread's managed frames, innermost first, from a snapshot of this process (the process goes on
    /// meanwhile; the snapshot is released at once).</summary>
    static string UiStack()
    {
        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            var thread = runtime.Threads.FirstOrDefault(t => t.OSThreadId == uiThread);
            if (thread is null) return "(the UI thread was not found)";
            var frames = thread.EnumerateStackTrace().Take(40)
                .Select(f => f.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : $"[{f.FrameName ?? f.Kind.ToString()}]")
                .ToList();
            return frames.Count == 0 ? "(no managed frames)" : string.Join(" <- ", frames);
        }
        catch (Exception e)
        {
            return $"(not read: {e.GetType().Name}: {e.Message})";
        }
    }
}
