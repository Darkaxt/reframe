using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The user's typing waits while Reframe types a message into the same thread.</summary>
public class InputGuardTests
{
    [Fact]
    public async Task Typing_waits_for_the_hold_and_follows_in_order()
    {
        var sent = new List<string>();
        Func<Task> Send(string s) => () => { lock (sent) sent.Add(s); return Task.CompletedTask; };
        Assert.False(InputGuard.Defer("g1", Send("free")));
        var hold = InputGuard.Hold("g1");
        Assert.True(InputGuard.Defer("g1", Send("a")));
        Assert.True(InputGuard.Defer("g1", Send("b")));
        Assert.False(InputGuard.Defer("other", Send("elsewhere")));
        Assert.Empty(sent);
        hold.Dispose();
        hold.Dispose();
        for (var i = 0; i < 100 && sent.Count < 2; i++) await Task.Delay(20);
        Assert.Equal(["a", "b"], sent);
        Assert.False(InputGuard.Defer("g1", Send("after")));
    }

    [Fact]
    public async Task Overlapping_holds_release_together()
    {
        var sent = new List<string>();
        var one = InputGuard.Hold("g2");
        var two = InputGuard.Hold("g2");
        Assert.True(InputGuard.Defer("g2", () => { lock (sent) sent.Add("x"); return Task.CompletedTask; }));
        one.Dispose();
        await Task.Delay(100);
        Assert.Empty(sent);
        two.Dispose();
        for (var i = 0; i < 100 && sent.Count < 1; i++) await Task.Delay(20);
        Assert.Equal(["x"], sent);
    }
}
