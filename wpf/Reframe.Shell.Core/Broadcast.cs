namespace Reframe.Shell.Core;

/// <summary>
/// The top bar's messages to running threads (Pause, Continue, Clean up, and one of the user's own): which threads
/// each goes to and what it says. A thread waiting on a question (a permission prompt would take the text as its
/// answer) or still starting is never typed into. A thread that is busy gets Ctrl+B first, so that a long command it
/// runs goes to the background and the message is read at the next turn instead of after the command.
/// </summary>
public static class Broadcast
{
    public const string Pause = "pause", Continue = "continue", Cleanup = "cleanup", Custom = "custom";

    /// <summary>Ctrl+B: Claude Code moves the command it runs to the background.</summary>
    public const string Background = "\u0002";

    public const string PauseText =
        "[Reframe] Please reach a safe pause point now: finish or stop the current step, save your work (commit it or " +
        "note where you are), and stop any command or task that would take more than 5 minutes; a command that was just " +
        "moved to the background counts too. Then say where you stopped and wait for a message to continue.";

    public const string ContinueText =
        "[Reframe] You can continue now: pick up where you paused, re-check anything that may have changed while you " +
        "waited, and go on with your plan.";

    public const string CleanupText =
        "[Reframe] Priority change before you continue: look for old or superseded files your project owns (temporary " +
        "outputs, stale builds, replaced copies, leftovers of earlier attempts) and delete them, following this machine's " +
        "cleanup rules. If a deletion needs elevated rights, ask me with an input request instead of skipping it. When " +
        "done, list what you removed, then go back to the plan or issue you were working on.";

    public static string? TextOf(string kind) => kind switch
    {
        Pause => PauseText,
        Continue => ContinueText,
        Cleanup => CleanupText,
        _ => null,
    };

    /// <summary>A thread to type into, and whether it gets Ctrl+B first.</summary>
    public readonly record struct Target(string SessionId, bool Interrupt);

    /// <summary>The threads among <paramref name="sessions"/> the message goes to. Pause: those at work (busy, or with
    /// a command in the background). Continue: those paused from here that are idle now. Clean up and the user's own:
    /// every thread that runs and can be typed into.</summary>
    public static List<Target> Targets(string kind, IEnumerable<string> sessions, Func<string, string> statusOf, IReadOnlySet<string> paused)
    {
        var targets = new List<Target>();
        foreach (var id in sessions.Distinct())
        {
            var status = statusOf(id);
            var fits = kind switch
            {
                Pause => status is "busy" or "background",
                Continue => paused.Contains(id) && status == "idle",
                _ => status is "idle" or "busy" or "background",
            };
            if (fits) targets.Add(new Target(id, status == "busy"));
        }
        return targets;
    }

    /// <summary>What the confirmation asks, by message and number of threads.</summary>
    public static string Question(string kind, int count)
    {
        var threads = count == 1 ? "1 thread" : $"{count} threads";
        return kind switch
        {
            Pause => $"Ask {threads} to reach a safe pause point?",
            Continue => $"Tell {threads} paused from here to continue?",
            Cleanup => $"Ask {threads} to clean up old files first?",
            _ => $"Send the message to {threads}?",
        };
    }

    /// <summary>Why nothing is sent, by message.</summary>
    public static string NoTarget(string kind) => kind switch
    {
        Pause => "No running thread is at work right now.",
        Continue => "No thread paused from here is idle now.",
        _ => "No running thread can take a message right now (threads waiting on a question are left alone).",
    };
}
