using System.Globalization;

namespace Reframe.Shell.Core;

/// <summary>One kind of file Claude Code keeps for a thread, where it is and how much it takes.</summary>
public sealed record ThreadFileGroup(string Name, string Path, long Bytes, int Files);

/// <summary>What a thread takes on disk in Claude Code's home: its transcript, its session folder (subagents, tool
/// results), file history, session environment, tasks, todos and debug log. Read only; the same places the history
/// helper archives or deletes (<see cref="ChatHistory.HasClaudeHistory"/>).</summary>
public static class ThreadFiles
{
    public static List<ThreadFileGroup> Measure(string home, string claudeSessionId)
    {
        var groups = new List<ThreadFileGroup>();
        if (string.IsNullOrEmpty(claudeSessionId) || claudeSessionId.IndexOfAny(['/', '\\', '.']) >= 0) return groups;
        void Add(string name, string path)
        {
            var (bytes, files) = Size(path);
            if (files > 0) groups.Add(new ThreadFileGroup(name, path, bytes, files));
        }
        foreach (var project in Directories(Path.Combine(home, "projects")))
        {
            Add("Transcript", Path.Combine(project, claudeSessionId + ".jsonl"));
            Add("Subagents and tool results", Path.Combine(project, claudeSessionId));
        }
        Add("File history", Path.Combine(home, "file-history", claudeSessionId));
        Add("Session environment", Path.Combine(home, "session-env", claudeSessionId));
        Add("Tasks", Path.Combine(home, "tasks", claudeSessionId));
        Add("Debug log", Path.Combine(home, "debug", claudeSessionId + ".txt"));
        var todos = Path.Combine(home, "todos");
        var todoFiles = Safe(() => Directory.GetFiles(todos, claudeSessionId + "-*.json"));
        if (todoFiles.Length > 0)
            groups.Add(new ThreadFileGroup("Todos", todos, todoFiles.Sum(f => Safe(() => new FileInfo(f).Length)), todoFiles.Length));
        return groups;
    }

    static string[] Directories(string path) => Safe(() => Directory.GetDirectories(path));

    static T Safe<T>(Func<T> read) where T : new()
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new T();
        }
    }

    static string[] Safe(Func<string[]> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A file's size, or a folder's (every file under it; links are not followed).</summary>
    static (long Bytes, int Files) Size(string path)
    {
        if (File.Exists(path)) return (Safe(() => new FileInfo(path).Length), 1);
        if (!Directory.Exists(path)) return (0, 0);
        long bytes = 0;
        var files = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                bytes += file.Length;
                files++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return (bytes, files);
    }

    /// <summary>"12.3 MB", "512 KB", "40 B".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => $"{bytes} B",
    };
}
