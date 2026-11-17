namespace Reframe.Shell.Core;

/// <summary>
/// Keeps the user's typing out of a message Reframe is typing into a thread (a wake, a task, a broadcast): while the
/// message is going in, the keys the user types into that thread's terminal wait, and follow it in order afterwards,
/// so the two never mix in the prompt.
/// </summary>
public static class InputGuard
{
    static readonly object Gate = new();
    static readonly Dictionary<string, (int Holds, List<Func<Task>> Waiting)> Sessions = new();

    /// <summary>Starts holding the user's input to the session; dispose to let what waited go through.</summary>
    public static IDisposable Hold(string sessionId)
    {
        lock (Gate)
        {
            Sessions[sessionId] = Sessions.TryGetValue(sessionId, out var held) ? (held.Holds + 1, held.Waiting) : (1, []);
        }
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
