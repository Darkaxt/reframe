using System.IO;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>read_thread: a thread asked for the recent conversation of another (a supervisor checking on its worker).
/// The last turns of each thread of that name (narrowed by provider) are typed into the asker, through a file when long
/// (TypeIntoAsync), with the transcript's path for more.</summary>
sealed partial class ShellController
{
    async Task ReadThreadAsync(JsonObject hook)
    {
        var askerId = (string?)hook["sessionId"];
        var payload = hook["payload"] as JsonObject;
        string? Text(string key) => payload?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 } t ? t : null;
        if (askerId is null || !State.Sessions.ContainsKey(askerId) || Text("read") is not { } name) return;
        var turns = payload?["turns"] is JsonValue n && n.TryGetValue<int>(out var asked) ? Math.Clamp(asked, 1, ThreadDigest.MaxTurns) : ThreadDigest.DefaultTurns;
        var provider = Text("provider");
        var matches = ThreadsNamed(askerId, name).Where(r => provider is null || ToolProvider(r) == provider).ToList();
        if (matches.Count == 0)
        {
            TaskLog($"read: {askerId}: no thread named {name}");
            await TypeIntoAsync(askerId, $"[Reframe: read_thread: no thread{(provider is null ? "" : $" on {provider}")} is named \"{name}\"]");
            return;
        }
        var parts = new List<string>();
        foreach (var target in matches.Take(3))
        {
            var state = Runs(target.Id) ? StatusOf(target.Id) : "ended";
            var head = $"## \"{name}\" [{target.Id[..6]}] ({ToolProvider(target)}{(target.Launch.Model is { Length: > 0 } m ? " " + m : "")}, {state}, folder {target.Cwd})";
            var file = ToolProvider(target) == "gemini" ? Antigravity.TranscriptFile(target.ClaudeSessionId) : FindTranscript(target.ClaudeSessionId);
            if (file is null || !File.Exists(file))
            {
                parts.Add($"{head}\nIts transcript was not found (it may not have started yet).");
                continue;
            }
            // The end of the transcript (at most TranscriptReader.MaxRead), parsed off the UI thread.
            var digest = await System.Threading.Tasks.Task.Run(() =>
            {
                var model = new TranscriptModel();
                model.Append(TranscriptReader.Read(file, 0).Lines);
                return ThreadDigest.Render(model.Items, turns);
            });
            parts.Add($"{head}\nTranscript: {file}\n\n{(digest.Length > 0 ? digest : "(nothing said yet)")}");
        }
        if (matches.Count > 3) parts.Add($"({matches.Count - 3} more threads have that name; give provider or the id in brackets.)");
        TaskLog($"read: {askerId} read {string.Join(", ", matches.Take(3).Select(t => t.Id))}");
        await TypeIntoAsync(askerId, $"[Reframe: read_thread: the last {turns} turns of \"{name}\", as you asked; for information, not a request]\n\n{string.Join("\n\n", parts)}");
    }
}
