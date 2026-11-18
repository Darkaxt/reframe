using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>A thread another thread opened (open_thread) is listed as that thread's subagent, under it.</summary>
public class SubagentTreeTests
{
    static ListItem Thread(string id, string status, double at, string? openedBy = null) => new()
    {
        Kind = ListItemKind.App, Id = id, ClaudeSessionId = $"c-{id}", Label = id, Cwd = @"D:\P", Provider = "anthropic",
        Status = status, LastActiveAt = at, OpenedBy = openedBy,
    };

    static List<(string Label, int Depth, int Subagents, bool Shown)> Nested(IReadOnlyDictionary<string, bool>? shown, params ListItem[] items) =>
        [.. SidebarView.Nest(SidebarChats.Build(new SessionList { App = [.. items] }), shown).Select(n => (n.Chat.Label, n.Depth, n.Subagents, n.Shown))];

    [Fact]
    public void Subagents_follow_their_parent_even_when_they_were_used_later()
    {
        var rows = Nested(null, Thread("lead", "idle", 10), Thread("other", "ended", 20), Thread("w1", "busy", 30, "lead"), Thread("w2", "ended", 5, "lead"));
        Assert.Equal([("other", 0, 0, false), ("lead", 0, 2, true), ("w1", 1, 0, false), ("w2", 1, 0, false)], rows);
    }

    [Fact]
    public void Subagents_are_hidden_by_default_once_all_have_ended_and_the_user_can_choose()
    {
        var items = new[] { Thread("lead", "ended", 10), Thread("w1", "ended", 5, "lead") };
        Assert.Equal([("lead", 0, 1, false)], Nested(null, items));
        var key = SidebarChats.ChatKey("c-lead");
        Assert.Equal([("lead", 0, 1, true), ("w1", 1, 0, false)], Nested(new Dictionary<string, bool> { [key] = true }, items));
        var busy = new[] { Thread("lead", "ended", 10), Thread("w1", "busy", 5, "lead") };
        Assert.Equal([("lead", 0, 1, false)], Nested(new Dictionary<string, bool> { [key] = false }, busy));
    }

    [Fact]
    public void A_thread_whose_opener_is_not_listed_stands_alone()
    {
        Assert.Equal([("w1", 0, 0, false)], Nested(null, Thread("w1", "idle", 5, "gone")));
    }
}
