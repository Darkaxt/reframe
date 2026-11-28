using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The thread map: <c>ProfileDir\threads.json</c>, each thread Reframe has with its name, provider, model,
/// conversation id, client pid (while it runs), status and folders, rewritten when it changes (at each tasks tick).
/// The thread tools' list_threads reads it, and so can any tool (a client's REFRAME_SESSION is the thread's id; an agy
/// client runs in its hook folder). Also the hook files of threads Reframe no longer has are removed.</summary>
sealed partial class ShellController
{
    public static string ThreadMapFile => Path.Combine(App.ProfileDir, "threads.json");
    static string HooksDir => Path.Combine(App.ProfileDir, "hooks");
    string? writtenMap;

    void WriteThreadMap()
    {
        if (!Writable) return;
        var list = new JsonArray();
        foreach (var r in State.Sessions.Values.OrderBy(r => StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd), StringComparer.OrdinalIgnoreCase))
        {
            var runs = Runs(r.Id);
            var agy = Path.Combine(HooksDir, r.Id + "-agy");
            list.Add(new JsonObject
            {
                ["name"] = StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd),
                ["id"] = r.Id,
                ["provider"] = ToolProvider(r),
                ["model"] = r.Launch.Model,
                ["conversationId"] = string.IsNullOrEmpty(r.ClaudeSessionId) ? null : r.ClaudeSessionId,
                ["pid"] = runs && r.Pid is { } pid ? JsonValue.Create((long)pid) : null,
                ["status"] = runs ? StatusOf(r.Id) : "ended",
                ["lifecycle"] = r.Lifecycle,
                ["folder"] = r.Cwd,
                ["hookFolder"] = Directory.Exists(agy) ? agy : null,
                ["openedBy"] = r.OpenedBy,
            });
        }
        var text = new JsonObject { ["updated"] = DateTimeOffset.Now.ToString("O"), ["threads"] = list };
        var body = list.ToJsonString();
        if (body == writtenMap) return;
        try
        {
            var temp = ThreadMapFile + ".tmp";
            File.WriteAllText(temp, text.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ThreadMapFile, overwrite: true);
            writtenMap = body;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TaskLog($"thread map not written: {e.Message}");
        }
    }

    /// <summary>Removes the hook files (<c>&lt;id&gt;.json</c>, an agy thread's <c>&lt;id&gt;-agy</c> folder) of threads
    /// Reframe no longer has, untouched for an hour (a thread being created is not caught).</summary>
    void PruneHooks(IReadOnlySet<string> known)
    {
        if (!Directory.Exists(HooksDir)) return;
        var before = DateTime.UtcNow.AddHours(-1);
        var removed = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(HooksDir))
            {
                var leaf = Path.GetFileName(entry);
                var id = leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? leaf[..^5] : leaf.EndsWith("-agy", StringComparison.Ordinal) ? leaf[..^4] : null;
                if (id is null || !Guid.TryParse(id, out _) || known.Contains(id)) continue;
                try
                {
                    if (File.Exists(entry))
                    {
                        if (File.GetLastWriteTimeUtc(entry) > before) continue;
                        File.Delete(entry);
                    }
                    else
                    {
                        if (Directory.GetLastWriteTimeUtc(entry) > before) continue;
                        Directory.Delete(entry, recursive: true);
                    }
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (removed > 0) System.Diagnostics.Trace.WriteLine($"hooks: removed {removed} of threads no longer in Reframe");
    }
}
