using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Stopping the proxy only signals its supervisor's own stop event (as stop-proxy.ps1 does) and waits for the
/// supervisor's mutex; a proxy no supervisor runs is left alone.</summary>
public class ProxyControlTests
{
    [Fact]
    public void Without_a_supervisor_there_is_nothing_to_stop()
    {
        Assert.Equal(ProxyStop.NotRunning, ProxyControl.Stop($"ReframeTest{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_supervisor_that_sees_the_event_and_exits_is_reported_stopped()
    {
        var name = $"ReframeTest{Guid.NewGuid():N}";
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{name}-stop");
        using var held = new ManualResetEventSlim();
        var supervisor = Task.Run(() =>
        {
            using var mutex = new Mutex(true, $@"Local\{name}-mutex");
            held.Set();
            stop.WaitOne();
            mutex.ReleaseMutex();
        });
        held.Wait(TimeSpan.FromSeconds(5));
        Assert.Equal(ProxyStop.Stopped, await Task.Run(() => ProxyControl.Stop(name, TimeSpan.FromSeconds(5))));
        await supervisor;
    }

    [Fact]
    public async Task A_supervisor_that_keeps_running_times_out_without_anything_else_touched()
    {
        var name = $"ReframeTest{Guid.NewGuid():N}";
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{name}-stop");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var supervisor = Task.Run(() =>
        {
            using var mutex = new Mutex(true, $@"Local\{name}-mutex");
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        held.Wait(TimeSpan.FromSeconds(5));
        Assert.Equal(ProxyStop.TimedOut, await Task.Run(() => ProxyControl.Stop(name, TimeSpan.FromMilliseconds(300))));
        release.Set();
        await supervisor;
    }

    [Fact]
    public void The_installation_needs_the_supervisor_and_the_launcher()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reframe-proxy-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "Programs", "claude-code-proxy");
        try
        {
            Directory.CreateDirectory(dir);
            Assert.Null(ProxyControl.Find(root));
            File.WriteAllText(Path.Combine(dir, "start-hidden.vbs"), "");
            Assert.Null(ProxyControl.Find(root));
            File.WriteAllText(Path.Combine(dir, "supervise-proxy.ps1"), "");
            var install = Assert.IsType<ProxyInstall>(ProxyControl.Find(root));
            Assert.Equal(["//B", "//Nologo", Path.Combine(dir, "start-hidden.vbs"), "scratch", "k.md"], ProxyControl.StartArguments(install, "scratch", "k.md"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
