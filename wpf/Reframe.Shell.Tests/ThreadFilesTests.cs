using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ThreadFilesTests
{
    [Fact]
    public void MeasuresAThreadsFilesInClaudesHome()
    {
        var home = Directory.CreateTempSubdirectory("reframe-threadfiles-").FullName;
        try
        {
            const string id = "11111111-2222-3333-4444-555555555555";
            void Write(string path, int bytes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[bytes]);
            }
            Write(Path.Combine(home, "projects", "C--work", id + ".jsonl"), 3000);
            Write(Path.Combine(home, "projects", "C--work", id, "subagents", "a.jsonl"), 100);
            Write(Path.Combine(home, "projects", "C--work", id, "tool-results", "b.txt"), 50);
            Write(Path.Combine(home, "file-history", id, "x@v1"), 20);
            Write(Path.Combine(home, "todos", id + "-agent-" + id + ".json"), 2);
            Write(Path.Combine(home, "projects", "C--work", "other.jsonl"), 999);

            var groups = ThreadFiles.Measure(home, id);
            Assert.Equal(["Transcript", "Subagents and tool results", "File history", "Todos"], groups.Select(g => g.Name));
            Assert.Equal(3000, groups[0].Bytes);
            Assert.Equal((150L, 2), (groups[1].Bytes, groups[1].Files));
            Assert.Empty(ThreadFiles.Measure(home, "../escape"));
            Assert.Empty(ThreadFiles.Measure(Path.Combine(home, "missing"), id));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void FormatsSizes()
    {
        Assert.Equal("40 B", ThreadFiles.Bytes(40));
        Assert.Equal("2 KB", ThreadFiles.Bytes(2048));
        Assert.Equal("1.5 MB", ThreadFiles.Bytes(3 << 19));
        Assert.Equal("2.0 GB", ThreadFiles.Bytes(2L << 30));
    }
}
