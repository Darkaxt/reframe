using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Restarts the shell into a release the deploy script installed while it ran (<see cref="UpdateWatch"/>). The
/// sessions keep running in their host; the new shell attaches to it. It waits until the user is not in a Reframe window.</summary>
sealed partial class ShellController
{
    DispatcherTimer? updateTimer;
    string? updateAnnounced;

    void StartUpdateWatch()
    {
        if (Fixture is not null || UpdateWatch.ActiveFile(AppContext.BaseDirectory) is null) return;
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += (_, _) => CheckForUpdate();
        updateTimer.Start();
    }

    /// <summary>The release the deploy script installed after this shell started, or null.</summary>
    static InstalledUpdate? PendingUpdate()
    {
        if (UpdateWatch.ActiveFile(AppContext.BaseDirectory) is not { } file) return null;
        try
        {
            return UpdateWatch.Pending(File.ReadAllText(file), AboutDialog.Version, File.Exists);
        }
        catch (IOException)
        {
            return null;
        }
    }

    void CheckForUpdate()
    {
        if (Quitting || ChatBatchRunning || PendingUpdate() is not { } update) return;
        if (!State.Preferences.CheckForUpdates)
        {
            Announce(update, "Restart Reframe to use it.");
            return;
        }
        if (windows.Values.Any(w => w.Attentive))
        {
            Announce(update, "Reframe restarts into it when you are not in a Reframe window.");
            return;
        }
        Restart(update);
    }

    void Announce(InstalledUpdate update, string body)
    {
        if (updateAnnounced == update.Version) return;
        updateAnnounced = update.Version;
        toasts?.Show("", $"Update installed: {update.Version}", body);
    }

    /// <summary>The tray's Reload: the shell starts again as its shortcut starts it. A managed start (REFRAME_APP_LAUNCHER) runs
    /// that launcher once this shell has gone (hash checks, client adoption, the installed release and its pins); without one the
    /// shell restarts into the installed update, or itself. The sessions keep running in their host.</summary>
    void Reload()
    {
        if (Quitting || Environment.ProcessPath is not { } exe) return;
        if (Environment.GetEnvironmentVariable("REFRAME_APP_LAUNCHER") is { Length: > 0 } launcher && Path.IsPathFullyQualified(launcher)
            && launcher.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase) && File.Exists(launcher))
        {
            System.Diagnostics.Trace.WriteLine($"update: reload through {launcher}");
            Restart(new InstalledUpdate(AboutDialog.Version, exe), [App.RelaunchArgument, launcher]);
            return;
        }
        Restart(PendingUpdate() ?? new InstalledUpdate(AboutDialog.Version, exe));
    }

    void Restart(InstalledUpdate update, IReadOnlyList<string>? arguments = null)
    {
        System.Diagnostics.Trace.WriteLine($"update: restarting into {update.Version}");
        var start = new ProcessStartInfo(update.Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(update.Executable)! };
        foreach (var a in arguments ?? Environment.GetCommandLineArgs().Skip(1).ToArray()) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--after-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"update: could not start {update.Version}: {error.Message}");
            return;
        }
        _ = QuitAsync();
    }
}
