using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>Claude Code's files on Windows: its home and its session registry (<c>&lt;home&gt;/sessions/&lt;pid&gt;.json</c>).
/// Read only.</summary>
public static class ClaudeRegistry
{
    /// <summary>Claude Code's home as reframe finds it: REFRAME_CLAUDE_HOME (tests), CLAUDE_CONFIG_DIR, else ~/.claude.</summary>
    public static string Home() =>
        Environment.GetEnvironmentVariable("REFRAME_CLAUDE_HOME") is { Length: > 0 } test ? test
        : Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config ? config
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>The registry's entries whose process still runs.</summary>
    public static List<RunningClaude> Running(string home) => Entries(home).Where(e => Alive(e.Pid)).ToList();

    /// <summary>Every entry of the registry, whether its process runs or not (one in WSL is a Linux process, asked
    /// about in its distribution).</summary>
    public static List<RunningClaude> Entries(string home)
    {
        var result = new List<RunningClaude>();
        var dir = Path.Combine(home, "sessions");
        try
        {
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                if (Read(file) is { } entry) result.Add(entry);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A share that went away while it was read: nothing found this time.
        }
        return result;
    }

    /// <summary>The registry's entry of process <paramref name="pid"/> as it is now (its name may have changed).</summary>
    public static RunningClaude? Entry(string home, int pid) => Read(Path.Combine(home, "sessions", $"{pid}.json"));

    static RunningClaude? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var entry = JsonNode.Parse(File.ReadAllText(file));
            if (entry is null || (int?)entry["pid"] is not { } pid || (string?)entry["sessionId"] is not { } sessionId) return null;
            return new RunningClaude(pid, sessionId, (string?)entry["cwd"], (string?)entry["name"], (double?)entry["startedAt"], (string?)entry["nameSource"]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            // Being written, or not an entry: skipped this time.
            return null;
        }
    }

    /// <summary>When the session's transcript (<c>&lt;home&gt;/projects/&lt;folder&gt;/&lt;id&gt;.jsonl</c>) was last written, in
    /// ms since the epoch; null without one (transcriptModifiedAsync).</summary>
    public static double? TranscriptModifiedAt(string home, string claudeSessionId)
    {
        var projects = Path.Combine(home, "projects");
        try
        {
            if (!Directory.Exists(projects)) return null;
            foreach (var folder in Directory.EnumerateDirectories(projects))
            {
                var file = Path.Combine(folder, claudeSessionId + ".jsonl");
                if (File.Exists(file)) return new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>The model of the newest assistant message in the session's transcript (read from its last 512 KB);
    /// null without a transcript or one.</summary>
    public static string? LastModel(string home, string claudeSessionId)
    {
        try
        {
            var projects = Path.Combine(home, "projects");
            if (!Directory.Exists(projects)) return null;
            foreach (var folder in Directory.EnumerateDirectories(projects))
            {
                var file = Path.Combine(folder, claudeSessionId + ".jsonl");
                if (File.Exists(file)) return LastModelIn(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>The transcript file of a session under <paramref name="home"/>\projects, or null.</summary>
    public static string? TranscriptFile(string home, string claudeSessionId)
    {
        try
        {
            var projects = Path.Combine(home, "projects");
            if (!Directory.Exists(projects)) return null;
            foreach (var folder in Directory.EnumerateDirectories(projects))
            {
                var file = Path.Combine(folder, claudeSessionId + ".jsonl");
                if (File.Exists(file)) return file;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>The model of the newest assistant message in a transcript file, a subagent's messages left out: its last
    /// 64 KB are read first, its last 512 KB only when those have none.</summary>
    public static string? LastModelIn(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int small = 64 * 1024, large = 512 * 1024;
        return ModelInTail(stream, small) ?? (stream.Length > small ? ModelInTail(stream, large) : null);
    }

    static string? ModelInTail(FileStream stream, int tail)
    {
        stream.Seek(stream.Length > tail ? -tail : 0, stream.Length > tail ? SeekOrigin.End : SeekOrigin.Begin);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var lines = reader.ReadToEnd().Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"assistant\"", StringComparison.Ordinal) || !line.Contains("\"model\"", StringComparison.Ordinal)) continue;
            try
            {
                var entry = JsonNode.Parse(line);
                if ((string?)entry?["type"] == "assistant" && entry["isSidechain"]?.GetValue<bool>() != true
                    && (string?)entry["message"]?["model"] is { Length: > 0 } model && !model.StartsWith('<'))
                    return model;
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                // The first line of the tail may be cut off.
            }
        }
        return null;
    }

    public static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
