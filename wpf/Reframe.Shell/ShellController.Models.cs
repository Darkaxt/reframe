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
                    var path = known ?? ClaudeRegistry.TranscriptFile(ClaudeRegistry.Home(), claudeId);
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
            if (ModelSync.Changed(record.Launch.Model, model) is not { } next) return;
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
