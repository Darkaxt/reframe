using System.Text;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>An in-memory host <c>state</c> service with the protocol's revision rule (src/main/state/files.ts).</summary>
sealed class MemoryStateFiles : IStateFiles
{
    long last;
    public readonly Dictionary<string, string?> Files = [];
    readonly Dictionary<string, long> revisions = [];
    public int Writes, Conflicts;
    public bool LastBackup;

    public Task<(string? Content, long Revision)> ReadAsync(string file)
    {
        if (!revisions.ContainsKey(file)) revisions[file] = ++last;
        return Task.FromResult((Files.GetValueOrDefault(file), revisions[file]));
    }

    public Task<long> WriteAsync(string file, string content, long revision, bool backup)
    {
        if (!revisions.TryGetValue(file, out var current) || current != revision)
        {
            Conflicts++;
            throw new HostServiceException("conflict", $"{file} is not at revision {revision}");
        }
        Writes++;
        LastBackup = backup;
        if (backup && Files.GetValueOrDefault(file) is { } old) Files[file + ".bak"] = old;
        Files[file] = content;
        return Task.FromResult(revisions[file] = ++last);
    }

    /// <summary>Someone else wrote the file (reframe itself, say): a new revision.</summary>
    public void ChangeElsewhere(string file, string content)
    {
        Files[file] = content;
        revisions[file] = ++last;
    }
}

public class StateStoreTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("reframe-store-").FullName;
    string File_ => Path.Combine(dir, "state.json");

    public void Dispose() => Directory.Delete(dir, true);

    static AppState Parse(string text) => StateParser.Parse(text).State!;

    [Fact]
    public async Task Writes_through_the_host_with_its_revision_and_keeps_unknown_fields()
    {
        var host = new MemoryStateFiles();
        host.Files[File_] = """{ "version": 1, "sessions": {}, "windows": [], "recentFolders": [], "preferences": { "nameFromFolder": false, "language": "en", "fontSize": 13, "snapToGrid": true, "checkForUpdates": true, "lookInWsl": false, "closeWindow": "ask", "gpuAcceleration": false, "ptyHost": "rust", "future": 1 }, "hiddenThreads": [], "later": { "x": [1, 2] } }""";
        var store = new StateStore(File_, host);
        var loaded = await store.LoadAsync();
        Assert.Equal(StateSource.File, loaded.Source);
        ShellActions.TogglePin(loaded.State, SidebarChats.ChatKey("c1"));
        store.Save(loaded.State);
        await store.FlushAsync();
        Assert.Equal(1, host.Writes);
        Assert.True(host.LastBackup);
        var written = JsonNode.Parse(host.Files[File_]!)!;
        Assert.Equal(1, (int)written["later"]!["x"]![0]!);
        Assert.Equal(1, (int)written["preferences"]!["future"]!);
        Assert.Equal(SidebarChats.ChatKey("c1"), (string)written["pinnedChats"]![0]!);
        Assert.Equal(StateParseKind.State, StateParser.Parse(host.Files[File_]!).Kind);
        Assert.False(File.Exists(File_)); // nothing written here
    }

    [Fact]
    public async Task Reads_again_and_writes_the_newest_after_a_conflict()
    {
        var host = new MemoryStateFiles();
        host.Files[File_] = StateJson.ToText(AppState.CreateDefault());
        var store = new StateStore(File_, host);
        var state = (await store.LoadAsync()).State;
        host.ChangeElsewhere(File_, StateJson.ToText(AppState.CreateDefault()));
        ShellActions.TogglePin(state, "k");
        store.Save(state);
        await store.FlushAsync();
        Assert.Equal(1, host.Conflicts);
        Assert.Equal(1, host.Writes);
        Assert.Equal(["k"], Parse(host.Files[File_]!).PinnedChats!);
    }

    [Fact]
    public async Task Without_a_host_writes_here_atomically_and_keeps_the_good_file_as_backup()
    {
        File.WriteAllText(File_, StateJson.ToText(AppState.CreateDefault()));
        var store = new StateStore(File_, (IStateFiles?)null);
        var state = (await store.LoadAsync()).State;
        state.RecentFolders.Add(@"C:\a");
        store.Save(state);
        await store.FlushAsync();
        Assert.Equal([@"C:\a"], Parse(File.ReadAllText(File_)).RecentFolders);
        Assert.Empty(Parse(File.ReadAllText(File_ + ".bak")).RecentFolders);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public async Task Coalesces_soon_saves_into_one_write_and_flush_writes_them()
    {
        var host = new MemoryStateFiles();
        var store = new StateStore(File_, host);
        var state = (await store.LoadAsync()).State;
        for (var i = 0; i < 5; i++)
        {
            state.RecentFolders.Add($"C:\\{i}");
            store.SaveSoon(state, delayMs: 10_000);
        }
        Assert.Equal(0, host.Writes);
        await store.FlushAsync();
        Assert.Equal(1, host.Writes);
        Assert.Equal(5, Parse(host.Files[File_]!).RecentFolders.Count);
    }

    [Fact]
    public async Task Sets_an_unusable_file_aside_and_loads_the_backup()
    {
        File.WriteAllText(File_, "{ not json");
        File.WriteAllText(File_ + ".bak", StateJson.ToText(AppState.CreateDefault()));
        var store = new StateStore(File_, (IStateFiles?)null);
        var loaded = await store.LoadAsync();
        Assert.Equal(StateSource.Backup, loaded.Source);
        Assert.NotNull(loaded.Rejected);
        Assert.False(File.Exists(File_));
        var aside = Assert.Single(Directory.GetFiles(dir, "state.json.rejected-*"));
        Assert.Matches(@"state\.json\.rejected-\d{8}-\d{6}$", aside);
    }

    [Fact]
    public void Keeps_five_set_aside_files_numbered_within_a_second()
    {
        var store = new StateStore(File_, (IStateFiles?)null);
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 7; i++)
        {
            File.WriteAllText(File_, "x");
            store.SetAside(now);
        }
        var names = Directory.GetFiles(dir, "state.json.rejected-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(StateStore.KeepRejected, names.Count);
        Assert.Equal("state.json.rejected-20261113-120000-007", names[^1]);
    }

    [Fact]
    public async Task A_newer_reframes_file_is_never_written()
    {
        var host = new MemoryStateFiles();
        host.Files[File_] = """{ "version": 99 }""";
        var store = new StateStore(File_, host);
        var loaded = await store.LoadAsync();
        Assert.Equal(StateSource.Newer, loaded.Source);
        store.Save(loaded.State);
        await store.FlushAsync();
        Assert.Equal(0, host.Writes);
        Assert.True(store.ReadOnly);
    }

    [Fact]
    public async Task Sessions_the_shell_only_lists_are_not_written()
    {
        var host = new MemoryStateFiles();
        var store = new StateStore(File_, host);
        var state = (await store.LoadAsync()).State;
        state.Sessions["listed"] = new SessionRecord { Id = "listed", ClaudeSessionId = "c", Cwd = @"C:\x" };
        store.Save(state, new HashSet<string> { "listed" });
        await store.FlushAsync();
        Assert.Empty(Parse(host.Files[File_]!).Sessions);
    }
}

public class ShellActionTests
{
    static AppState Full() => StateParser.Parse(ShellWrittenFixtures.Input("full-current.json")).State!;

    [Fact]
    public void Rename_keeps_claude_code_s_name_once_and_empty_goes_back_to_automatic()
    {
        var state = Full();
        Assert.True(ShellActions.Rename(state, "s1", "  New name  "));
        Assert.Equal("New name", state.Sessions["s1"].Name);
        Assert.True(state.Sessions["s1"].HasClaudeName);
        var s2 = state.Sessions["s2"];
        ShellActions.Rename(state, "s2", "");
        Assert.Null(s2.Name);
        Assert.Equal("Named", s2.ClaudeName);
        ShellActions.Rename(state, "s2", new string('x', 150));
        Assert.Equal(100, s2.Name!.Length);
        Assert.False(ShellActions.Rename(state, "nope", "x"));
    }

    [Fact]
    public void Pin_appends_once_and_unpin_removes()
    {
        var state = Full();
        var before = state.PinnedChats!.ToList();
        ShellActions.TogglePin(state, "new");
        Assert.Equal([.. before, "new"], state.PinnedChats);
        ShellActions.TogglePin(state, "new");
        Assert.Equal(before, state.PinnedChats);
    }

    [Fact]
    public void Takeover_exclusions_round_trip_and_are_removed()
    {
        var state = Full();
        Assert.Null(state.TakeoverExclusions);
        Assert.DoesNotContain("takeoverExclusions", StateStore.Serialize(state));
        Assert.True(ShellActions.Exclude(state, [new TakeoverExclusion { ClaudeSessionId = "e1", Label = "One", Cwd = @"C:\e", ExcludedAt = 5 }]));
        ShellActions.Exclude(state, [new TakeoverExclusion { ClaudeSessionId = "e2" }, new TakeoverExclusion { ClaudeSessionId = "e1", Label = "Again" }]);
        var back = StateParser.Parse(StateStore.Serialize(state)).State!;
        Assert.Equal(["e2", "e1"], back.TakeoverExclusions!.Select(x => x.ClaudeSessionId));
        Assert.Equal("Again", back.TakeoverExclusions![^1].Label);
        Assert.True(ShellActions.Unexclude(back, ["e2"]));
        Assert.False(ShellActions.Unexclude(back, ["nope"]));
        Assert.True(ShellActions.Unexclude(back, null));
        Assert.Empty(back.TakeoverExclusions!);
    }

    [Fact]
    public void Hide_replaces_an_entry_and_restore_takes_them_back()
    {
        var state = Full();
        Assert.False(ShellActions.Hide(state, new HiddenThread { ClaudeSessionId = "x" }, busy: true));
        ShellActions.Hide(state, new HiddenThread { ClaudeSessionId = "h1", Label = "Again", Cwd = @"C:\h", HiddenAt = 9 });
        Assert.Equal(["h2", "h1"], state.HiddenThreads.Select(h => h.ClaudeSessionId));
        Assert.Equal("Again", state.HiddenThreads[^1].Label);
        Assert.True(ShellActions.Restore(state, ["h2"]));
        Assert.False(ShellActions.Restore(state, ["nope"]));
        Assert.True(ShellActions.Restore(state, null));
        Assert.Empty(state.HiddenThreads);
    }

    [Fact]
    public void End_marks_the_session_ended_and_takes_it_out_of_the_windows()
    {
        var state = Full();
        Assert.True(ShellActions.End(state, "s2"));
        Assert.Equal("ended", state.Sessions["s2"].Lifecycle);
        var json = StateJson.Write(state).ToJsonString();
        Assert.DoesNotContain("\"sessionId\":\"s2\"", json);
        // the group's split still has s3 and s4, sizes scaled to 100
        Assert.Contains("\"sessionId\":\"s3\"", json);
    }

    [Fact]
    public void Remove_forgets_only_a_session_in_no_window()
    {
        var state = Full();
        Assert.False(ShellActions.Remove(state, "s3"));
        ShellActions.End(state, "s3");
        Assert.True(ShellActions.Remove(state, "s3"));
        Assert.False(state.Sessions.ContainsKey("s3"));
    }

    [Fact]
    public void Removing_a_tile_normalizes_like_layout_ts()
    {
        var root = new SplitNode
        {
            Id = "a", Dir = "row", Sizes = [50, 50],
            Children = [new TileNode { SessionId = "x" }, new SplitNode { Id = "b", Dir = "col", Sizes = [30, 70], Children = [new TileNode { SessionId = "y" }, new TileNode { SessionId = "z" }] }],
        };
        var rest = Assert.IsType<SplitNode>(ShellActions.RemoveTile(root, "x"));
        Assert.Equal("col", rest.Dir);
        Assert.Equal([30.0, 70.0], rest.Sizes);
        Assert.IsType<TileNode>(ShellActions.RemoveTile(rest, "y"));
        Assert.Null(ShellActions.RemoveTile(new TileNode { SessionId = "q" }, "q"));
    }
}

public class ContextMenuTests
{
    static SessionRecord Record(string lifecycle = "ended", string? provider = null) =>
        new() { Id = "s", ClaudeSessionId = "c", Cwd = @"C:\p", Lifecycle = lifecycle, Launch = new LaunchOptions { Provider = provider } };

    static string Shape(IEnumerable<MenuSpec> items) => string.Join("|", items.Select(i =>
        i.Type == "separator" ? "-" : $"{i.Label}{(i.Enabled ? "" : "(off)")}{(i.Checked == true ? "*" : "")}{(i.Submenu is null ? "" : "[" + Shape(i.Submenu) + "]")}"));

    [Fact]
    public void List_menu_of_an_ended_session_of_reframe_s()
    {
        var chat = ContextMenus.ChatActions(new ChatMenuContext(ListItemKind.App, Record(), "k", false, true, false, true));
        Assert.Equal("Open|Rename|-|Pin chat|Hide from Reframe…|-|Model provider[ChatGPT*|Anthropic]|Change model…|Archive chat…|Delete chat…|-|Open in a new group|Open in a new group in a new window|-|Properties…",
            Shape(ContextMenus.ListMenu(1, true, chat)));
    }

    [Fact]
    public void A_running_session_must_be_ended_first()
    {
        var chat = ContextMenus.ChatActions(new ChatMenuContext(ListItemKind.App, Record("open", "anthropic"), "k", true, false, false, false));
        Assert.Equal("Open|Rename|-|Unpin chat|Hide from Reframe (end chat first)(off)|-|Model provider (end chat first)[ChatGPT(off)|Anthropic(off)*]|Change model…(off)|Archive chat (end chat first)(off)|Delete chat (end chat first)(off)|-|Open in a new group|Open in a new group in a new window|-|Properties…",
            Shape(ContextMenus.ListMenu(1, true, chat)));
    }

    [Fact]
    public void Running_elsewhere_and_wsl_chats_only_pin_and_hide()
    {
        var running = ContextMenus.ChatActions(new ChatMenuContext(ListItemKind.Running, null, SidebarChats.ChatKey("1c0f6f0e-4c1a-4a51-9a51-0c6a1d0e0a09"), false, true, false, true));
        Assert.Equal("Open|Take over…|-|Pin chat|Hide from Reframe…|-|Open in a new group|Open in a new group in a new window|-|Properties…", Shape(ContextMenus.ListMenu(1, false, running)));
        var wsl = ContextMenus.ChatActions(new ChatMenuContext(ListItemKind.Found, null, SidebarChats.ChatKey("1c0f6f0e-4c1a-4a51-9a51-0c6a1d0e0a09", "ubuntu"), false, true, true, true));
        Assert.Equal(3, wsl.Count);
    }

    [Fact]
    public void A_found_chat_has_no_provider_checked()
    {
        var found = ContextMenus.ChatActions(new ChatMenuContext(ListItemKind.Found, null, SidebarChats.ChatKey("1c0f6f0e-4c1a-4a51-9a51-0c6a1d0e0a09"), false, true, false, true));
        Assert.Equal("-|Pin chat|Hide from Reframe…|-|Model provider[ChatGPT|Anthropic]|Change model…|Archive chat…|Delete chat…", Shape(found));
    }

    [Fact]
    public void Tile_menu_and_its_json()
    {
        var menu = ContextMenus.TileMenu([], false, []);
        Assert.Equal("Rename|-|New group|Add to group(off)[]|Remove from group(off)|-|Properties…", Shape(menu));
        var json = MenuSpec.ToJson(menu).ToJsonString();
        Assert.Equal("""[{"type":"normal","label":"Rename","enabled":true},{"type":"separator"},{"type":"normal","label":"New group","enabled":true},{"type":"submenu","label":"Add to group","enabled":false,"submenu":[]},{"type":"normal","label":"Remove from group","enabled":false},{"type":"separator"},{"type":"normal","label":"Properties…","enabled":true}]""", json);
    }
}

/// <summary>
/// Files the shell writes (test/state-fixtures/shell-written): each action applied to a shared input, as
/// <c>written/&lt;name&gt;.json</c> (what goes to state.json) and <c>parsed/&lt;name&gt;.json</c> (what reframe's TS parser
/// must make of it: the model without unknown keys). src/main/state/shell-written.test.ts parses every written file with
/// Electron's parser and compares. Fails when a committed file is stale; REFRAME_UPDATE_SHELL_FIXTURES=1 writes them.
/// </summary>
public class ShellWrittenFixtures
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test", "state-fixtures");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("test/state-fixtures");
    }

    public static string Input(string name) => new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(Root, "inputs", name)));

    static readonly (string Name, string Input, Action<AppState> Act)[] Cases =
    [
        ("rename", "full-current.json", s => ShellActions.Rename(s, "s1", "Renamed “quoted” ä")),
        ("rename-automatic", "full-current.json", s => ShellActions.Rename(s, "s2", "")),
        ("pin", "full-current.json", s => ShellActions.TogglePin(s, SidebarChats.ChatKey("1c0f6f0e-4c1a-4a51-9a51-0c6a1d0e0a09"))),
        ("unpin", "full-current.json", s => ShellActions.TogglePin(s, "c1")),
        ("hide", "full-current.json", s => ShellActions.Hide(s, new HiddenThread { ClaudeSessionId = "c2", Label = "Named", Cwd = "/home/u/p", HiddenAt = 1700000600000 })),
        ("hide-wsl", "full-current.json", s => ShellActions.Hide(s, new HiddenThread { ClaudeSessionId = "c9", Label = "In WSL", Cwd = @"\\wsl$\Ubuntu\home\u", Distro = "Ubuntu", HiddenAt = 7 })),
        ("restore", "full-current.json", s => ShellActions.Restore(s, ["h1"])),
        ("restore-all", "full-current.json", s => ShellActions.Restore(s, null)),
        ("end", "full-current.json", s => ShellActions.End(s, "s2")),
        ("end-and-remove", "full-current.json", s => { ShellActions.End(s, "s3"); ShellActions.Remove(s, "s3"); }),
        ("rename-unknown-fields", "unknown-fields.json", s => ShellActions.Rename(s, s.Sessions.Keys.First(), "Kept")),
        ("defaults-pin", "empty-default-v1.json", s => ShellActions.TogglePin(s, SidebarChats.ChatKey("1c0f6f0e-4c1a-4a51-9a51-0c6a1d0e0a01"))),
    ];

    public static IEnumerable<object[]> Names() => Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Committed_files_are_what_the_shell_writes(string name)
    {
        var (_, input, act) = Cases.Single(c => c.Name == name);
        var state = StateParser.Parse(Input(input)).State!;
        act(state);
        var written = StateStore.Serialize(state) + "\n";
        var parsed = StateJson.Format(StateJson.Write(state, keepUnknown: false)) + "\n";
        var writtenFile = Path.Combine(Root, "shell-written", "written", name + ".json");
        var parsedFile = Path.Combine(Root, "shell-written", "parsed", name + ".json");
        if (Environment.GetEnvironmentVariable("REFRAME_UPDATE_SHELL_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(writtenFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(parsedFile)!);
            File.WriteAllText(writtenFile, written);
            File.WriteAllText(parsedFile, parsed);
        }
        Assert.Equal(written, File.ReadAllText(writtenFile).Replace("\r\n", "\n"));
        Assert.Equal(parsed, File.ReadAllText(parsedFile).Replace("\r\n", "\n"));
        // The shell reads back what it wrote.
        Assert.Equal(StateParseKind.State, StateParser.Parse(written).Kind);
    }

    [Fact]
    public void Every_committed_file_has_a_case()
    {
        var dir = Path.Combine(Root, "shell-written", "written");
        Assert.Equal(Cases.Select(c => c.Name + ".json").Order(), Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).Order());
    }
}
