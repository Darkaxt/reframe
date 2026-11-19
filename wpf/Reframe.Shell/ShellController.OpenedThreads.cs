using System.IO;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>A thread another session opened with open_thread must not fail unseen: when its first request ends in an API error
/// (an unknown model, a refused key) the opener is told what happened and the user gets a notification. Watched for three
/// minutes after the thread opens.</summary>
sealed partial class ShellController
{
    async Task WatchOpenedThreadAsync(string openerId, string name)
    {
        var record = State.Sessions.Values.FirstOrDefault(r => r.OpenedBy == openerId && r.Name == name);
        if (record?.ClaudeSessionId is not { Length: > 0 } transcriptId) return;
        string? file = null;
        var until = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < until && !Quitting && State.Sessions.ContainsKey(record.Id))
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
            file ??= FindTranscript(transcriptId);
            if (file is null) continue;
            string[] lines;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (IOException)
            {
                continue;
            }
            var (state, error) = OpenedThreadCheck.Check(lines);
            if (state == OpenedThreadState.Working) return;
            if (state != OpenedThreadState.Failed || error is null) continue;
            System.Diagnostics.Trace.WriteLine($"threads: \"{name}\" failed at its first request; telling {openerId}");
            toasts?.Show(record.Id, $"Thread failed: {name}", error.Length > 200 ? error[..200] + "…" : error);
            if (State.Sessions.ContainsKey(openerId)) await TypeIntoAsync(openerId, OpenedThreadCheck.Report(name, record.Launch.Model, error));
            return;
        }
    }

    static string? FindTranscript(string claudeSessionId)
    {
        try
        {
            var projects = Path.Combine(ClaudeRegistry.Home(), "projects");
            if (!Directory.Exists(projects)) return null;
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(dir, claudeSessionId + ".jsonl");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
