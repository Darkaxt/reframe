using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>src/shared/new-session.test.ts ported, with app.ts createSession/restartSession, Tile.tsx's card and the
/// spawnPlan request the shell sends (held to test/spawn-plan-fixtures' inputs).</summary>
public class NewSessionTests
{
    [Fact]
    public void A_thread_asked_for_by_another_gets_the_providers_defaults_and_who_opened_it()
    {
        var preferences = new Preferences
        {
            DefaultEffort = "high",
            ProviderDefaults = new ProviderDefaultsSet { Chatgpt = new ProviderDefaults { Model = "gpt-6.1-sol", PermissionMode = "auto" } },
        };
        var opener = new SessionRecord { Id = "s1", Cwd = @"D:\Projects\app", Name = "Lead", Launch = new LaunchOptions { Provider = "anthropic" } };
        var request = NewSession.OpenedBy(JsonNode.Parse("""{ "name": " Docs ", "prompt": "Review the API", "provider": "openai" }"""), opener, preferences);
        Assert.Equal(new NewSessionRequest(@"D:\Projects\app", "chatgpt", "Docs", Model: "gpt-6.1-sol", PermissionMode: "auto",
            InitialPrompt: "[opened by the Reframe thread \"Lead\" on Claude (Anthropic); answer it with SendMessage to \"Lead\"]\nReview the API", OpenedBy: "s1"),
            request! with { Follows = null });
        Assert.Equal(new[] { "permissionMode", "model", "effort" }, request.Follows!);
        var claude = NewSession.OpenedBy(JsonNode.Parse("""{ "name": "B", "prompt": "x", "provider": "claude", "folder": "E:\\w", "model": "opus" }"""),
            new SessionRecord { Cwd = @"D:\a" }, preferences)!;
        Assert.Equal(("anthropic", @"E:\w", "opus", "high"), (claude.Provider, claude.Cwd, claude.Model, claude.Effort));
        Assert.StartsWith("[opened by a Reframe thread on OpenAI;", claude.InitialPrompt);
        Assert.Equal("low", NewSession.OpenedBy(JsonNode.Parse("""{ "name": "B", "prompt": "x", "provider": "openai", "reasoning_effort": "low" }"""), opener, preferences)!.Effort);
        Assert.NotEqual("bogus", NewSession.OpenedBy(JsonNode.Parse("""{ "name": "B", "prompt": "x", "provider": "openai", "effort": "bogus" }"""), opener, preferences)!.Effort);
        Assert.Null(NewSession.OpenedBy(JsonNode.Parse("""{ "name": "B" }"""), opener, preferences));
        Assert.Null(NewSession.OpenedBy(JsonNode.Parse("""{ "name": "B", "prompt": "x" }"""), opener, preferences));
        Assert.Null(NewSession.OpenedBy(null, opener, preferences));
    }

    [Fact]
    public void Falls_back_to_Settings_the_default_model_only_for_the_ChatGPT_route()
    {
        var preferences = new Preferences { DefaultModel = "opus", DefaultEffort = "high", DefaultPermissionMode = "plan" };
        Assert.Equal(new DialogDefaults("opus", "high", "plan", []), NewSession.DefaultsFor(preferences, "chatgpt") with { AddDirs = [] });
        Assert.Empty(NewSession.DefaultsFor(preferences, "chatgpt").AddDirs);
        Assert.Null(NewSession.DefaultsFor(preferences, "anthropic").Model);
    }

    [Fact]
    public void Prefers_what_was_used_last_for_that_provider_including_a_deliberate_Default_effort()
    {
        var preferences = new Preferences
        {
            DefaultEffort = "high",
            ProviderDefaults = new ProviderDefaultsSet { Anthropic = new ProviderDefaults { Model = "sonnet", AddDirs = [@"D:\lib"] } },
        };
        var anthropic = NewSession.DefaultsFor(preferences, "anthropic");
        Assert.Equal("sonnet", anthropic.Model);
        Assert.Null(anthropic.Effort);
        Assert.Equal([@"D:\lib"], anthropic.AddDirs);
        Assert.Equal("high", NewSession.DefaultsFor(preferences, "chatgpt").Effort);
    }

    [Fact]
    public void Remembers_only_set_choices()
    {
        var remembered = NewSession.RememberedDefaults(new LaunchOptions { Model = "opus", AddDirs = [] });
        Assert.Equal("opus", remembered.Model);
        Assert.Null(remembered.Effort);
        Assert.Null(remembered.PermissionMode);
        Assert.Null(remembered.AddDirs);
    }

    [Fact]
    public void Reads_one_folder_per_line()
    {
        Assert.Equal([@"D:\a", @"E:\b"], NewSession.ParseAddDirs(" D:\\a \r\n\nD:\\a\nE:\\b "));
    }

    [Fact]
    public void Keeps_absolute_folders_only_and_bounds_them()
    {
        Assert.Equal([@"D:\a", @"\\srv\share", "/home/me"], NewSession.CleanAddDirs([@"D:\a", "relative", @"\\srv\share", "/home/me", "C:/x\u0000"]));
        Assert.Null(NewSession.CleanAddDirs(null));
        Assert.Null(NewSession.CleanAddDirs(["relative"]));
        Assert.Equal(20, NewSession.CleanAddDirs(Enumerable.Range(0, 30).Select(i => $@"D:\d{i}"))!.Count);
    }

    [Fact]
    public void Accepts_multi_line_prompts_but_not_control_characters_or_empty_ones()
    {
        Assert.Equal("fix\nthe tests", NewSession.CleanPrompt("  fix\nthe tests\t "));
        Assert.Null(NewSession.CleanPrompt("bad\u0007"));
        Assert.Null(NewSession.CleanPrompt("   "));
        Assert.Null(NewSession.CleanPrompt(new string('x', 20_001)));
    }

    [Fact]
    public void A_new_session_takes_the_request_s_choices_and_Settings_effort_unless_Default_was_picked()
    {
        var preferences = new Preferences { DefaultEffort = "high" };
        var request = new NewSessionRequest(@" D:\shop ", "anthropic", " Shop ", true, " wt ", " opus ", null, "plan",
            [@"D:\lib", "relative"], " go\n ");
        var record = NewSession.CreateRecord(request, preferences, "s-1", "c-1", 1000);
        Assert.Equal(@"D:\shop", record.Cwd);
        Assert.Equal("Shop", record.Name);
        Assert.Equal("open", record.Lifecycle);
        Assert.Equal((1000d, 1000d), (record.CreatedAt, record.LastActiveAt));
        var launch = record.Launch;
        Assert.Equal(("anthropic", "opus", "high", "plan"), (launch.Provider, launch.Model, launch.Effort, launch.PermissionMode));
        Assert.Equal((true, "wt"), (launch.Worktree, launch.WorktreeName));
        Assert.Equal([@"D:\lib"], launch.AddDirs);
        Assert.Equal("go", launch.InitialPrompt);

        var plain = NewSession.CreateRecord(new NewSessionRequest(@"D:\shop", "other", Effort: "", PermissionMode: "nope", WorktreeName: "x"), preferences, "s-2", "c-2", 0);
        Assert.Equal("chatgpt", plain.Launch.Provider);
        Assert.Null(plain.Launch.Effort);
        Assert.Null(plain.Launch.PermissionMode);
        Assert.Null(plain.Launch.Worktree);
        Assert.Null(plain.Launch.WorktreeName);
        Assert.Null(plain.Name);
    }

    [Fact]
    public void Creating_puts_the_folder_first_among_eight_recent_ones_and_keeps_the_provider_s_defaults()
    {
        var state = AppState.CreateDefault();
        state.RecentFolders = [@"d:\SHOP", .. Enumerable.Range(0, 9).Select(i => $@"D:\f{i}")];
        var record = NewSession.CreateRecord(new NewSessionRequest(@"D:\shop", "anthropic", Model: "sonnet"), state.Preferences, "s-1", "c-1", 0);
        NewSession.Remember(state, record);
        Assert.Same(record, state.Sessions["s-1"]);
        Assert.Equal(8, state.RecentFolders.Count);
        Assert.Equal([@"D:\shop", @"D:\f0"], state.RecentFolders.Take(2));
        Assert.Equal("sonnet", state.Preferences.ProviderDefaults!.Anthropic!.Model);
        Assert.Null(state.Preferences.ProviderDefaults.Chatgpt);
        // Once there are defaults, a session with other choices leaves them alone.
        NewSession.Remember(state, NewSession.CreateRecord(new NewSessionRequest(@"D:\shop", "anthropic", Model: "haiku"), state.Preferences, "s-2", "c-2", 0));
        Assert.Equal("sonnet", state.Preferences.ProviderDefaults.Anthropic.Model);
    }

    [Fact]
    public void The_choices_left_at_the_defaults_follow_a_change_of_them_the_others_stay()
    {
        var state = AppState.CreateDefault();
        state.Preferences.ProviderDefaults = new ProviderDefaultsSet { Chatgpt = new ProviderDefaults { Model = "gpt-a", Effort = "low", PermissionMode = "bypassPermissions" } };
        var defaults = NewSession.DefaultsFor(state.Preferences, "chatgpt");
        Assert.Equal(new[] { "model", "effort", "permissionMode" }, NewSession.Following(defaults, "gpt-a", "low", "bypassPermissions")!);
        Assert.Equal(new[] { "effort", "permissionMode" }, NewSession.Following(defaults, "gpt-b", null, "bypassPermissions")!);
        var follower = NewSession.CreateRecord(new NewSessionRequest(@"D:/fa", "chatgpt", Model: "gpt-a", Effort: "low", PermissionMode: "bypassPermissions",
            Follows: NewSession.Following(defaults, "gpt-a", "low", "bypassPermissions")), state.Preferences, "f", "cf", 0);
        var own = NewSession.CreateRecord(new NewSessionRequest(@"D:/fb", "chatgpt", Model: "gpt-own", Effort: "high", PermissionMode: "bypassPermissions",
            Follows: NewSession.Following(defaults, "gpt-own", "high", "bypassPermissions")), state.Preferences, "o", "co", 0);
        state.Sessions["f"] = follower;
        state.Sessions["o"] = own;

        Assert.True(Settings.Apply(state.Preferences, new PreferencesPatch { DefaultsProvider = "chatgpt", DefaultModel = "gpt-new", DefaultPermissionMode = "plan" }));
        var changed = NewSession.FollowDefaults(state, "chatgpt", defaults, NewSession.DefaultsFor(state.Preferences, "chatgpt"));
        Assert.Equal(("gpt-new", "low", "plan"), (follower.Launch.Model, follower.Launch.Effort, follower.Launch.PermissionMode));
        Assert.Equal(("gpt-own", "high", "plan"), (own.Launch.Model, own.Launch.Effort, own.Launch.PermissionMode));
        Assert.Equal(new[] { "f", "o" }, changed.Select(c => c.Record.Id).OrderBy(x => x).ToArray());
        Assert.Equal("gpt-a", changed.First(c => c.Record.Id == "f").OldModel);

        // A manual change ends following for that choice only.
        Assert.True(ShellActions.SetPermissionMode(state, "f", "auto"));
        Assert.Equal(new[] { "model", "effort" }, follower.Launch.FollowsDefaults!);
        var before = NewSession.DefaultsFor(state.Preferences, "chatgpt");
        Settings.Apply(state.Preferences, new PreferencesPatch { DefaultsProvider = "chatgpt", DefaultPermissionMode = "acceptEdits", DefaultEffort = "xhigh" });
        NewSession.FollowDefaults(state, "chatgpt", before, NewSession.DefaultsFor(state.Preferences, "chatgpt"));
        Assert.Equal(("auto", "xhigh"), (follower.Launch.PermissionMode, follower.Launch.Effort));
        // The other provider's threads are left alone, and the setting is kept with the thread.
        Assert.Null(state.Preferences.ProviderDefaults.Anthropic);
        var back = StateParser.Parse(StateJson.ToText(state)).State!.Sessions["f"].Launch.FollowsDefaults;
        Assert.Equal(new[] { "model", "effort" }, back!);
    }

    [Fact]
    public void Settings_for_one_provider_start_from_its_current_defaults()
    {
        var preferences = new Preferences { DefaultModel = "opus", DefaultEffort = "medium", DefaultPermissionMode = "plan" };
        Assert.True(Settings.Apply(preferences, new PreferencesPatch { DefaultsProvider = "anthropic", DefaultEffort = "high" }));
        Assert.Equal(new DialogDefaults(null, "high", "plan", []), NewSession.DefaultsFor(preferences, "anthropic") with { AddDirs = [] });
        Assert.Equal("medium", preferences.DefaultEffort);
        Assert.Equal("medium", NewSession.DefaultsFor(preferences, "chatgpt").Effort);
    }

    [Fact]
    public void A_fresh_start_is_a_new_unused_conversation_a_resume_keeps_it()
    {
        var record = new SessionRecord { Id = "s", ClaudeSessionId = "old", Lifecycle = "ended", Used = true, LastActiveAt = 5 };
        NewSession.Restart(record, false, "new", 9);
        Assert.Equal(("old", true, 5d, "open"), (record.ClaudeSessionId, record.Used, record.LastActiveAt, record.Lifecycle));
        NewSession.Restart(record, true, "new", 9);
        Assert.Equal(("new", false, 9d), (record.ClaudeSessionId, record.Used, record.LastActiveAt));
    }

    [Fact]
    public void The_recent_folders_leave_out_the_other_provider_s_workspace()
    {
        var roots = NewSession.WorkspaceRoots(@"C:\Users\ann\Documents");
        string[] recent = [@"C:\Users\ann\Documents\Anthropic\shop", @"C:\Users\ann\Documents\ChatGPT\shop", @"D:\work"];
        Assert.Equal([@"C:\Users\ann\Documents\ChatGPT\shop", @"D:\work"], NewSession.ProviderRecent(recent, roots, "chatgpt"));
        Assert.Equal([@"C:\Users\ann\Documents\Anthropic\shop", @"D:\work"], NewSession.ProviderRecent(recent, roots, "anthropic"));
        Assert.Equal("shop", NewSession.FolderName(@"C:\x\shop\ "));
        // Gemini threads have a workspace of their own.
        Assert.Equal([@"D:\work"], NewSession.ProviderRecent(recent, roots, "gemini"));
        Assert.Equal(@"C:\Users\ann\Documents\Gemini", roots["gemini"]);
    }

    [Fact]
    public void A_gemini_thread_runs_agy_with_a_gemini_model()
    {
        var preferences = new Preferences { DefaultModel = "gpt-6.1-sol" };
        Assert.Equal("gemini-4-neon", NewSession.DefaultsFor(preferences, "gemini").Model);
        Settings.Apply(preferences, new PreferencesPatch { DefaultsProvider = "gemini", DefaultModel = "gemini-4-hydrogen-xhigh" });
        // A default from before the effort picked the variant: its family.
        Assert.Equal("gemini-4-hydrogen", NewSession.DefaultsFor(preferences, "gemini").Model);
        Assert.Equal(("gemini-4-neon-xhigh", "gemini-4-argon-high", "gemini-3.8-flash-high", "gpt-6.1-sol"),
            (ModelNames.AgyModel("gemini-4-neon", "max"), ModelNames.AgyModel("gemini-4-argon", "xhigh"), ModelNames.AgyModel("gemini-3.8-flash", null), ModelNames.AgyModel("gpt-6.1-sol", "low")));
        Assert.Null(ModelSync.Changed("gemini-4-neon", "gemini-4-neon-high"));
        Assert.Equal("gemini-4-argon-low", ModelSync.Changed("gemini-4-neon", "gemini-4-argon-low"));
        Assert.Equal("gpt-6.1-sol", NewSession.DefaultsFor(preferences, "chatgpt").Model);
        Assert.Equal("gemini", Takeover.ProviderOfModel("gemini-4-neon-xhigh"));
        Assert.Equal(("chatgpt", "anthropic"), (StateValues.ClientOf("gemini"), StateValues.ClientOf("anthropic")));
        var launch = new LaunchOptions { Provider = "gemini" };
        Takeover.Resumed(launch, new LaunchOptions { Provider = "chatgpt", Model = "gemini-4-neon-xhigh" });
        Assert.Equal("gemini", launch.Provider);
        var opener = new SessionRecord { Id = "s1", Cwd = @"D:\p", Name = "Lead", Launch = new LaunchOptions { Provider = "gemini" } };
        var request = NewSession.OpenedBy(JsonNode.Parse("""{ "name": "G", "prompt": "x", "provider": "gemini" }"""), opener, preferences)!;
        Assert.Equal(("gemini", "gemini-4-hydrogen"), (request.Provider, request.Model));
        Assert.StartsWith("[opened by the Reframe thread \"Lead\" on Gemini (Google);", request.InitialPrompt);
    }

    [Fact]
    public void The_tile_card_offers_what_Tile_tsx_offers_for_each_error()
    {
        var ended = TileCard.For(null, null);
        Assert.Equal(("Session ended", "Resume", false, "Close"), (ended.Text, ended.Primary, ended.StartFresh, ended.Close));
        var missing = TileCard.For("folderMissing", null);
        Assert.Equal(("The session folder no longer exists.", null, false), (missing.Text, missing.Primary, missing.StartFresh));
        var lost = TileCard.For("transcriptMissing", null);
        Assert.Equal((null, true), (lost.Primary, lost.StartFresh));
        Assert.Equal(("Take over", false), (TileCard.For("runningElsewhere", null).Primary, TileCard.For("runningElsewhere", null).StartFresh));
        Assert.Equal(("Take over", false), (TileCard.For("olderHost", null).Primary, TileCard.For("olderHost", null).StartFresh));
        Assert.Equal(("Resume", false), (TileCard.For("hostCrashed", null).Primary, TileCard.For("hostCrashed", null).StartFresh));
        Assert.Equal(("Resume", true), (TileCard.For("spawnFailed", null).Primary, TileCard.For("spawnFailed", null).StartFresh));
        Assert.EndsWith("and it runs there.", TileCard.For("claudeNotFound", null).Text);
        Assert.Equal("Claude Code was not found in Ubuntu (WSL). Install it there: claude.com/claude-code", TileCard.For("claudeNotFound", "Ubuntu").Text);
    }

    [Fact]
    public void The_spawn_plan_request_carries_the_session_as_the_fixtures_inputs_do()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "test", "spawn-plan-fixtures", "inputs", "new-full-options.json")))!["input"]!;
        var record = new SessionRecord
        {
            Id = "s-1", ClaudeSessionId = "11111111-1111-4111-8111-111111111111", Cwd = @"D:\Projects\shop", Name = "Shop", HasClaudeName = true,
            Launch = new LaunchOptions
            {
                Provider = "chatgpt", Model = "fable", Effort = "max", PermissionMode = "bypassPermissions", Worktree = true, WorktreeName = "feature-x",
                AddDirs = [@"D:\Shared", @"E:\Other dir"], InitialPrompt = "-start with this \"quoted\" %PATH%",
            },
        };
        var env = fixture["env"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);
        var input = SpawnPlans.Input(record, "new", null, env, (string)fixture["claudeHome"]!, (string)fixture["settingsDir"]!,
            new SpawnPlans.Hooks(52100, "<hook-token>"), null, null, false, null);
        foreach (var key in new[] { "session", "mode", "env", "claudeHome", "settingsDir", "hooks" })
            Assert.True(JsonNode.DeepEquals(fixture[key], input[key]), key);
        Assert.Null(input["adoption"]);
        Assert.Null(input["wsl"]);
    }

    [Fact]
    public void A_plan_answer_becomes_the_host_s_spawn()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "test", "spawn-plan-fixtures", "expected", "adopted.json")));
        var plan = SpawnPlans.Parse(expected);
        Assert.True(plan.Ok);
        Assert.Equal("new", plan.Mode);
        Assert.EndsWith(@"hook-settings\s-1.json", plan.SettingsFile);
        var spawn = SpawnPlans.Spawn("s-1", 3, plan);
        Assert.Equal(("spawn", "s-1", 3, 120, 32), ((string?)spawn["type"], (string?)spawn["sessionId"], (int)spawn["instance"]!, (int)spawn["cols"]!, (int)spawn["rows"]!));
        Assert.True(JsonNode.DeepEquals(expected!["args"], spawn["args"]));
        Assert.True(JsonNode.DeepEquals(expected["env"], spawn["env"]));
        Assert.True(JsonNode.DeepEquals(expected["meta"], spawn["meta"]));

        var failed = SpawnPlans.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(TokenTestsRoot.Root, "test", "spawn-plan-fixtures", "expected", "folder-missing.json"))));
        Assert.Equal((false, "folderMissing"), (failed.Ok, failed.Error));
    }

    [Fact]
    public void At_start_resumes_open_sessions_in_a_window_the_host_lost_attaches_the_rest_and_ends_those_in_none()
    {
        SessionRecord R(string id, string lifecycle = "open") => new() { Id = id, ClaudeSessionId = id, Cwd = @"C:\p", Lifecycle = lifecycle };
        var sessions = new[] { R("lost"), R("alive"), R("docked-lost"), R("windowless"), R("ended", "ended"), R("synthetic") };
        var inWindow = new HashSet<string> { "lost", "alive", "docked-lost", "ended", "synthetic" };
        var hostRuns = new HashSet<string> { "alive", "synthetic" };
        var (resume, ended) = NewSession.AtStart(sessions, inWindow.Contains, hostRuns.Contains, new HashSet<string> { "synthetic" });
        Assert.Equal(["lost", "docked-lost"], resume);
        Assert.Equal(["windowless"], ended);
    }
}
