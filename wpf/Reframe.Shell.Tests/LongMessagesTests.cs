using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Long messages for threads go through a file (LongMessages).</summary>
public class LongMessagesTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), $"longmessages-{Guid.NewGuid():N}");

    [Fact]
    public void A_short_message_is_typed_as_it_is()
    {
        var dir = TempDir();
        Assert.Equal("hello", LongMessages.Prepare("hello", dir, DateTimeOffset.Now));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void A_long_message_is_written_and_pointed_at_keeping_the_header()
    {
        var dir = TempDir();
        try
        {
            var text = "[message from the Reframe thread \"eBook Backend\"; answer it with wake_thread to \"eBook Backend\"]\n" + new string('x', 4000);
            var line = LongMessages.Prepare(text, dir, DateTimeOffset.Now)!;
            var file = Assert.Single(Directory.GetFiles(dir));
            Assert.Equal(text, File.ReadAllText(file));
            Assert.StartsWith("[message from the Reframe thread \"eBook Backend\"", line);
            Assert.Contains(file, line);
            Assert.True(line.Length < 400);
            // As a WSL thread reaches it.
            var wsl = LongMessages.Prepare(text, dir, DateTimeOffset.Now, p => "/mnt/x/" + Path.GetFileName(p))!;
            Assert.Contains("/mnt/x/", wsl);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Old_files_are_removed()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var old = Path.Combine(dir, "old.md");
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - LongMessages.KeepFor - TimeSpan.FromHours(1));
            var recent = Path.Combine(dir, "recent.md");
            File.WriteAllText(recent, "y");
            LongMessages.Prune(dir, DateTimeOffset.Now);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
