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

    void CheckForUpdate()
    {
        if (Quitting || ChatBatchRunning || UpdateWatch.ActiveFile(AppContext.BaseDirectory) is not { } file) return;
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (IOException)
        {
            return;
        }
        if (UpdateWatch.Pending(json, AboutDialog.Version, File.Exists) is not { } update) return;
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

    void Restart(InstalledUpdate update)
    {
        System.Diagnostics.Trace.WriteLine($"update: restarting into {update.Version}");
        var start = new ProcessStartInfo(update.Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(update.Executable)! };
        foreach (var a in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(a);
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
