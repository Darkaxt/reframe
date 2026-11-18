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

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[remaining];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }

        int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', total - 1);
        if (total == 0 || lastNewline < 0) return new TranscriptRead(Array.Empty<string>(), offset, reset);

        var lines = new List<string>();
        int start = 0;
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
