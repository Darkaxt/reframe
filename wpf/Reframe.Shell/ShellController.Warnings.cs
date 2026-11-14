using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The top bar's warnings: what the current host's <c>monitor</c> service raised (runaway processes,
/// whole-disk searches, processes left after a thread ended), less those dismissed here. Each can be told to its
/// thread (a note typed for its agent) or its process ended. Those resolved (gone, killed or dismissed) are kept
/// as history while the shell runs, newest first.</summary>
sealed partial class ShellController
{
    readonly List<MonitorWarning> warnings = [];
    readonly HashSet<string> dismissedWarnings = [];
    readonly List<(MonitorWarning Warning, string How, long At)> resolvedWarnings = [];
    const int ResolvedKept = 50;

    public IReadOnlyList<MonitorWarning> Warnings => warnings;
    public IReadOnlyList<(MonitorWarning Warning, string How, long At)> ResolvedWarnings => resolvedWarnings;
    public event Action? WarningsChanged;

    async Task StartWarningsAsync()
    {
        if (Services is not { OffersWarnings: true } services) return;
        services.WarningRaised += warning => Ui.InvokeAsync(() => AddWarning(warning));
        services.WarningCleared += id => Ui.InvokeAsync(() => RemoveWarning(id));
        try
        {
            foreach (var warning in await services.MonitorWarningsAsync()) AddWarning(warning);
        }
        catch (HostServiceException e)
        {
            System.Diagnostics.Trace.WriteLine($"monitor warnings: {e.Message}");
        }
    }

    void AddWarning(MonitorWarning warning)
    {
        if (dismissedWarnings.Contains(warning.Id) || warnings.Any(w => w.Id == warning.Id)) return;
        warnings.Add(warning);
        WarningsChanged?.Invoke();
    }

    void RemoveWarning(string id, string how = "resolved")
    {
        // A dismissed one is in the history already.
        if (dismissedWarnings.Remove(id)) return;
        if (warnings.FirstOrDefault(w => w.Id == id) is not { } warning) return;
        warnings.Remove(warning);
        Resolve(warning, how);
    }

    void Resolve(MonitorWarning warning, string how)
    {
        resolvedWarnings.Insert(0, (warning, how, DateTimeOffset.Now.ToUnixTimeMilliseconds()));
        if (resolvedWarnings.Count > ResolvedKept) resolvedWarnings.RemoveAt(ResolvedKept);
        WarningsChanged?.Invoke();
    }

    public void DismissWarning(MonitorWarning warning)
    {
        dismissedWarnings.Add(warning.Id);
        if (warnings.Remove(warning)) Resolve(warning, "dismissed");
    }

    public void ClearWarningHistory()
    {
        resolvedWarnings.Clear();
        WarningsChanged?.Invoke();
    }

    /// <summary>Whether the thread can be told: it runs and does not wait on a question (a permission prompt would
    /// take the note as its answer). A busy Claude Code queues what is typed.</summary>
    public bool CanNotify(MonitorWarning warning) => Runs(warning.SessionId) && StatusOf(warning.SessionId) is not ("waiting" or "starting");

    /// <summary>Types the warning's note into its thread, then Enter (a moment later, so that it is not taken as part
    /// of a paste), through a view of its own.</summary>
    public async Task NotifyAsync(MonitorWarning warning)
    {
        if (Connection is not { IsOpen: true, IsObserver: false } connection || !CanNotify(warning)) return;
        var view = connection.CreateView(warning.SessionId);
        try
        {
            await view.OpenAsync();
            await view.InputAsync("\u001b[200~" + warning.Note() + "\u001b[201~");
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await view.InputAsync("\r");
        }
        finally
        {
            view.Close();
        }
    }

    /// <summary>Ends what the warning is about (the host ends only a warned process and what it started, or an
    /// ended thread's leftovers). Null when done, or what went wrong.</summary>
    public async Task<string?> KillAsync(MonitorWarning warning)
    {
        if (Services is not { } services || Connection is not { IsOpen: true, IsObserver: false }) return "No PTY host attached.";
        try
        {
            var killed = await services.MonitorKillAsync(warning.Id);
            RemoveWarning(warning.Id, killed == 1 ? "killed 1 process" : $"killed {killed} processes");
            return null;
        }
        catch (HostServiceException e)
        {
            return e.Message;
        }
    }
}
