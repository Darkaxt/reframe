using System.IO;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Every 15 s the running threads' transcripts that changed are read for the model they last used (ClaudeRegistry.LastModelIn);
/// a thread whose model differs from its record's (a /model typed in the terminal or the chat chip) has the record updated, so the
/// next start keeps it instead of going back to the model it first started with.</summary>
sealed partial class ShellController
{
    static readonly TimeSpan ModelEvery = TimeSpan.FromSeconds(15);
    DispatcherTimer? modelTimer;
    bool syncingModels;
    readonly Dictionary<string, (string? File, long Stamp)> modelFiles = [];

    void StartModelSync()
    {
        modelTimer = new DispatcherTimer { Interval = ModelEvery };
        modelTimer.Tick += async (_, _) => await SyncModelsAsync();
        modelTimer.Start();
    }

    async Task SyncModelsAsync()
    {
        if (syncingModels || Quitting || Fixture is not null) return;
        var running = State.Sessions.Values.Where(r => Runs(r.Id)).Select(r => (r.Id, r.ClaudeSessionId)).ToList();
        if (running.Count == 0) return;
        syncingModels = true;
        try
        {
            var home = ClaudeRegistry.Home();
            var known = running.ToDictionary(r => r.Id, r => modelFiles.GetValueOrDefault(r.Id));
            var seen = await Task.Run(() =>
            {
                var found = new List<(string Id, string? Model, (string? File, long Stamp) Mark)>();
                foreach (var (id, claudeId) in running)
                {
                    try
                    {
                        var mark = known[id];
                        var file = mark.File ?? ClaudeRegistry.TranscriptFile(home, claudeId);
                        if (file is null) continue;
                        var stamp = File.GetLastWriteTimeUtc(file).Ticks;
                        if (file == mark.File && stamp == mark.Stamp) continue;
                        found.Add((id, ClaudeRegistry.LastModelIn(file), (file, stamp)));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                return found;
            });
            var changed = false;
            foreach (var (id, model, mark) in seen)
            {
                modelFiles[id] = mark;
                if (model is null || !State.Sessions.TryGetValue(id, out var record)) continue;
                if (Takeover.ProviderOfModel(model) is { } provider && provider != (record.Launch.Provider ?? "chatgpt")) continue;
                if (ModelSync.Changed(record.Launch.Model, model) is not { } next) continue;
                System.Diagnostics.Trace.WriteLine($"models: {id} runs {next} (record said {record.Launch.Model ?? "default"})");
                record.Launch.Model = next;
                changed = true;
            }
            if (changed) Saved();
        }
        finally
        {
            syncingModels = false;
        }
    }
}
