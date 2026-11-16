using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class BroadcastTests
{
    static readonly Dictionary<string, string> Statuses = new()
    {
        ["busy"] = "busy", ["bg"] = "background", ["idle"] = "idle", ["waiting"] = "waiting",
        ["starting"] = "starting", ["pausedIdle"] = "idle", ["pausedBusy"] = "busy",
    };

    static List<Broadcast.Target> Targets(string kind, params string[] paused) =>
        Broadcast.Targets(kind, Statuses.Keys, id => Statuses[id], paused.ToHashSet());

    [Fact]
    public void PauseGoesToThreadsAtWorkAndInterruptsTheBusyOnes()
    {
        Assert.Equal(
            [new("busy", true), new("bg", false), new("pausedBusy", true)],
            Targets(Broadcast.Pause));
    }

    [Fact]
    public void ContinueGoesOnlyToThreadsPausedFromHereThatAreIdleNow()
    {
        Assert.Equal([new("pausedIdle", false)], Targets(Broadcast.Continue, "pausedIdle", "pausedBusy", "waiting"));
        Assert.Empty(Targets(Broadcast.Continue));
    }

    [Theory]
    [InlineData(Broadcast.Cleanup)]
    [InlineData(Broadcast.Custom)]
    public void OtherMessagesGoToEveryThreadThatCanBeTypedInto(string kind)
    {
        var targets = Targets(kind);
        Assert.Equal(["busy", "bg", "idle", "pausedIdle", "pausedBusy"], targets.Select(t => t.SessionId));
        Assert.Equal(["busy", "pausedBusy"], targets.Where(t => t.Interrupt).Select(t => t.SessionId));
    }

    [Fact]
    public void PresetsHaveTheirTextsAndQuestions()
    {
        Assert.Contains("safe pause point", Broadcast.TextOf(Broadcast.Pause));
        Assert.Contains("continue now", Broadcast.TextOf(Broadcast.Continue));
        Assert.Contains("input request", Broadcast.TextOf(Broadcast.Cleanup));
        Assert.Null(Broadcast.TextOf(Broadcast.Custom));
        Assert.Equal("Ask 1 thread to reach a safe pause point?", Broadcast.Question(Broadcast.Pause, 1));
        Assert.Equal("Send the message to 3 threads?", Broadcast.Question(Broadcast.Custom, 3));
        Assert.Equal("\u0002", Broadcast.Background);
    }
}
