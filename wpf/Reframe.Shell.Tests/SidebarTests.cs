using System.Text.RegularExpressions;
using System.Windows.Media;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The sidebar's logic, ported with its tests: src/shared/sidebar-chats.test.ts, project-view.test.ts
/// (projectActivity), labels.test.ts, last-used.test.ts, hidden-threads.test.ts, folder-workspaces.test.ts,
/// src/renderer/time.test.ts and src/main/paths.test.ts (homeRelative, Windows).</summary>
public class SidebarTests
{
    const string Id = "6d4c6300-9f9c-4fe5-a76d-5091b6b1f11a";

    static readonly ListItem Managed = new()
    {
        Kind = ListItemKind.App, Id = "managed", ClaudeSessionId = Id, Label = "Managed", Cwd = @"D:\Project", Provider = "anthropic",
        Status = "ended", Lifecycle = "ended", LastActiveAt = 10,
    };

    [Fact]
    public void ChatsKeepTheirIdentityThroughHistoryAdoptionAndTheRecordedProvider()
    {
        var found = new ListItem { Kind = ListItemKind.Found, ClaudeSessionId = Id, Cwd = @"D:\Project", Title = "Old", ModifiedAt = 5 };
        var before = SidebarChats.Build(new SessionList { Found = [found] })[0];
        var after = SidebarChats.Build(new SessionList { App = [Managed], PinnedItems = [found] });
        Assert.Single(after);
        Assert.Equal(before.Key, after[0].Key);
        Assert.Equal("anthropic", after[0].Provider);
        Assert.False(after[0].Defaulted);
        Assert.Equal(new ListItemRef(ListItemKind.App, SessionId: "managed"), after[0].Ref);
    }

    [Fact]
    public void ChatsAreOrderedByActivityAcrossSourcesAndWslIdentitiesDiffer()
    {
        var chats = SidebarChats.Build(new SessionList
        {
            App = [Managed],
            Running = [new ListItem { Kind = ListItemKind.Running, Pid = 1, ClaudeSessionId = Id, Cwd = @"\\wsl$\Ubuntu\home\project", Distro = "Ubuntu", LastActiveAt = 20 }],
            Found = [new ListItem { Kind = ListItemKind.Found, ClaudeSessionId = "b34f6c30-9f9c-4fe5-a76d-5091b6b1f11a", Cwd = @"D:\Other", ModifiedAt = 30 }],
        });
        Assert.Equal([30.0, 20, 10], chats.Select(c => c.LastActiveAt));
        Assert.Equal("chatgpt", chats[0].Provider);
        Assert.True(chats[0].Defaulted);
        Assert.Null(chats[1].Provider);
        Assert.Equal(SidebarChats.ChatKey(Id, "Ubuntu"), SidebarChats.ChatKey(Id, "ubuntu"));
        Assert.NotEqual(SidebarChats.ChatKey(Id), SidebarChats.ChatKey(Id, "Ubuntu"));
        Assert.Equal((Id, "ubuntu"), SidebarChats.ParseChatKey(SidebarChats.ChatKey(Id, "Ubuntu")));
        Assert.Null(SidebarChats.ParseChatKey("[\"\", \"../outside\"]"));
        Assert.Equal($"[\"\",\"{Id}\"]", SidebarChats.ChatKey(Id));
    }

    [Fact]
    public void ProjectActivityCountsRunningSessionsWithTheStateThatMostNeedsAttention()
    {
        var activity = ProjectView.Activity([
            ("C:/w/shop", "idle"), ("D:/other/shop", "busy"), ("C:/w/blog", "starting"), ("C:/w/blog", "waiting"),
            ("C:/w/docs", "ended"), ("C:/w/api", "error"),
        ]);
        Assert.Equal(new ProjectActivity(2, "busy"), activity[Workspaces.WorkspaceKey("C:/w/shop")]);
        Assert.Equal(new ProjectActivity(2, "waiting"), activity[Workspaces.WorkspaceKey("C:/w/blog")]);
        Assert.Equal(new ProjectActivity(1, "error"), activity[Workspaces.WorkspaceKey("C:/w/api")]);
        Assert.False(activity.ContainsKey(Workspaces.WorkspaceKey("C:/w/docs")));
    }

    [Fact]
    public void SessionLabelPrefersTheTypedNameThenTheTitleThenTheFolder()
    {
        Assert.Equal("Mine", StateValues.SessionLabel("Mine", "AI title", @"C:\work\Public"));
        Assert.Equal("AI title", StateValues.SessionLabel(null, "AI title", @"C:\work\Public"));
        Assert.Equal("Public", StateValues.SessionLabel("  ", null, @"C:\work\Public"));
        Assert.Equal("shop", StateValues.SessionLabel(null, null, "/home/me/shop/"));
    }

    [Theory]
    [InlineData("idle", "busy", true)]
    [InlineData("busy", "idle", true)]
    [InlineData("busy", "waiting", true)]
    [InlineData("idle", "waiting", true)]
    [InlineData("waiting", "busy", true)]
    [InlineData("starting", "idle", false)]
    [InlineData("idle", "ended", false)]
    [InlineData("idle", "error", false)]
    [InlineData("waiting", "idle", false)]
    [InlineData("busy", "busy", false)]
    public void CountsAsUse(string before, string after, bool counts) => Assert.Equal(counts, LastUsed.CountsAsUse(before, after));

    static HiddenThread Thread(string id, double hiddenAt = 1) => new() { ClaudeSessionId = id, Label = $"Thread {id}", Cwd = "C:/work", HiddenAt = hiddenAt };

    [Fact]
    public void HiddenThreadsAreHiddenOnceKeepingTheNewestLabelAndTime()
    {
        var once = HiddenThreads.Hide([], Thread("a"));
        var twice = HiddenThreads.Hide(once, Thread("a", 2));
        Assert.Equal([("a", 2.0)], twice.Select(h => (h.ClaudeSessionId, h.HiddenAt)));
        Assert.Equal(["b"], HiddenThreads.Hide(null, Thread("b")).Select(h => h.ClaudeSessionId));
        Assert.Equal(["a", "b"], HiddenThreads.HiddenIds(HiddenThreads.Hide(once, Thread("b"))));
    }

    [Fact]
    public void HiddenThreadsAreRestoredSomeOrAll()
    {
        List<HiddenThread> hidden = [Thread("a"), Thread("b"), Thread("c")];
        Assert.Equal(["a", "c"], HiddenThreads.Restore(hidden, ["b"]).Select(h => h.ClaudeSessionId));
        Assert.Equal(hidden, HiddenThreads.Restore(hidden, ["unknown"]));
        Assert.Empty(HiddenThreads.Restore(hidden, null));
        Assert.Empty(HiddenThreads.Restore(null, ["a"]));
        var items = new[] { "a", "b" };
        Assert.Equal(["b"], HiddenThreads.WithoutHidden(items, HiddenThreads.HiddenIds([Thread("a")]), i => i));
        Assert.Equal(items, HiddenThreads.WithoutHidden(items, new HashSet<string>(), i => i));
    }

    [Fact]
    public void FolderWorkspacesMergeFinalFolderNamesAcrossPathsAndProviders()
    {
        var groups = Workspaces.Build([
            (@"D:\work\game", "a", null), ("d:/WORK/game/", "b", null), ("D:/other/game", "c", "anthropic"), ("D:/other/different", "d", null),
        ]);
        Assert.Equal(2, groups.Count);
        var game = groups.Single(g => g.Id == Workspaces.WorkspaceKey("d:/work/game"));
        Assert.Equal(["a", "b", "c"], game.SessionIds);
        Assert.Equal([(@"D:\work\game", "chatgpt", "a,b"), ("D:/other/game", "anthropic", "c")],
            game.Locations!.Select(l => (l.Cwd, l.Provider, string.Join(",", l.SessionIds))));
        Assert.Equal("d:/work/game", Workspaces.FolderKey("D:/work/./game/../game"));
    }

    [Fact]
    public void FolderWorkspacesKeepLinuxCaseAndDropDuplicateIdentities()
    {
        Assert.NotEqual(Workspaces.FolderKey("/A"), Workspaces.FolderKey("/a"));
        Assert.Equal(["one"], Workspaces.Build([("/a", "one", null), ("/a", "one", null)])[0].SessionIds);
        var groups = Workspaces.Build([("D:/work/game", "gpt", "chatgpt"), (@"d:\WORK\game\", "claude", "anthropic")]);
        Assert.Single(groups);
        Assert.Equal(["gpt", "claude"], groups[0].SessionIds);
        Assert.Equal(["chatgpt", "anthropic"], groups[0].Locations!.Select(l => l.Provider));
    }

    [Theory]
    [InlineData("chatgpt:c:/owner/ChatGPT/Android")]
    [InlineData("anthropic:c:/owner/Anthropic/Android")]
    [InlineData("d:/other/ANDROID")]
    [InlineData("project:android")]
    public void LegacyExpansionKeysBecomeOneProject(string key) => Assert.Equal("project:android", Workspaces.ExpandedWorkspaceKey(key));

    [Fact]
    public void RebaseFolderMovesFoldersInsideTheRootOnly()
    {
        const string from = @"C:\Users\me\Documents\ChatGPT\Windows", to = @"C:\Users\me\Documents\Anthropic\Windows";
        Assert.Equal($@"{to}\Foo/Bar", Workspaces.RebaseFolder($@"{from}\Foo/Bar", from, to));
        Assert.Equal($@"{to}\Foo", Workspaces.RebaseFolder("c:/users/ME/documents/chatgpt/windows/Foo", from, to));
        Assert.Equal($@"{to}\foo", Workspaces.RebaseFolder($@"{from}\foo", $@"{from}\", $@"{to}\"));
        Assert.Equal(to, Workspaces.RebaseFolder(from, from, to));
        Assert.Equal(to, Workspaces.RebaseFolder($@"{from}\", from, to));
        Assert.Equal(to, Workspaces.RebaseFolder("", from, to));
        Assert.Equal(@"D:\Projects\x", Workspaces.RebaseFolder(@"D:\Projects\x", from, to));
        const string sibling = @"C:\Users\me\Documents\ChatGPT2\Windows\x";
        Assert.Equal(sibling, Workspaces.RebaseFolder(sibling, @"C:\Users\me\Documents\ChatGPT", to));
        Assert.Equal($@"{from}2\x", Workspaces.RebaseFolder($@"{from}2\x", from, to));
    }

    [Fact]
    public void ShortTimeCountsMinutesHoursAndDaysForTheLastWeek()
    {
        var now = new DateTimeOffset(new DateTime(2026, 11, 9, 15, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        const double Minute = 60_000, Hour = 60 * Minute, Day = 24 * Hour;
        string En(double ago) => RelativeTime.Short(now - ago, now);
        Assert.Equal("now", En(20_000));
        Assert.Equal("5m", En(5 * Minute + 59_000));
        Assert.Equal("59m", En(59 * Minute));
        Assert.Equal("2h", En(2 * Hour));
        Assert.Equal("3d", En(3 * Day));
        Assert.Equal("5 Min.", RelativeTime.Short(now - 5 * Minute, now, "de"));
        Assert.Equal("3 Tg.", RelativeTime.Short(now - 3 * Day, now, "de"));
        double At(int y, int m, int d) => new DateTimeOffset(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        Assert.Equal("Oct 28", RelativeTime.Short(At(2026, 10, 28), now));
        Assert.Equal("Nov 2025", RelativeTime.Short(At(2025, 11, 6), now));
        Assert.Equal("now", RelativeTime.Short(now + 5_000, now));
    }

    [Theory]
    [InlineData(@"C:\Users\Ann\projects\shop", @"C:\Users\Ann", @"~\projects\shop")]
    [InlineData(@"c:\users\ann\projects", @"C:\Users\Ann\", @"~\projects")]
    [InlineData(@"D:\work\shop", @"C:\Users\Ann", @"D:\work\shop")]
    [InlineData(@"C:\Users\Anna\shop", @"C:\Users\Ann", @"C:\Users\Anna\shop")]
    public void HomeRelative(string folder, string home, string expected) => Assert.Equal(expected, RelativeTime.HomeRelative(folder, home));

    // ---------- the session list as the visual fixture makes it ----------

    static readonly string Home = @"C:\Users\visual";

    static VisualFixture Sessions(double now) => VisualFixture.Load(Path.Combine(TokenTestsRoot.Root, "tools", "visual", "fixtures", "sessions.json"), Home, now);

    [Fact]
    public async Task TheSessionsFixtureListsProjectsPinnedAndRecentsAsElectronDoes()
    {
        var now = 1_800_000_000_000.0;
        var fixture = Sessions(now);
        var list = await SessionListBuilder.BuildAsync(fixture.Sources(), "", SessionListBuilder.Page);
        var view = SidebarView.Build(list, now);

        Assert.Equal(["Pinned: plan the Q3 roadmap"], view.Pinned.Select(r => r.Label));
        Assert.Equal("docs-site · ended", view.Pinned[0].Meta);
        Assert.Equal("4h", view.Pinned[0].Stamp);
        Assert.Equal(["api-gateway", "docs-site", "mobile-app", "moved-away", "webshop"], view.Projects.Select(p => p.Name));
        var projects = view.Projects.ToDictionary(p => p.Name);
        Assert.Equal(2, projects["api-gateway"].Count);
        Assert.Equal(new ProjectActivity(1, "busy"), projects["api-gateway"].Activity);
        Assert.Equal(4, projects["webshop"].Count);
        Assert.Equal(new ProjectActivity(2, "waiting"), projects["webshop"].Activity);
        Assert.Null(projects["docs-site"].Activity);
        // ci-pipeline has no time (no transcript, no start time in the registry entry), so it comes last.
        Assert.Equal(["rate-limiter", "db-migration", "old-checkout", "checkout-fix", "release-notes", "Add dark mode to the account settings",
            "Pinned: plan the Q3 roadmap", "Bump dependencies and fix the deprecation warnings", "ci-pipeline"], view.Recents.Select(r => r.Label));
        var rows = view.Recents.ToDictionary(r => r.Label);
        Assert.Equal("api-gateway · window 1", rows["rate-limiter"].Meta);
        Assert.Equal("busy", rows["rate-limiter"].Status);
        Assert.Equal("moved-away · ended", rows["old-checkout"].Meta);
        Assert.Equal("now", rows["checkout-fix"].Stamp);
        Assert.Equal("2h", rows["release-notes"].Stamp);
        Assert.True(rows["ci-pipeline"].RunningMark);
        Assert.Equal("webshop · Running", rows["ci-pipeline"].Meta);
        Assert.Equal("ended", rows["Add dark mode to the account settings"].Status);
        Assert.Equal("chatgpt", rows["Add dark mode to the account settings"].Provider);
    }

    [Fact]
    public async Task SearchingFiltersEverySection()
    {
        var now = 1_800_000_000_000.0;
        var fixture = Sessions(now);
        var list = await SessionListBuilder.BuildAsync(fixture.Sources(), "check", SessionListBuilder.Page);
        var view = SidebarView.Build(list, now);
        Assert.Empty(view.Pinned);
        Assert.Equal(["old-checkout", "checkout-fix"], view.Recents.Select(r => r.Label));
        var webshop = view.Projects.Single(p => p.Name == "webshop");
        Assert.Equal(new ProjectActivity(1, "idle"), webshop.Activity);
    }

    // ---------- icons ----------

    [Fact]
    public void IconPathsAreTheElectronUisAndParseInWpf()
    {
        var sources = string.Concat(
            File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "src", "renderer", "components", "icons.tsx")),
            File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "src", "renderer", "components", "ProviderIcon.tsx")));
        foreach (var icon in Icons.All)
            foreach (var part in icon.Parts.OfType<IconPath>())
            {
                Assert.Contains(part.D, sources);
                Assert.False(Geometry.Parse(SvgPath.ToWpf(part.D)).IsEmpty(), icon.Name);
            }
        Assert.Equal("a 1 1 0 0 0 -1 0", SvgPath.ToWpf("a1 1 0 00-1 0"));
        Assert.Equal("M .5 .5", Regex.Replace(SvgPath.ToWpf("M.5.5"), "0\\.", "."));
    }
}

static class TokenTestsRoot
{
    public static readonly string Root = Find();

    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "renderer", "styles.css"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
