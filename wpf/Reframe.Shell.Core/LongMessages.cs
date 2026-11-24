namespace Reframe.Shell.Core;

/// <summary>A long message for a thread (wake_thread, a handed-off job, a task's prompt) is put in a file and the
/// thread is given a short line pointing at it: a long paste into a big session can lose the Enter that sends it.</summary>
public static class LongMessages
{
    /// <summary>Messages longer than this go through a file.</summary>
    public const int InlineLimit = 1000;

    /// <summary>How long the files are kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

    /// <summary>The text to type in: <paramref name="text"/> itself when short; else the file it was written to (named
    /// as the thread reaches it: <paramref name="readerPath"/> maps a Windows path, e.g. for WSL), after the message's
    /// first line when that is Reframe's "[...]" header. Null when the file could not be written.</summary>
    public static string? Prepare(string text, string dir, DateTimeOffset now, Func<string, string>? readerPath = null)
    {
        if (text.Length <= InlineLimit) return text;
        Directory.CreateDirectory(dir);
        Prune(dir, now);
        var path = Path.Combine(dir, $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.md");
        File.WriteAllText(path, text);
        var shown = readerPath?.Invoke(path) ?? path;
        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
        var header = firstLine.StartsWith('[') && firstLine.EndsWith(']') && firstLine.Length < 400 ? firstLine + "\n" : "";
        return $"{header}[Reframe: the message ({text.Length} characters) is in the file {shown}. Read all of it and act on it as if it had been typed here.]";
    }

    /// <summary>Removes the files older than <see cref="KeepFor"/>.</summary>
    public static void Prune(string dir, DateTimeOffset now)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            try
            {
                if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) > KeepFor) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
