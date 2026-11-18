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

    /// <summary>"end": each session ends in the host and leaves its window (as the tile's × does); the thread stays listed
    /// as ended. "archive" or "delete": then the window does as the list's Archive or Delete does, after the user confirms
    /// there unless the asker opened the thread (then no popup at all); delete is refused for any other thread unless the asker is the manager thread (preferences.managerThread). Several names
    /// are one request: they are closed in turn, the owned ones retired together in one quiet batch, and the asker is told
    /// the outcome of all of them in one message.</summary>
    async Task CloseThreadAsync(JsonObject hook)
    {
        var askerId = (string?)hook["sessionId"];
        string? Text(string key) => hook["payload"] is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
        List<string> names = hook["payload"] is JsonObject body && body["close"] is { } asked
            ? (asked is JsonArray list ? list.Select(n => (string?)n?.GetValue<string>()) : new string?[] { asked.GetValue<string>() })
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (askerId is null || !State.Sessions.ContainsKey(askerId) || names.Count == 0) return;
        var action = Text("action") ?? "end";
        var problems = new List<string>();
        var done = new List<string>();
        var owned = new List<string>();
        var asking = new List<string>();
        foreach (var name in names)
        {
            var matches = ThreadsNamed(askerId, name);
            string? why = null;
            if (!Writable) why = "Reframe is watching read-only";
            else if (matches.Count == 0) why = State.Sessions[askerId] is var own && string.Equals(StateValues.SessionLabel(own.Name, own.ClaudeName, own.Cwd).Trim(), name, StringComparison.OrdinalIgnoreCase)
                ? "a thread cannot close the thread it runs in" : "no thread has that name";
            else if (matches.Count > 1) why = "more than one thread has that name";
            else if (action == "delete" && matches[0].OpenedBy != askerId && State.Preferences.ManagerThread != askerId) why = "you did not open that thread; end or archive it instead";
            if (why is not null)
            {
                problems.Add($"\"{name}\": {why}");
                continue;
            }
            var target = matches[0];
            var window = State.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, target.Id)) ?? State.Windows.FirstOrDefault();
            if (window is null) { problems.Add($"\"{name}\": no window"); continue; }
            if (Runs(target.Id) || LayoutActions.WindowHasSession(window, target.Id)) EndSession(window.Id, target.Id);
            TaskLog($"close: {askerId} {action}: {target.Id}{(target.OpenedBy == askerId ? " (its own)" : "")}");
            done.Add(name);
            if (action is "archive" or "delete") (target.OpenedBy == askerId ? owned : asking).Add(target.Id);
        }
        if (action is "archive" or "delete" && State.Windows.FirstOrDefault() is { } home && windows.GetValueOrDefault(home.Id) is { } shown)
        {
            // One batch for the threads it opened (no popup), one for the rest (the user confirms once).
            if (owned.Count > 0) shown.RetireThreads(owned, action, confirmed: true);
            if (asking.Count > 0) shown.RetireThreads(asking, action, confirmed: false);
        }
        var verb = action switch { "archive" => "archiving", "delete" => "deleting", _ => "ended" };
        var message = done.Count == 0 ? "" : action == "end" ? $"ended {done.Count} thread(s)" : $"ended {done.Count} thread(s); {verb} them now";
        if (problems.Count > 0) message += (message.Length > 0 ? "; " : "") + "could not close " + string.Join(", ", problems);
        TaskLog($"close: {askerId}: {message}");
        if (message.Length > 0 && Runs(askerId)) await TypeIntoAsync(askerId, $"[Reframe: {message}]");
    }
}
