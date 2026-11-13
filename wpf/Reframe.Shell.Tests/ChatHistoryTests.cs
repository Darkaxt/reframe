using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The chat history rules of src/main/app.ts (provider/model, retire, restore, the batch report) with
/// src/shared/providers.test.ts's modelForProviderChange cases, the selection's clicks, the selected chats' menu, the
/// WSL sources and archives of the list, and the history/wsl visual fixtures.</summary>
public class ChatHistoryTests
{
    const string Id = "6d4c6300-9f9c-4fe5-a76d-5091b6b1f11a";

    static SessionRecord Ended(string id = "r1", string? provider = null, string? model = null) => new()
    {
        Id = id, ClaudeSessionId = Id, Cwd = @"D:\Project", Name = "Chat", Lifecycle = "ended",
        Launch = new LaunchOptions { Provider = provider, Model = model },
    };

    [Fact]
    public void TheModelIsKeptOnlyOnTheSameRoute()
    {
        Assert.Equal("gpt-5.4", ChatHistory.ModelForProviderChange("chatgpt", "chatgpt", "gpt-5.4"));
        Assert.Equal("gpt-5.4", ChatHistory.ModelForProviderChange(null, "chatgpt", "gpt-5.4"));
        Assert.Null(ChatHistory.ModelForProviderChange("chatgpt", "anthropic", "gpt-5.4"));
    }

    [Fact]
    public void SelectProviderSetsTheNextStartsRoute()
    {
        var state = new AppState();
        var record = Ended(provider: "chatgpt", model: "gpt-5.4");
        ChatHistory.SelectProvider(state, record, "anthropic");
        Assert.Equal("anthropic", record.Launch.Provider);
        Assert.Null(record.Launch.Model);
        Assert.Same(record, state.Sessions["r1"]);
        record.Launch.Model = "kept";
        ChatHistory.SelectProvider(state, record, "chatgpt", "", modelGiven: true);
        Assert.Null(record.Launch.Model);
        ChatHistory.SelectProvider(state, record, "chatgpt", "gpt-5.5", modelGiven: true);
        Assert.Equal("gpt-5.5", record.Launch.Model);
        Assert.True(ChatHistory.ValidModel(""));
        Assert.False(ChatHistory.ValidModel("a\nb"));
        Assert.False(ChatHistory.ValidModel(new string('m', 301)));
    }

    [Fact]
    public void TheModelEditorStartsFromTheCommonRoute()
    {
        Assert.Equal(("anthropic", "opus"), ChatHistory.ModelEditOf([Ended("a", "anthropic", "opus"), Ended("b", "anthropic", "opus")]));
        Assert.Equal(("anthropic", null), ChatHistory.ModelEditOf([Ended("a", "anthropic", "opus"), Ended("b", "anthropic", "sonnet")]));
        Assert.Equal(("chatgpt", null), ChatHistory.ModelEditOf([Ended("a", "anthropic", "opus"), Ended("b", null, "opus")]));
    }

    [Fact]
    public void RetiringRemovesTheRecordsTheirTilesAndTheWindowsPinAndRestoringBringsAnEndedRecordBack()
    {
        var state = new AppState();
        var record = Ended();
        state.Sessions[record.Id] = record;
        var window = WindowState.Empty("w");
        window.Layout = new TileNode { SessionId = record.Id };
        state.Windows = [window];
        state.PinnedChats = [SidebarChats.ChatKey(Id), SidebarChats.ChatKey(Id, "Ubuntu")];
        ChatHistory.AddArchive(state, record, "Chat", @"D:\a\x.zip", "ab", @"D:\a\op");
        ChatHistory.WithoutHistory(state, Id);
        Assert.Empty(state.Sessions);
        Assert.Null(state.Windows[0].Layout);
        Assert.Equal([SidebarChats.ChatKey(Id, "Ubuntu")], state.PinnedChats);
        var entry = Assert.Single(state.Archives!);
        Assert.Equal(("chatgpt", @"D:\Project", @"D:\a\op"), (entry.Provider, entry.Cwd, entry.Operation));

        var restored = ChatHistory.Restored(state, entry, "new", 42);
        Assert.Empty(state.Archives!);
        Assert.Equal(("ended", Id, "Chat", "chatgpt", 42.0), (restored.Lifecycle, restored.ClaudeSessionId, restored.Name, restored.Launch.Provider, restored.LastActiveAt));
        Assert.Same(restored, state.Sessions["new"]);
    }

    [Fact]
    public void ThePreparedPlanBecomesTheRetireJob()
    {
        var outcome = JsonNode.Parse("""{"ok":true,"id":"6d4c6300-9f9c-4fe5-a76d-5091b6b1f11a","plan":"p","sha256":"s","operation":"D:\\op","files":3,"bytes":1234}""")!.AsObject();
        var prepared = PreparedHistory.From(outcome)!;
        Assert.Equal((3L, 1234L, @"D:\op"), (prepared.Files, prepared.Bytes, prepared.Operation));
        Assert.Null(PreparedHistory.From(JsonNode.Parse("""{"ok":false,"id":"x","error":"no"}""")!.AsObject()));
        var job = ChatHistory.RetireJob(prepared, "archive", Ended(provider: "anthropic"), "Chat");
        Assert.Equal("archive", (string?)job["mode"]);
        Assert.Equal("p", (string?)job["prepared"]!["plan"]);
        Assert.Equal("anthropic", (string?)job["metadata"]!["provider"]);
        Assert.Equal(Id, (string?)job["metadata"]!["id"]);

        var (message, detail, button) = ChatHistory.Confirmation("archive", [("Chat", Ended(), prepared)]);
        Assert.Equal("Archive “Chat”?", message);
        Assert.Equal("Archive", button);
        Assert.Contains("3 exact chat files (1,234 bytes).", detail);
        Assert.Contains("verified restorable ZIP", detail);
        (message, detail, button) = ChatHistory.Confirmation("delete", [("A", Ended(), prepared), ("B", Ended(), null)]);
        Assert.Equal("Permanently delete 2 selected chats?", message);
        Assert.Equal("Delete permanently", button);
        Assert.Contains("No Claude history: only the Reframe entry is removed.", detail);
        Assert.Contains("This cannot be undone.", detail);
    }

    [Fact]
    public void TheBatchReportCountsEachOutcome()
    {
        List<ChatBatchEntry> results = [new("a", "A", "completed"), new("b", "B", "skipped", ChatHistory.EndFirst), new("c", "C", "failed", "boom")];
        Assert.Equal("Working on selected chats… 1 completed · 1 skipped · 1 failed", ChatHistory.Summary(results, true));
        Assert.Equal("Selected chat action finished. 1 completed · 1 skipped · 1 failed", ChatHistory.Summary(results, false));
        Assert.Equal("A: completed", ChatHistory.Line(results[0]));
        Assert.Equal("C: failed — boom", ChatHistory.Line(results[2]));
    }

    [Fact]
    public void ClaudeHistoryIsAnyFileTheHelperWouldPlan()
    {
        var home = Path.Combine(Path.GetTempPath(), "reframe-shell-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "projects", "D--Project"));
            Assert.False(ChatHistory.HasClaudeHistory(home, Id));
            File.WriteAllText(Path.Combine(home, "projects", "D--Project", Id + ".jsonl"), "");
            Assert.True(ChatHistory.HasClaudeHistory(home, Id));
            Assert.False(ChatHistory.HasClaudeHistory(Path.Combine(home, "missing"), Id));
        }
        finally
        {
            Directory.Delete(home, true);
        }
        Assert.Equal(@"E:\Archives", ChatHistory.ArchiveRoot(@"E:\Archives", @"F:\Other", @"C:\Profile"));
        Assert.Equal(@"F:\Other", ChatHistory.ArchiveRoot(null, @"F:\Other", @"C:\Profile"));
        Assert.Throws<InvalidOperationException>(() => ChatHistory.ArchiveRoot(null, null, @"C:\Profile"));
    }

    [Fact]
    public void ClicksSelectOpenAndExtend()
    {
        string[] order = ["a", "b", "c", "d"];
        Assert.Equal(("b", "b", true), Flat(SidebarView.Click(order, ["a"], "a", "b", false, false)));
        Assert.Equal(("a,c", "c", false), Flat(SidebarView.Click(order, ["a"], "a", "c", false, true)));
        Assert.Equal(("c", "a", false), Flat(SidebarView.Click(order, ["a", "c"], "a", "a", false, true)));
        Assert.Equal(("b,c,d", "d", false), Flat(SidebarView.Click(order, ["d"], "d", "b", true, false)));
        Assert.Equal(("b", "b", true), Flat(SidebarView.Click(order, [], null, "b", true, false)));
    }

    static (string, string?, bool) Flat((List<string> Selected, string? Anchor, bool Open) r) => (string.Join(",", r.Selected), r.Anchor, r.Open);

    [Fact]
    public void TheSelectedChatsMenuOffersTheBatchActions()
    {
        var items = ContextMenus.ListMenu(2, false, ContextMenus.BatchChatActions(false));
        var provider = items.Single(i => i.Label == "Model provider");
        Assert.Equal(StateValues.Providers.Select(p => "batchProvider:" + p), provider.Submenu!.Select(i => i.Action!));
        Assert.Equal("Open 2 sessions", items[0].Label);
        Assert.Contains(items, i => i.Action == "batchModel" && i.Label == "Change model…" && i.Enabled);
        Assert.Contains(items, i => i.Action == "batchArchive" && i.Label == "Archive selected…");
        Assert.Contains(items, i => i.Action == "batchDelete" && i.Label == "Delete selected…");
        Assert.All(ContextMenus.BatchChatActions(true).Where(i => i.Action is not null), i => Assert.False(i.Enabled));
    }

    [Fact]
    public async Task TheListHasTheRunningDistributionsChatsAndTheStoppedOnesNotes()
    {
        var state = new AppState();
        state.Archives = [new ArchiveEntry { Id = "z", Label = "Old", Cwd = @"D:\Project", Provider = "chatgpt", Archive = @"D:\a.zip", Sha256 = "s", Operation = @"D:\op" }];
        var wsl = new WslSource("Ubuntu", new MemoryTranscriptSource([new TranscriptItem(Id, 5, "/home/ann/shop", "Linux chat")]), @"\\wsl.localhost");
        var list = await SessionListBuilder.BuildAsync(new ListSources
        {
            State = state, StatusOf = _ => "ended", Running = [], Transcripts = new MemoryTranscriptSource([]), Wsl = [wsl], WslStopped = ["Debian"],
        }, "", SessionListBuilder.Page);
        var found = Assert.Single(list.Found);
        Assert.Equal(("Ubuntu", @"\\wsl.localhost\Ubuntu\home\ann\shop"), (found.Distro, found.Cwd));
        Assert.Equal(["Debian"], list.WslStopped);
        Assert.Equal("z", Assert.Single(list.Archives).Id);
        var view = SidebarView.Build(list, 10);
        Assert.Equal(["Debian"], view.WslStopped);
        Assert.Single(view.Archives);
        Assert.Equal("Debian (WSL) is not running: its sessions show up here once it runs.", SidebarView.WslStoppedText("Debian"));
    }

    [Fact]
    public void TheHistoryAndWslFixturesParse()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tools", "visual", "fixtures"))) root = Path.GetDirectoryName(root)!;
        var fixtures = Path.Combine(root, "tools", "visual", "fixtures");
        var history = VisualFixture.Load(Path.Combine(fixtures, "history.json"), @"C:\Users\ann", 1e12);
        Assert.Equal(["Bulk 1: tidy the release scripts", "Bulk 2: profile the search endpoint"], history.Select);
        var wsl = VisualFixture.Load(Path.Combine(fixtures, "wsl.json"), @"C:\Users\ann", 1e12);
        Assert.Equal("Ubuntu", Assert.Single(wsl.Wsl).Distro);
        Assert.Equal(["Debian"], wsl.WslStopped);
        Assert.True(wsl.State.Preferences.LookInWsl);
        Assert.Equal(@"C:\Users\ann\projects\webshop", Assert.Single(wsl.State.Archives!).Cwd);
    }
}
