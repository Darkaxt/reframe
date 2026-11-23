using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Reframe.Shell;

/// <summary>
/// Tells in main.log when the UI thread does not answer (the windows do not respond to the user): a thread of its own
/// asks the dispatcher every half second for an Input-priority callback (where the user's keys and clicks wait), and a
/// callback that waited 400 ms or more is logged with how long, what the UI thread was doing (<see cref="Enter"/>, or
/// else the dispatcher operation that ran: a timer's handlers, an async method, a delegate's method),
/// how many terminal outputs waited on the dispatcher, and whether a full garbage collection ran meanwhile. A wait of
/// 5 seconds is logged while it lasts. A stall's stacks are logged at 1.8, 5, 10 and 20 seconds (a stall's first at
/// most once in 30 seconds): the UI thread's native and managed frames (<see cref="NativeStacks"/>), and, at most once
/// in ten minutes, those of the threads in the terminals' or graphics modules (module offsets and method names only). Each logged
/// wait also tells how much of it the UI thread spent running (busy, or idle waiting for messages), and whether a
/// Normal-priority callback got through meanwhile. One callback every half second is all it costs otherwise.
/// </summary>
static class UiWatchdog
{
    const int IntervalMs = 500;
    const int LogFromMs = 400;
    const int StillFromMs = 5000;
    /// <summary>When a stall's stacks are taken: the first early (most stalls end within a few seconds), then while it
    /// lasts.</summary>
    static readonly int[] StacksAtMs = [1800, 5000, 10000, 20000];

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
    static readonly TimeSpan StackEvery = TimeSpan.FromSeconds(30);
    static long othersTakenAt = long.MinValue;
    static readonly TimeSpan OthersEvery = TimeSpan.FromMinutes(10);

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);

    const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    static IntPtr uiThreadHandle;

    /// <summary>The UI thread's processor time so far (100 ns units), or -1.</summary>
    static long UiCpu() => uiThreadHandle != IntPtr.Zero && GetThreadTimes(uiThreadHandle, out _, out _, out var k, out var u) ? k + u : -1;

    /// <summary>Called on the UI thread.</summary>
    public static void Start(Dispatcher dispatcher)
    {
        uiThread = GetCurrentThreadId();
        uiThreadHandle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, uiThread);
        // Every dispatcher operation names itself while it runs (labels from Enter, inside it, are more precise).
        dispatcher.Hooks.OperationStarted += (_, e) =>
        {
            operationLabels.Push(activity);
            activity = OperationName(e.Operation);
        };
        dispatcher.Hooks.OperationCompleted += (_, _) =>
        {
            if (operationLabels.TryPop(out var before)) activity = before;
        };
        var thread = new Thread(() => Watch(dispatcher)) { IsBackground = true, Name = "reframe ui watchdog", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    static readonly Stack<string?> operationLabels = new();
    static readonly Dictionary<(MethodInfo, Type?), string> operationNames = new();
    static readonly FieldInfo? OperationMethod = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? OperationArgs = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? TimerTick = typeof(DispatcherTimer).GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>What a dispatcher operation runs: a timer's Tick handlers, an async method's continuation, or the
    /// delegate's method (on the UI thread).</summary>
    static string OperationName(DispatcherOperation operation)
    {
        try
        {
            if (OperationMethod?.GetValue(operation) is not Delegate method) return "an operation";
            var args = OperationArgs?.GetValue(operation);
            if (args is DispatcherTimer timer)
                return TimerTick?.GetValue(timer) is Delegate tick
                    ? "timer: " + string.Join(", ", tick.GetInvocationList().Select(h => Name(h.Method, h.Target?.GetType())))
                    : "a timer";
            // A posted callback (an await's continuation): what it continues.
            if (args is Delegate inner) method = inner;
            return Name(method.Method, method.Target?.GetType());
        }
        catch (Exception)
        {
            return "an operation";
        }
    }

    static string Name(MethodInfo method, Type? target)
    {
        if (operationNames.TryGetValue((method, target), out var name)) return name;
        // An async method's state machine box: the method.
        if (target is { IsGenericType: true } && target.Name.Contains("AsyncStateMachineBox") && target.GetGenericArguments() is [.., var machine])
            name = $"{machine.DeclaringType?.Name}.{machine.Name}";
        else name = $"{method.DeclaringType?.Name}.{method.Name}";
        operationNames[(method, target)] = name;
        return name;
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
            var stacks = 0;
            while (!served.Wait(100))
            {
                if (dispatcher.HasShutdownStarted) return;
                if (normalAt < 0 && servedNormal.IsSet) normalAt = clock.ElapsedMilliseconds;
                var now = activity;
                if (clock.ElapsedMilliseconds >= LogFromMs)
                {
                    first ??= now ?? "-";
                    last = now ?? "-";
                }
                if (!toldStill && clock.ElapsedMilliseconds >= StillFromMs)
                {
                    toldStill = true;
                    Trace.WriteLine($"ui: not responding for {clock.ElapsedMilliseconds} ms so far (doing: {last}; terminal outputs waiting: {Volatile.Read(ref outputsQueued)})");
                }
                // The stall's stacks (StacksAtMs); a stall's first at most once in 30 seconds.
                var stackDue = stacks < StacksAtMs.Length && clock.ElapsedMilliseconds >= StacksAtMs[stacks] &&
                    (stacks > 0 || stackTakenAt == long.MinValue || Stopwatch.GetElapsedTime(stackTakenAt) >= StackEvery);
                if (stackDue)
                {
                    if (stacks == 0) stackTakenAt = Stopwatch.GetTimestamp();
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
            Trace.WriteLine($"ui: did not respond for {waited} ms (doing: {doing}; terminal outputs waiting at most: " +
                $"{Volatile.Read(ref outputsPeak)}, {Volatile.Read(ref outputsDispatched) - dispatchedBefore} queued meanwhile{gc}{cpu}{normal})");
        }
    }

    static readonly string[] GraphicsModules = ["Microsoft.Terminal.Control", "d3d11", "d3d10warp", "dxgi", "nvwgf", "nvldumd", "wpfgfx", "msctf", "dcomp"];

    /// <summary>The UI thread's frames, innermost first, then the other threads that are in the terminals' or graphics
    /// modules (identical stacks counted once), from a snapshot of this process.</summary>
    static string UiStack()
    {
        try
        {
            var threads = NativeStacks.Read(uiThread, out var managed);
            var ui = threads.FirstOrDefault(t => t.Thread == uiThread).Frames;
            var uiText = (ui is null ? "(the UI thread was not found)" : string.Join(" <- ", ui)) +
                $" || managed, from the snapshot: {(managed is { Count: > 0 } ? string.Join(" <- ", managed) : "(none)")}";
            // The other threads (a long line) at most once in ten minutes.
            if (othersTakenAt != long.MinValue && Stopwatch.GetElapsedTime(othersTakenAt) < OthersEvery) return uiText;
            othersTakenAt = Stopwatch.GetTimestamp();
            var others = threads
                .Where(t => t.Thread != uiThread && t.Frames.Any(f => GraphicsModules.Any(m => f.StartsWith(m, StringComparison.OrdinalIgnoreCase))))
                .GroupBy(t => string.Join(" <- ", t.Frames.Take(12)))
                .Select(g => $"{g.Count()}x: {g.Key}")
                .Take(12);
            return uiText + $" || terminal and graphics threads: {string.Join(" | ", others)}";
        }
        catch (Exception e)
        {
            return $"(not read: {e.GetType().Name}: {e.Message})";
        }
    }
}
