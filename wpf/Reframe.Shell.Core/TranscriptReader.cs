using System.Text;

namespace Reframe.Shell.Core;

/// <summary>What one read of the transcript produced.</summary>
/// <param name="Lines">The complete new lines (no line terminator, blanks dropped).</param>
/// <param name="NewOffset">The byte offset to store and pass to the next read.</param>
/// <param name="WasReset">True when the file was shorter than the stored offset, so the read restarted at 0 and the caller should <see cref="TranscriptModel.Reset"/> before appending.</param>
public sealed record TranscriptRead(IReadOnlyList<string> Lines, long NewOffset, bool WasReset);

/// <summary>
/// Tails a UTF-8 JSONL transcript by byte offset. Only complete lines (ended by a newline) are returned; a partial
/// last line stays unread until its newline arrives. A file shorter than the offset means it was truncated or
/// rewritten (compaction), so reading restarts at 0. A rewrite that is not shorter cannot be detected here.
/// </summary>
public static class TranscriptReader
{
    /// <summary>The most one read takes (the end of a longer transcript): the view shows the recent part of it.</summary>
    public const int MaxRead = 8 * 1024 * 1024;

    /// <summary>Reads from the file at <paramref name="path"/>. A missing file reads as no new lines at the same offset.</summary>
    public static TranscriptRead Read(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream, offset);
        }
        catch (FileNotFoundException)
        {
            return new TranscriptRead(Array.Empty<string>(), offset, false);
        }
        catch (DirectoryNotFoundException)
        {
            return new TranscriptRead(Array.Empty<string>(), offset, false);
        }
    }

    /// <summary>The complete lines between <paramref name="from"/> and <paramref name="to"/> (an offset a read returned): a line
    /// cut by <paramref name="from"/> is left out. For the Chat view's earlier part, loaded when asked for.</summary>
    public static IReadOnlyList<string> ReadRange(string path, long from, long to)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        from = Math.Max(0, from);
        to = Math.Min(to, stream.Length);
        if (to <= from) return [];
        // From the byte before: a line that starts exactly at from is whole.
        var lead = from > 0 ? 1 : 0;
        stream.Seek(from - lead, SeekOrigin.Begin);
        var buffer = new byte[to - from + lead];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        var start = lead == 0 || buffer[0] == (byte)'\n' ? lead : Array.IndexOf(buffer, (byte)'\n', 0, total) + 1;
        if (start <= 0 && lead > 0) return [];
        var lines = new List<string>();
        for (int i = start; i < total; i++)
        {
            if (buffer[i] != (byte)'\n') continue;
            int end = i;
            if (end > start && buffer[end - 1] == (byte)'\r') end--;
            if (end > start)
            {
                var text = Encoding.UTF8.GetString(buffer, start, end - start);
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
            }
            start = i + 1;
        }
        return lines;
    }

    /// <summary>Reads from a seekable stream.</summary>
    public static TranscriptRead Read(Stream stream, long offset)
    {
        bool reset = false;
        if (offset < 0 || stream.Length < offset)
        {
            offset = 0;
            reset = true;
        }

        long remaining = stream.Length - offset;
        if (remaining <= 0) return new TranscriptRead(Array.Empty<string>(), offset, reset);
        // A long transcript (or a long gap) is read from its last MaxRead bytes only, from the first whole line in them.
        var skipPartial = false;
        if (remaining > MaxRead)
        {
            offset = stream.Length - MaxRead;
            remaining = MaxRead;
            skipPartial = true;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[remaining];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }

        int lastNewline = total == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', total - 1);
        if (total == 0 || lastNewline < 0) return new TranscriptRead(Array.Empty<string>(), offset, reset);

        var lines = new List<string>();
        int start = skipPartial ? Array.IndexOf(buffer, (byte)'\n', 0, lastNewline + 1) + 1 : 0;
        for (int i = 0; i <= lastNewline; i++)
        {
            if (buffer[i] != (byte)'\n') continue;
            int end = i;
            if (end > start && buffer[end - 1] == (byte)'\r') end--;
            if (end > start)
            {
                var text = Encoding.UTF8.GetString(buffer, start, end - start);
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
            }
            start = i + 1;
        }

        return new TranscriptRead(lines, offset + lastNewline + 1, reset);
    }
}
