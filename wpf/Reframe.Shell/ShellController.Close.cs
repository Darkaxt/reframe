using System.IO;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The threads' <c>close_thread</c> tool: a thread shuts down another by name, so the threads it opened do not
/// pile up.</summary>
sealed partial class ShellController
{
    /// <summary>The threads other than the asker's that carry the name: a thread's own name (given here or by Claude)
    /// wins over the folder name a nameless thread is shown by.</summary>
    List<SessionRecord> ThreadsNamed(string askerId, string name)
    {
        var others = State.Sessions.Values.Where(r => r.Id != askerId).ToList();
        var matches = others.Where(r => string.Equals((string.IsNullOrWhiteSpace(r.Name) ? r.ClaudeName : r.Name)?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = others.Where(r => string.Equals(StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd).Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches;
    }

    /// <summary>"end": the session ends in the host and leaves its window (as the tile's × does); the thread stays listed
    /// as ended. "archive": then the window archives its conversation as the list's Archive does, after the user confirms
    /// there. An unknown or ambiguous name, or the asker itself, is told to the asker.</summary>
    async Task CloseThreadAsync(JsonObject hook)
    {
        var askerId = (string?)hook["sessionId"];
        string? Text(string key) => hook["payload"] is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
        if (askerId is null || !State.Sessions.ContainsKey(askerId) || Text("close") is not { } name) return;
        var archive = Text("action") == "archive";
        var matches = ThreadsNamed(askerId, name);
        string? why = null;
        if (!Writable) why = "Reframe is watching read-only";
        else if (matches.Count == 0) why = State.Sessions[askerId] is var own && string.Equals(StateValues.SessionLabel(own.Name, own.ClaudeName, own.Cwd).Trim(), name, StringComparison.OrdinalIgnoreCase)
            ? "a thread cannot close the thread it runs in" : $"no thread is named \"{name}\"";
        else if (matches.Count > 1) why = $"more than one thread is named \"{name}\"";
        if (why is not null)
        {
            TaskLog($"close: {askerId}: {why}");
            if (Runs(askerId)) await TypeIntoAsync(askerId, $"[Reframe: could not close: {why}]");
            return;
        }
        var target = matches[0];
        var window = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, target.Id)) ?? State.Windows.FirstOrDefault();
        if (window is null) return;
        var wasRunning = Runs(target.Id);
        if (wasRunning || LayoutActions.WindowHasSession(window, target.Id)) EndSession(window.Id, target.Id);
        TaskLog($"close: {askerId} ended {target.Id}{(archive ? ", archiving" : "")}");
        if (archive && windows.GetValueOrDefault(window.Id) is { } shown) shown.ArchiveThread(target.Id);
        else if (Runs(askerId)) await TypeIntoAsync(askerId, $"[Reframe: ended the thread \"{name}\"]");
    }
}
