using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class TranscriptForkTests
{
    static readonly string[] Lines =
    [
        """{"type":"user","uuid":"u1","sessionId":"old","message":{"content":"first"}}""",
        """{"type":"assistant","uuid":"a1","sessionId":"old","message":{"content":[{"type":"text","text":"ok"}]}}""",
        """{"type":"user","uuid":"u2","sessionId":"old","message":{"content":"second"}}""",
        """{"type":"assistant","uuid":"a2","sessionId":"old","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}""",
        """{"type":"user","uuid":"u3","sessionId":"old","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"x"}]}}""",
    ];

    [Fact]
    public void Before_a_message_keeps_what_came_first_with_the_new_session_id()
    {
        var cut = TranscriptFork.Cut(Lines, "u2", true, "old", "new")!;
        Assert.Equal(2, cut.Count);
        Assert.All(cut, l => Assert.DoesNotContain("old", l));
        Assert.Contains("\"new\"", cut[0]);
    }

    [Fact]
    public void After_a_message_includes_it()
    {
        Assert.Equal(2, TranscriptFork.Cut(Lines, "a1", false, "old", "new")!.Count);
        Assert.Equal(5, TranscriptFork.Cut(Lines, "u3", false, "old", "new")!.Count);
    }

    [Fact]
    public void A_call_without_its_result_is_dropped_and_an_unknown_message_gives_null()
    {
        Assert.Equal(3, TranscriptFork.Cut(Lines, "a2", false, "old", "new")!.Count);
        Assert.Null(TranscriptFork.Cut(Lines, "nope", true, "old", "new"));
    }
}
