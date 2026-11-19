using System.IO;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Fork a thread at a point of its conversation (the Chat view's "Fork from here"): the transcript up to that point
/// becomes the transcript of a new thread, which is resumed next to the old one. A thread stuck on a rejection can be
/// continued from before it, with another approach. The old transcript is only read.</summary>
sealed partial class ShellController
{
    public async Task ForkSessionAsync(string windowId, string sessionId, string uuid, bool before)
    {
        if (!Writable || Fixture is not null || !State.Sessions.TryGetValue(sessionId, out var record)) return;
        var source = FindTranscript(record.ClaudeSessionId);
        if (source is null || !windows.TryGetValue(windowId, out var window))
        {
            SetStatus(Status + " (fork: the conversation was not found)");
            return;
        }
        var newId = Guid.NewGuid().ToString();
        List<string>? cut;
        try
        {
            var lines = await Task.Run(() =>
            {
                using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            });
            cut = TranscriptFork.Cut(lines, uuid, before, record.ClaudeSessionId, newId);
            if (cut is { Count: > 0 })
                await File.WriteAllLinesAsync(Path.Combine(Path.GetDirectoryName(source)!, newId + ".jsonl"), cut);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"fork failed: {e.Message}");
            return;
        }
        if (cut is not { Count: > 0 })
        {
            SetStatus(Status + " (fork: nothing to continue from there)");
            return;
        }
        var copy = new SessionRecord
        {
            Id = Guid.NewGuid().ToString(),
            ClaudeSessionId = newId,
            Cwd = record.Cwd,
            Name = (record.Name ?? "thread") + " (fork)",
            Used = true,
            CreatedAt = Now(),
            LastActiveAt = Now(),
            Launch = new LaunchOptions
            {
                Provider = record.Launch.Provider, Model = record.Launch.Model, Effort = record.Launch.Effort,
                PermissionMode = record.Launch.PermissionMode, AddDirs = record.Launch.AddDirs,
            },
        };
        State.Sessions[copy.Id] = copy;
        window.PlaceSession(copy.Id, null);
        Saved();
        Log($"fork: {sessionId} at {uuid} ({(before ? "before" : "after")}) -> {copy.Id}");
        await StartOrTakeOverAsync(copy.Id, "resume", windowId);
    }
}
