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
    public void The_user_owns_the_prompt_from_the_first_key_until_it_is_sent_or_cleared()
    {
        var t = DateTimeOffset.Now;
        var later = t + InputGuard.Quiet + TimeSpan.FromSeconds(1);
        Assert.False(InputGuard.UserOwns("d1", t));
        InputGuard.NoteUser("d1", "hel", t);
        Assert.True(InputGuard.UserOwns("d1", later));
        InputGuard.NoteUser("d1", "lo\r", t);
        Assert.True(InputGuard.UserOwns("d1", t));      // just typed
        Assert.False(InputGuard.UserOwns("d1", later)); // sent

        InputGuard.NoteUser("d1", "ab\u007f\u007f", t);
        Assert.False(InputGuard.UserOwns("d1", later)); // deleted again
        InputGuard.NoteUser("d1", "\u001b[200~pasted\u001b[201~", t);
        Assert.True(InputGuard.UserOwns("d1", later));
        InputGuard.NoteUser("d1", "\u0003", t);
        Assert.False(InputGuard.UserOwns("d1", later)); // Ctrl+C cleared it

        // The history's arrows bring an old prompt: owned until it is sent or cleared.
        InputGuard.NoteUser("d1", "\u001b[A", t);
        Assert.True(InputGuard.UserOwns("d1", later));
        InputGuard.Forget("d1");
        Assert.False(InputGuard.UserOwns("d1", t));

        // Mouse and focus reports are not writing.
        InputGuard.NoteUser("d2", "\u001b[<0;10;5M\u001b[I\u001b[O", t);
        Assert.False(InputGuard.UserOwns("d2", t));
    }

    [Fact]
    public async Task Reframes_messages_queue_until_the_user_sends_their_prompt()
    {
        InputGuard.NoteUser("q1", "half a thou", DateTimeOffset.Now - InputGuard.Quiet);
        var first = InputGuard.TakeAsync("q1");
        var second = InputGuard.TakeAsync("q1");
        await Task.Delay(100);
        Assert.False(first.IsCompleted);
        InputGuard.NoteUser("q1", "ght\r", DateTimeOffset.Now - InputGuard.Quiet);
        var one = await first.WaitAsync(TimeSpan.FromSeconds(5));
        // One at a time: the second waits for the first to be typed in.
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        // The user's keys meanwhile wait for the message.
        Assert.True(InputGuard.Defer("q1", () => Task.CompletedTask));
        one.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
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
