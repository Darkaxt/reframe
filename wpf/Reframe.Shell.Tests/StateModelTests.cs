using System.Text;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>
/// The C# state model against the TS parser: every input in test/state-fixtures/inputs must give what the TS parser
/// gave for it (test/state-fixtures/expected, written and kept current by src/main/state/fixtures.test.ts).
/// </summary>
public class StateModelTests
{
    static readonly string Fixtures = FindFixtures();

    static string FindFixtures()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test", "state-fixtures");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("test/state-fixtures not found above " + AppContext.BaseDirectory);
    }

    public static IEnumerable<object[]> Inputs() =>
        Directory.GetFiles(Path.Combine(Fixtures, "inputs"), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>The text as Node reads it (UTF-8, a byte order mark kept).</summary>
    static string ReadInput(string name) =>
        new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(Fixtures, "inputs", name)));

    [Fact]
    public void Every_input_has_a_ts_result()
    {
        var inputs = Inputs().Select(i => (string)i[0]).Order().ToList();
        var expected = Directory.GetFiles(Path.Combine(Fixtures, "expected"), "*.json").Select(f => Path.GetFileName(f)).Order().ToList();
        Assert.True(inputs.Count > 10);
        Assert.Equal(inputs, expected);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Parses_like_the_ts_parser(string name)
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected", name)))!;
        var result = StateParser.Parse(ReadInput(name));
        JsonObject actual = result.Kind switch
        {
            StateParseKind.State => new JsonObject { ["result"] = "state", ["state"] = StateJson.Write(result.State!, keepUnknown: false) },
            StateParseKind.Newer => new JsonObject { ["result"] = "newer" },
            _ => new JsonObject { ["result"] = "rejected" },
        };
        Assert.True(JsonEquivalent.Equal(expected, actual),
            $"{name}: {result.Reason}\nexpected {expected.ToJsonString()}\nactual   {actual.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Round_trips_every_accepted_input_with_its_unknown_keys(string name)
    {
        var result = StateParser.Parse(ReadInput(name));
        if (result.Kind != StateParseKind.State) return;
        var written = StateJson.ToText(result.State!);
        var again = StateParser.Parse(written);
        Assert.Equal(StateParseKind.State, again.Kind);
        Assert.True(JsonEquivalent.Equal(JsonNode.Parse(written), StateJson.Write(again.State!)), name);
    }

    [Fact]
    public void Keeps_keys_it_does_not_know()
    {
        var state = StateParser.Parse(ReadInput("unknown-fields.json")).State!;
        Assert.True(state.Extra["futureTopLevel"]!["nested"]![2]!["deep"]!.GetValue<bool>());
        Assert.Equal(7, (int)state.Sessions["s1"].Extra["futureSession"]!);
        Assert.Equal("x", (string)state.Sessions["s1"].Launch.Extra["futureLaunch"]!);
        Assert.Equal(3, (int)((TileNode)state.Windows[0].Layout!).Extra["zoom"]!);
        Assert.Equal(2, (int)state.Windows[0].Bounds!.Extra["dpi"]!);
        Assert.Equal("z", (string)state.Preferences.Extra["futurePref"]!);
        Assert.True(state.Preferences.ProviderDefaults!.Extra.ContainsKey("other"));
        Assert.Equal("x", (string)state.HiddenThreads[0].Extra["why"]!);
        var written = StateJson.Write(state);
        Assert.Equal("y", (string)written["windows"]![0]!["futureWindow"]!);
        Assert.Equal(1, (int)written["preferences"]!["providerDefaults"]!["anthropic"]!["more"]!);
        Assert.False(StateJson.Write(state, keepUnknown: false).ContainsKey("futureTopLevel"));
    }

    [Fact]
    public void Exposes_what_the_session_list_needs()
    {
        var state = StateParser.Parse(ReadInput("full-current.json")).State!;
        Assert.Equal(new[] { "c1", "c9" }, state.PinnedChats!);
        Assert.Equal(new[] { @"C:\work\one" }, state.ExpandedProjects!);
        Assert.Equal(new[] { "h1", "h2" }, state.HiddenThreads.Select(h => h.ClaudeSessionId));
        Assert.Equal("Ubuntu", state.HiddenThreads[0].Distro);
        var s1 = state.Sessions["s1"];
        Assert.Equal(1700000500000.5, s1.LastActiveAt);
        Assert.Equal("Build fix", s1.Name);
        Assert.True(s1.HasClaudeName);
        Assert.Null(s1.ClaudeName);
        Assert.False(state.Sessions["s3"].HasClaudeName);
        Assert.Equal("Build fix", StateValues.SessionLabel(s1.Name, "title", s1.Cwd));
        Assert.Equal("three", StateValues.SessionLabel(null, " ", state.Sessions["s3"].Cwd));
        Assert.Equal(StateValues.SidebarWidthMax, StateValues.ClampSidebarWidth(state.Preferences.SidebarWidth));
        Assert.Equal(StateValues.SidebarWidthNormal, StateValues.ClampSidebarWidth(null));
        Assert.Equal(new[] { "Ubuntu" }, state.WslDistros!);
        Assert.Equal(2, state.Archives!.Count);
        var layout = Assert.IsType<SplitNode>(state.Windows[0].Layout);
        Assert.IsType<GroupNode>(layout.Children[1]);
        Assert.IsType<DockGroup>(state.Windows[0].Dock[2]);
    }

    [Fact]
    public void Gives_older_files_the_defaults()
    {
        var state = StateParser.Parse(ReadInput("old-no-preferences-no-hidden.json")).State!;
        Assert.Empty(state.HiddenThreads);
        Assert.Equal("system", state.Preferences.Language);
        Assert.Equal(13, state.Preferences.FontSize);
        Assert.True(state.Preferences.SnapToGrid);
        Assert.Equal("node", state.Preferences.PtyHost);
        var workspaces = StateParser.Parse(ReadInput("old-saved-workspaces.json")).State!;
        Assert.False(StateJson.Write(workspaces, keepUnknown: false).ContainsKey("workspaces"));
        Assert.False(StateJson.Write(workspaces, keepUnknown: false)["windows"]![0]!.AsObject().ContainsKey("workspaceId"));
    }

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reframe-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Loads_from_the_file_then_the_backup_then_the_defaults_and_writes_nothing()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "state.json");
            var none = await StateLoader.LoadFileAsync(file);
            Assert.Equal(StateSource.Default, none.Source);
            Assert.Null(none.Rejected);
            Assert.Single(none.State.Windows);

            File.WriteAllText(file + ".bak", ReadInput("e2e-backup-minimal.json"));
            File.WriteAllText(file, ReadInput("e2e-corrupt.json"));
            var backup = await StateLoader.LoadFileAsync(file);
            Assert.Equal(StateSource.Backup, backup.Source);
            Assert.StartsWith("not JSON", backup.Rejected);
            Assert.Equal(new[] { @"C:\work\project" }, backup.State.RecentFolders);

            File.WriteAllText(file, ReadInput("full-current.json"));
            Assert.Equal(StateSource.File, (await StateLoader.LoadFileAsync(file)).Source);

            File.WriteAllText(file, ReadInput("e2e-newer-version.json"));
            Assert.Equal(StateSource.Newer, (await StateLoader.LoadFileAsync(file)).Source);

            File.WriteAllText(file, ReadInput("invalid-enum.json"));
            File.WriteAllText(file + ".bak", ReadInput("e2e-newer-version.json"));
            var newerBackup = await StateLoader.LoadFileAsync(file);
            Assert.Equal(StateSource.Newer, newerBackup.Source);
            Assert.NotNull(newerBackup.Rejected);
            Assert.Equal(new[] { "state.json", "state.json.bak" }, Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Loads_through_the_hosts_state_service()
    {
        await using var host = new FakeHost();
        var file = Path.Combine(host.ProfileDir, "state.json");
        host.StateFiles[file] = ReadInput("e2e-corrupt.json");
        host.StateFiles[file + ".bak"] = ReadInput("e2e-stale-registry.json");
        var attached = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false);
        await using var connection = attached.Connection!;
        var services = new HostServices(connection);
        var loaded = await StateLoader.LoadAsync(services, file);
        Assert.Equal(StateSource.Backup, loaded.Source);
        Assert.Equal(7L, loaded.Revision);
        Assert.Equal("own-session", loaded.State.Sessions["a"].ClaudeSessionId);
        Assert.False(File.Exists(file));
        var reads = host.Requests.Where(r => (string?)r["type"] == "svc").Select(r => (string)r["params"]!["file"]!).ToList();
        Assert.Equal(new[] { file, file + ".bak" }, reads);
        attached.Lock?.Dispose();
    }
}

/// <summary>JSON equality as JavaScript sees it: object keys in any order, numbers by value.</summary>
static class JsonEquivalent
{
    public static bool Equal(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var v) && Equal(p.Value, v)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => Equal(p.First, p.Second)),
        (JsonValue x, JsonValue y) => Value(x).Equals(Value(y)),
        _ => false,
    };

    static object Value(JsonValue v) => v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.Number => double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture),
        System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        var kind => kind,
    };
}
