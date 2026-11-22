using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The top bar's messages to running threads (Broadcast): typed into each thread as NotifyAsync types a
/// warning's note, with Ctrl+B first for a busy one. The threads paused from here are remembered while the shell runs,
/// for Continue to go to them only.</summary>
sealed partial class ShellController
{
    readonly HashSet<string> pausedHere = [];

    /// <summary>The threads the message would go to: those of <paramref name="scope"/> (null: every session).</summary>
    public List<Broadcast.Target> BroadcastTargets(string kind, IEnumerable<string>? scope)
    {
        if (Connection is not { IsOpen: true, IsObserver: false }) return [];
        pausedHere.RemoveWhere(id => !Runs(id));
        var sessions = (scope ?? State.Sessions.Keys).Where(Runs).ToList();
        return Broadcast.Targets(kind, sessions, StatusOf, pausedHere);
    }

    /// <summary>Types <paramref name="text"/> into each target; the number of threads it reached.</summary>
    public async Task<int> BroadcastAsync(string kind, string text, IReadOnlyList<Broadcast.Target> targets)
    {
        if (Connection is not { IsOpen: true, IsObserver: false }) return 0;
        var sent = await Task.WhenAll(targets.Select(target => TypeIntoAsync(target.SessionId, text, target.Interrupt, queued: false)));
        for (var i = 0; i < targets.Count; i++)
        {
            if (!sent[i]) continue;
            if (kind == Broadcast.Pause) pausedHere.Add(targets[i].SessionId);
            else if (kind == Broadcast.Continue) pausedHere.Remove(targets[i].SessionId);
        }
        var reached = sent.Count(s => s);
        System.Diagnostics.Trace.WriteLine($"broadcast: {kind} sent to {reached} of {targets.Count} threads");
        return reached;
    }
}
