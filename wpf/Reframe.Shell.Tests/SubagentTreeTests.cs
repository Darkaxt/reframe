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
        [.. SidebarView.Nest(SidebarChats.Build(new SessionList { App = [.. items] }), shown).Select(n => (n.Chat.Label, n.Depth, n.Subagents + n.Ended, n.Shown))];

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

    [Fact]
    public void A_subagent_in_another_folder_is_listed_under_its_opener_in_the_openers_project()
    {
        var lead = Thread("lead", "idle", 10);
        var worker = Thread("w1", "busy", 30, "lead") with { Cwd = @"D:\Workers" };
        FolderWorkspace Project(string cwd) => new() { Id = Workspaces.WorkspaceKey(cwd), Cwd = cwd, Name = cwd };
        var list = new SessionList { App = [lead, worker], FolderWorkspaces = [Project(@"D:\P"), Project(@"D:\Workers")] };
        var sections = SidebarView.Build(list, 100);
        var main = sections.Projects.Single(p => p.Cwd == @"D:\P");
        Assert.Equal(["lead", "w1"], main.Chats.Select(c => c.Label));
        Assert.Equal([0, 1], main.Chats.Select(c => c.Depth));
        Assert.Empty(sections.Projects.Single(p => p.Cwd == @"D:\Workers").Chats);
    }

    [Fact]
    public void The_count_leaves_out_ended_subagents_and_the_page_limit_counts_parents_only()
    {
        var items = new List<ListItem> { Thread("lead", "idle", 100), Thread("live", "busy", 50, "lead") };
        for (var n = 0; n < 30; n++) items.Add(Thread($"done{n}", "ended", n, "lead"));
        var rows = SidebarView.Nest(SidebarChats.Build(new SessionList { App = items }), null, limit: 20);
        var lead = rows.Single(r => r.Depth == 0);
        Assert.Equal((1, 30, true), (lead.Subagents, lead.Ended, lead.Shown));
        Assert.Equal(32, rows.Count);
    }
}
