using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace Reframe.Shell;

/// <summary>
/// Tells in main.log when the UI thread does not answer (the windows do not respond to the user): a thread of its own
/// asks the dispatcher every half second for an Input-priority callback (where the user's keys and clicks wait), and a
/// callback that waited 400 ms or more is logged with how long, what the UI thread was doing (<see cref="Enter"/>),
/// how many terminal outputs waited on the dispatcher, and whether a full garbage collection ran meanwhile. A wait of
/// 5 seconds is logged while it lasts. One callback every half second is all it costs.
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

    public static void Start(Dispatcher dispatcher)
    {
        var thread = new Thread(() => Watch(dispatcher)) { IsBackground = true, Name = "reframe ui watchdog", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    static void Watch(Dispatcher dispatcher)
    {
        using var served = new ManualResetEventSlim();
        var clock = new Stopwatch();
        while (!dispatcher.HasShutdownStarted)
        {
            Thread.Sleep(IntervalMs);
            served.Reset();
            var gen2 = GC.CollectionCount(2);
            Interlocked.Exchange(ref outputsPeak, Volatile.Read(ref outputsQueued));
            var dispatchedBefore = Volatile.Read(ref outputsDispatched);
            clock.Restart();
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Input, () => served.Set());
            }
            catch (InvalidOperationException)
            {
                return;
            }
            // What the UI thread does while the callback waits: the first activity seen, and the last.
            string? first = null, last = null;
            var toldStill = false;
            while (!served.Wait(100))
            {
                if (dispatcher.HasShutdownStarted) return;
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
            }
            var waited = clock.ElapsedMilliseconds;
            if (waited < LogFromMs) continue;
            var doing = first == last ? first : $"{first}, then {last}";
            var gc = GC.CollectionCount(2) > gen2 ? "; a full garbage collection ran" : "";
            Trace.WriteLine($"ui: did not respond for {waited} ms (doing: {doing}; terminal outputs waiting at most: " +
                $"{Volatile.Read(ref outputsPeak)}, {Volatile.Read(ref outputsDispatched) - dispatchedBefore} queued meanwhile{gc})");
        }
    }
}
