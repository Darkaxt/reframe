namespace Reframe.Shell.Core;

/// <summary>
/// Keeps the user's typing and Reframe's own messages to a thread (a wake, a task, a monitor's note, a report) apart.
/// <list type="bullet">
/// <item>The user owns a thread's prompt while they write in it: from their first key until they send it (Enter), clear
/// it (Ctrl+C, Ctrl+U, or deleting what they wrote), and for a moment after any key. Reframe's messages to that thread
/// wait in its queue meanwhile (<see cref="TakeAsync"/>), one after another, instead of being typed into the half-written
/// prompt and sent with it.</item>
/// <item>While a message is going in, the keys the user types into that thread wait (<see cref="Defer"/>), and follow
/// it in order afterwards, so the two never mix.</item>
/// </list>
/// What the user writes is followed from the keys only (the prompt is not read): a key whose effect is not known (the
/// history's up and down arrows, Ctrl+W) keeps the prompt owned until it is sent or cleared.
/// </summary>
public static class InputGuard
{
    /// <summary>How long after the user's last key the prompt stays theirs, even when what they wrote was deleted.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    static readonly object Gate = new();
    static readonly Dictionary<string, (int Holds, List<Func<Task>> Waiting)> Sessions = new();
    static readonly Dictionary<string, Draft> Drafts = new();
    static readonly Dictionary<string, SemaphoreSlim> Turns = new();

    sealed class Draft
    {
        /// <summary>About how many characters the user's prompt holds; Unknown when a key's effect is not known.</summary>
        public int Chars;
        public bool Unknown;
        public DateTimeOffset LastKey;
        /// <summary>Completed (and replaced) when the prompt may have become free.</summary>
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The user's input to a session, as their terminal sends it: what it does to their prompt is noted.</summary>
    public static void NoteUser(string sessionId, string data) => NoteUser(sessionId, data, DateTimeOffset.Now);

    public static void NoteUser(string sessionId, string data, DateTimeOffset now)
    {
        TaskCompletionSource? changed = null;
        lock (Gate)
        {
            if (!Drafts.TryGetValue(sessionId, out var draft)) Drafts[sessionId] = draft = new Draft();
            var keyed = false;
            for (var i = 0; i < data.Length; i++)
            {
                var c = data[i];
                if (c == '\u001b' && i + 1 < data.Length)
                {
                    var next = data[i + 1];
                    if (next == '[')
                    {
                        // CSI: parameters, then a final byte. A paste's text counts as written.
                        var end = i + 2;
                        while (end < data.Length && data[end] is < '@' or > '~') end++;
                        var sequence = data[(i + 2)..Math.Min(end + 1, data.Length)];
                        if (sequence == "200~")
                        {
                            var close = data.IndexOf("\u001b[201~", end + 1, StringComparison.Ordinal);
                            var text = close < 0 ? data[(end + 1)..] : data[(end + 1)..close];
                            draft.Chars += text.Length;
                            keyed = true;
                            i = close < 0 ? data.Length : close + 5;
                            continue;
                        }
                        // Mouse and focus reports are not the user writing.
                        if (sequence.StartsWith('<') || sequence.StartsWith('M') || sequence is "I" or "O")
                        {
                            i = end;
                            continue;
                        }
                        keyed = true;
                        if (sequence is "A" or "B") draft.Unknown = true;       // the history: an old prompt in it
                        else if (sequence == "3~") draft.Chars = Math.Max(0, draft.Chars - 1);
                        i = end;
                        continue;
                    }
                    keyed = true;
                    if (next == 'O')
                    {
                        if (i + 2 < data.Length && data[i + 2] is 'A' or 'B') draft.Unknown = true;
                        i += 2;
                        continue;
                    }
                    // Alt+key; Alt+Enter is a new line in the prompt.
                    if (next == '\r') draft.Chars++;
                    i++;
                    continue;
                }
                keyed = true;
                switch (c)
                {
                    case '\r':
                        // Sent: the prompt is empty again.
                        draft.Chars = 0;
                        draft.Unknown = false;
                        break;
                    case '\u0003' or '\u0015':
                        // Ctrl+C clears Claude Code's prompt, Ctrl+U what is before the cursor.
                        draft.Chars = 0;
                        draft.Unknown = false;
                        break;
                    case '\u007f' or '\b':
                        draft.Chars = Math.Max(0, draft.Chars - 1);
                        break;
                    case '\u0017':
                        draft.Unknown = true;
                        break;
                    case '\n':
                        draft.Chars++;
                        break;
                    default:
                        if (c >= ' ') draft.Chars++;
                        break;
                }
            }
            if (!keyed) return;
            draft.LastKey = now;
            changed = draft.Changed;
            draft.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>The session started again (a new process): its prompt is empty.</summary>
    public static void Forget(string sessionId)
    {
        TaskCompletionSource? changed = null;
        lock (Gate)
        {
            if (Drafts.Remove(sessionId, out var draft)) changed = draft.Changed;
        }
        changed?.TrySetResult();
    }

    /// <summary>Whether the user owns the session's prompt (writing in it, or what they wrote is not sent).</summary>
    public static bool UserOwns(string sessionId) => UserOwns(sessionId, DateTimeOffset.Now);

    public static bool UserOwns(string sessionId, DateTimeOffset now)
    {
        lock (Gate) return Wait(sessionId, now) is not null;
    }

    /// <summary>Null when the prompt is free; else how long until it may be (infinite until the user sends or clears it).</summary>
    static TimeSpan? Wait(string sessionId, DateTimeOffset now)
    {
        if (!Drafts.TryGetValue(sessionId, out var draft)) return null;
        if (draft.Chars > 0 || draft.Unknown) return Timeout.InfiniteTimeSpan;
        var left = draft.LastKey + Quiet - now;
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>The session's queue: waits for the messages before this one, then until the user does not own the
    /// prompt, and holds the user's input while the caller types. Dispose when the message is in.</summary>
    public static async Task<IDisposable> TakeAsync(string sessionId, CancellationToken cancel = default)
    {
        SemaphoreSlim turn;
        lock (Gate)
        {
            if (Turns.TryGetValue(sessionId, out var queue)) turn = queue;
            else Turns[sessionId] = turn = new SemaphoreSlim(1, 1);
        }
        await turn.WaitAsync(cancel);
        try
        {
            while (true)
            {
                Task changed;
                TimeSpan wait;
                lock (Gate)
                {
                    if (Wait(sessionId, DateTimeOffset.Now) is not { } left)
                    {
                        var hold = HoldLocked(sessionId);
                        return new Taken(hold, turn);
                    }
                    wait = left;
                    changed = Drafts[sessionId].Changed.Task;
                }
                await Task.WhenAny(changed, Task.Delay(wait, cancel));
                cancel.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            turn.Release();
            throw;
        }
    }

    sealed class Taken(IDisposable hold, SemaphoreSlim turn) : IDisposable
    {
        int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            hold.Dispose();
            turn.Release();
        }
    }

    /// <summary>Starts holding the user's input to the session; dispose to let what waited go through.</summary>
    public static IDisposable Hold(string sessionId)
    {
        lock (Gate) return HoldLocked(sessionId);
    }

    static IDisposable HoldLocked(string sessionId)
    {
        Sessions[sessionId] = Sessions.TryGetValue(sessionId, out var held) ? (held.Holds + 1, held.Waiting) : (1, []);
        return new Release(sessionId);
    }

    /// <summary>True when the input was kept back (it is sent once the hold ends); false when the caller sends it now.</summary>
    public static bool Defer(string sessionId, Func<Task> send)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(sessionId, out var held)) return false;
            held.Waiting.Add(send);
            return true;
        }
    }

    sealed class Release(string sessionId) : IDisposable
    {
        int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            List<Func<Task>>? flush = null;
            lock (Gate)
            {
                var (holds, waiting) = Sessions[sessionId];
                if (holds > 1) Sessions[sessionId] = (holds - 1, waiting);
                else
                {
                    Sessions.Remove(sessionId);
                    flush = waiting;
                }
            }
            if (flush is { Count: > 0 }) _ = Task.Run(async () => { foreach (var send in flush) await send(); });
        }
    }
}
