using System.IO;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>When a thread finishes a turn its transcript's tail is read (off the UI thread) for the model it last used
/// (ClaudeRegistry.LastModelIn); a thread whose model differs from its record's (a /model typed in the terminal or the chat
/// chip) has the record updated, so the next start keeps it instead of going back to the model it first started with.
/// Only the finished thread is read, once: a poll of every running transcript made the UI stutter.</summary>
sealed partial class ShellController
{
    readonly Dictionary<string, string> modelFiles = [];
    readonly HashSet<string> syncingModels = [];
    // The model each thread's transcript showed last, and the threads whose model a change of the defaults moved.
    readonly Dictionary<string, string> seenModels = [];
    readonly HashSet<string> movedByDefaults = [];

    /// <summary>Settings' defaults changed: the threads that follow them take the new ones (at their next start).</summary>
    public void FollowDefaults(IReadOnlyDictionary<string, DialogDefaults> before)
    {
        foreach (var (provider, old) in before)
            foreach (var (record, oldModel) in NewSession.FollowDefaults(State, provider, old, NewSession.DefaultsFor(State.Preferences, provider)))
            {
                if (oldModel != record.Launch.Model) movedByDefaults.Add(record.Id);
                System.Diagnostics.Trace.WriteLine($"models: {record.Id} follows the new defaults ({record.Launch.Model ?? "default"}, {record.Launch.Effort ?? "default"}, {record.Launch.PermissionMode ?? "default"})");
            }
    }

    async Task SyncModelAsync(string sessionId)
    {
        if (Quitting || Fixture is not null || !State.Sessions.TryGetValue(sessionId, out var record) || !syncingModels.Add(sessionId)) return;
        try
        {
            var claudeId = record.ClaudeSessionId;
            var known = modelFiles.GetValueOrDefault(claudeId);
            var (file, model) = await Task.Run(() =>
            {
                try
                {
                    var path = known ?? (record.Launch.Provider == "gemini" ? Antigravity.TranscriptFile(claudeId)
                        : ClaudeRegistry.TranscriptFile(ClaudeRegistry.Home(), claudeId));
                    return (path, path is null ? null : ClaudeRegistry.LastModelIn(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return (known, (string?)null);
                }
            });
            if (file is not null) modelFiles[claudeId] = file;
            if (model is null || !State.Sessions.TryGetValue(sessionId, out record)) return;
            if (Takeover.ProviderOfModel(model) is { } provider && provider != (record.Launch.Provider ?? "chatgpt")) return;
            var previous = seenModels.GetValueOrDefault(sessionId);
            seenModels[sessionId] = model;
            if (ModelSync.Changed(record.Launch.Model, model) is not { } next)
            {
                movedByDefaults.Remove(sessionId);
                return;
            }
            if (record.Launch.FollowsDefaults?.Contains("model") == true)
            {
                // A thread that follows the defaults keeps following them unless a /model was typed in it: a thread still
                // running the model it started with (before the defaults changed) is not that.
                var typed = previous is not null ? ModelSync.Changed(previous, model) is not null
                    : !movedByDefaults.Contains(sessionId) && !string.IsNullOrEmpty(record.Launch.Model);
                if (!typed) return;
                movedByDefaults.Remove(sessionId);
                NewSession.StopFollowing(record.Launch, "model");
            }
            System.Diagnostics.Trace.WriteLine($"models: {sessionId} runs {next} (record said {record.Launch.Model ?? "default"})");
            record.Launch.Model = next;
            // The model shows in no list: saving is enough, without redrawing the windows' lists.
            Save();
        }
        finally
        {
            syncingModels.Remove(sessionId);
        }
    }
}
