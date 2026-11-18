using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The per-thread meters: every 3 s while a window shows, the current host's <c>monitor.sample</c> goes to
/// the windows' tiles. Sessions of an older host (or a host without the service) show no meters.</summary>
sealed partial class ShellController
{
    static readonly TimeSpan MonitorEvery = TimeSpan.FromSeconds(3);
    DispatcherTimer? monitorTimer;
    bool sampling;
    bool monitorFailed;
    /// <summary>The services of each older host still keeping sessions: their sessions are sampled too.</summary>
    readonly Dictionary<HostConnection, HostServices> olderHostServices = [];

    void StartMonitor()
    {
        if (Services is not { } services || !services.Offers("monitor")) return;
        monitorTimer = new DispatcherTimer { Interval = MonitorEvery };
        monitorTimer.Tick += async (_, _) => await SampleAsync(services);
        monitorTimer.Start();
    }

    async Task SampleAsync(HostServices services)
    {
        if (sampling || Quitting || Connection is not { IsOpen: true }) return;
        if (!windows.Values.Any(w => w.IsVisible && w.WindowState != System.Windows.WindowState.Minimized)) return;
        sampling = true;
        try
        {
            var usage = await services.MonitorSampleAsync();
            // A thread kept by an older host (after a restart of reframe) is sampled by that host.
            foreach (var (host, older) in olderHostServices.ToList())
            {
                if (!host.IsOpen) continue;
                try
                {
                    foreach (var (id, use) in await older.MonitorSampleAsync()) usage[id] = use;
                }
                catch (Exception e) when (e is HostServiceException or InvalidOperationException or System.IO.IOException) { }
            }
            monitorFailed = false;
            foreach (var window in windows.Values) window.ShowUsage(usage);
        }
        catch (HostServiceException e)
        {
            // Said once, not every 3 s.
            if (!monitorFailed) System.Diagnostics.Trace.WriteLine($"monitor: {e.Message}");
            monitorFailed = true;
        }
        finally
        {
            sampling = false;
        }
    }
}
