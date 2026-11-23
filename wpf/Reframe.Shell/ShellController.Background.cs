using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>What "background" threads run (their claude ended its turn, its commands still run): every 20 s while
/// any thread is background, and 2 s after one becomes so, the process table names the work off the UI thread.
/// Nothing runs while no thread is background.</summary>
sealed partial class ShellController
{
    static readonly TimeSpan BackgroundEvery = TimeSpan.FromSeconds(20);
    static readonly TimeSpan BackgroundSoon = TimeSpan.FromSeconds(2);
    readonly Dictionary<string, (string Label, DateTime Since)> backgroundWork = [];
    DispatcherTimer? backgroundTimer;
    bool readingBackground;

    /// <summary>The tile's and row's text for a background thread ("Background: python x.py · 14 min") and its full
    /// label, or null.</summary>
    public (string Text, string Label)? BackgroundTextOf(string sessionId) =>
        StatusOf(sessionId) == "background" && backgroundWork.TryGetValue(sessionId, out var work)
            ? (BackgroundWork.Text(work.Label, work.Since, DateTime.UtcNow), work.Label) : null;

    /// <summary>A session's status changed: a new background thread is read soon; one that left it loses its label.</summary>
    void BackgroundStatusChanged(string sessionId)
    {
        if (StatusOf(sessionId) == "background")
        {
            if (!backgroundWork.ContainsKey(sessionId)) ScheduleBackground(BackgroundSoon);
        }
        else backgroundWork.Remove(sessionId);
    }

    void ScheduleBackground(TimeSpan after)
    {
        backgroundTimer ??= new DispatcherTimer(DispatcherPriority.Background, Ui);
        backgroundTimer.Stop();
        backgroundTimer.Tick -= BackgroundTick;
        backgroundTimer.Tick += BackgroundTick;
        backgroundTimer.Interval = after;
        backgroundTimer.Start();
    }

    async void BackgroundTick(object? sender, EventArgs e)
    {
        backgroundTimer?.Stop();
        if (readingBackground) return;
        var pids = new Dictionary<string, int>();
        foreach (var (id, record) in State.Sessions)
            if (record.Pid is double pid && pid > 0 && StatusOf(id) == "background") pids[id] = (int)pid;
        if (pids.Count == 0)
        {
            backgroundWork.Clear();
            return;
        }
        readingBackground = true;
        Dictionary<int, (string Label, DateTime Since)> read;
        try { read = await Task.Run(() => BackgroundWatch.Describe(pids.Values.Distinct().ToList())); }
        catch { read = []; }
        finally { readingBackground = false; }
        await Ui.InvokeAsync(() =>
        {
            var shown = new List<string>();
            foreach (var (id, pid) in pids)
            {
                var had = backgroundWork.ContainsKey(id);
                if (StatusOf(id) == "background" && read.TryGetValue(pid, out var work)) backgroundWork[id] = work;
                else backgroundWork.Remove(id);
                // The minutes tick too: every read refreshes what shows a label.
                if (had ||backgroundWork.ContainsKey(id)) shown.Add(id);
            }
            if (shown.Count > 0) foreach (var window in windows.Values) window.BackgroundWorkChanged(shown);
            foreach (var id in backgroundWork.Keys.Where(id => !pids.ContainsKey(id)).ToList()) backgroundWork.Remove(id);
            if (State.Sessions.Keys.Any(id => StatusOf(id) == "background")) ScheduleBackground(BackgroundEvery);
        }, DispatcherPriority.Background);
    }
}
