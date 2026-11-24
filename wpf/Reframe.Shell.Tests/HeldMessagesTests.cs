using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Messages for ended threads are held until those threads run (HeldMessages).</summary>
public class HeldMessagesTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), $"heldmessages-{Guid.NewGuid():N}", "held.json");

    static HeldMessage Held(string target, long at) => new(target, "Navic", "s1", "Lead", $"[message]\nfor {target} at {at}", at);

    [Fact]
    public void Saves_loads_and_takes_a_targets_messages()
    {
        var file = TempFile();
        try
        {
            HeldMessages.Save(file, new List<HeldMessage> { Held("t1", 2), Held("t2", 1), Held("t1", 1) });
            var held = HeldMessages.Load(file);
            Assert.Equal(3, held.Count);
            var taken = HeldMessages.TakeFor(held, "t1");
            Assert.Equal(new long[] { 1, 2 }, taken.Select(m => m.HeldAt));
            Assert.Equal("t2", Assert.Single(held).TargetId);
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Fact]
    public void A_missing_or_corrupt_file_is_empty()
    {
        var file = TempFile();
        try
        {
            Assert.Empty(HeldMessages.Load(file));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ not json");
            Assert.Empty(HeldMessages.Load(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Fact]
    public void Messages_expire_after_fourteen_days()
    {
        var now = DateTimeOffset.Now;
        var old = Held("t1", now.AddDays(-15).ToUnixTimeMilliseconds());
        var fresh = Held("t2", now.AddDays(-1).ToUnixTimeMilliseconds());
        var held = new List<HeldMessage> { old, fresh };
        Assert.Equal(old, Assert.Single(HeldMessages.TakeExpired(held, now)));
        Assert.Equal(fresh, Assert.Single(held));
    }
}
