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
