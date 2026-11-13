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
    public static List<RunningClaude> Running(string home)
    {
        var result = new List<RunningClaude>();
        var dir = Path.Combine(home, "sessions");
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var entry = JsonNode.Parse(File.ReadAllText(file));
                if (entry is null || (int?)entry["pid"] is not { } pid || (string?)entry["sessionId"] is not { } sessionId) continue;
                if (!Alive(pid)) continue;
                result.Add(new RunningClaude(pid, sessionId, (string?)entry["cwd"], (string?)entry["name"], (double?)entry["startedAt"]));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
            {
                // Being written, or not an entry: skipped this time.
            }
        }
        return result;
    }

    static bool Alive(int pid)
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
