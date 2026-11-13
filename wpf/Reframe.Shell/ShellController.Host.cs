using System.Diagnostics;
using System.IO;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Replacing a terminal host too old for this shell: a host from before launch.spawnPlan (Electron's, kept
/// running across the update so its sessions went on) cannot start sessions for the shell. Its restart ends it and
/// starts Reframe again, which starts the current host and resumes the open sessions there (ResumeSessions).</summary>
sealed partial class ShellController
{
    /// <summary>The start error (and service error code) of a session on such a host.</summary>
    public const string HostTooOld = "hostTooOld";

    /// <summary>Set for the Reframe started by a restart: the process it waits for before starting.</summary>
    public const string RestartAfterVariable = "REFRAME_RESTART_AFTER";

    bool restartingHost;

    /// <summary>A session card's "Restart terminal host": asks first (the running sessions stop), then saves the
    /// state, sends the host <c>shutdown</c>, starts Reframe again through the managed launcher (so it is a managed
    /// start, with its launch receipt) or its own executable, and quits; the new one waits for this one to exit.</summary>
    public async Task RestartHostAsync(MainWindow owner)
    {
        if (restartingHost || Quitting || !Writable || Connection is not { IsOpen: true } connection || connection.IsObserver) return;
        var running = HostSessions.Count;
        if (!CloseWindowDialog.Confirm(owner, Strings.T("host.restart.title"),
                Strings.T("host.restart.message", ("version", connection.Welcome.Version), ("count", running)),
                Strings.T("host.restart.detail"), Strings.T("host.restart")))
            return;
        restartingHost = true;
        Trace.WriteLine($"terminal host restart: {connection.Welcome.Version} pid {connection.Welcome.Pid} with {running} sessions");
        if (Store is not null)
        {
            Store.Save(State, Synthetic);
            await Store.FlushAsync(2000);
        }
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => closed.TrySetResult();
        await connection.ShutdownAsync();
        if (connection.IsOpen) await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        try
        {
            Environment.SetEnvironmentVariable(RestartAfterVariable, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var launcher = Environment.GetEnvironmentVariable("REFRAME_APP_LAUNCHER");
            var start = launcher is { Length: > 0 } && File.Exists(launcher)
                ? new ProcessStartInfo("wscript.exe") { ArgumentList = { "//B", "//Nologo", launcher } }
                : new ProcessStartInfo(Environment.ProcessPath!);
            start.UseShellExecute = false;
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Trace.WriteLine($"terminal host restart: Reframe could not be started again: {e.Message}");
            SetStatus(Status + " (the terminal host was ended; start Reframe again to resume the sessions)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RestartAfterVariable, null);
        }
        await QuitAsync();
    }

    /// <summary>A Reframe started by a host restart waits (up to 30 s) for the one that started it to exit, so that it
    /// becomes the profile's shell and starts the new host.</summary>
    public static void WaitForRestartingShell()
    {
        var value = Environment.GetEnvironmentVariable(RestartAfterVariable);
        Environment.SetEnvironmentVariable(RestartAfterVariable, null);
        if (!int.TryParse(value, out var pid) || pid == Environment.ProcessId) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already.
        }
    }
}
