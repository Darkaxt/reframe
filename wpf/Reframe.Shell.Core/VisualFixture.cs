using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>
/// A visual parity fixture (tools/visual/fixtures/*.json): the same sessions, past chats and outside claudes as a test
/// of e2e/visual-reference.spec.ts sets up for Electron, so that <c>--diag-capture</c> renders the same session list
/// (through <see cref="SessionListBuilder"/>, as live data would go). Times are relative to the moment it is loaded;
/// folders are <c>&lt;home&gt;\projects\&lt;project&gt;</c> as in the Electron fixture.
/// </summary>
public sealed class VisualFixture
{
    public required AppState State { get; init; }
    /// <summary>Each session's status, by reframe session id (demo-claude's scenes set them in Electron).</summary>
    public required Dictionary<string, string> Statuses { get; init; }
    public required List<RunningClaude> Running { get; init; }
    public required MemoryTranscriptSource Transcripts { get; init; }
    /// <summary>The sessions the capture host runs (their tiles show terminals; the others the ended card), and the
    /// thread a single window shows.</summary>
    public required List<string> Hosted { get; init; }
    public string? Selected { get; init; }
    public required double Now { get; init; }
    /// <summary>The quota the Coordinator reported (Electron's capture sets it through its test hook), none by default.</summary>
    public List<ProviderQuota> Quota { get; init; } = [];

    public ListSources Sources() => new()
    {
        State = State,
        StatusOf = r => Statuses.GetValueOrDefault(r.Id, "ended"),
        Running = Running,
        Transcripts = Transcripts,
    };

    public static VisualFixture Load(string file, string home, double now) => Parse(File.ReadAllText(file), home, now);

    public static VisualFixture Parse(string json, string home, double now)
    {
        const double Second = 1000, Hour = 3600 * Second;
        var root = JsonNode.Parse(json)!.AsObject();
        string Folder(JsonNode n) => Path.Combine(home, "projects", (string)n["project"]!);
        var state = new AppState { Preferences = new Preferences { Language = "en" } };
        var statuses = new Dictionary<string, string>();
        var tiles = new List<LayoutNode>();
        var dock = new List<DockEntry>();
        foreach (var s in root["sessions"]?.AsArray() ?? [])
        {
            var id = (string)s!["id"]!;
            var ended = (bool?)s["ended"] ?? false;
            state.Sessions[id] = new SessionRecord
            {
                Id = id,
                ClaudeSessionId = (string)s["claudeSessionId"]!,
                Cwd = Folder(s),
                Name = (string?)s["name"],
                Lifecycle = ended ? "ended" : "open",
                CreatedAt = now - 5 * Hour,
                LastActiveAt = now - ((double?)s["usedSecondsAgo"] ?? 0) * Second,
                Used = true,
            };
            statuses[id] = (string?)s["status"] ?? (ended ? "ended" : "idle");
            if ((bool?)s["docked"] == true) dock.Add(new DockTile { SessionId = id });
            else if ((bool?)s["inWindow"] ?? true) tiles.Add(new TileNode { SessionId = id });
        }
        var window = WindowState.Empty("visual-window");
        window.SidebarOpen = true;
        window.ViewMode = (string?)root["viewMode"];
        window.Dock = dock;
        // The window as state.json has it (Electron's capture builds the same layout), else the open sessions in a row.
        static System.Text.Json.JsonElement Element(JsonNode n) => System.Text.Json.JsonDocument.Parse(n.ToJsonString()).RootElement;
        if (root["dock"] is JsonArray dockJson) window.Dock = [.. dockJson.Select(d => StateParser.DockOf(Element(d!)))];
        window.Layout = root["layout"] is JsonNode layoutJson ? StateParser.LayoutOf(Element(layoutJson)) : tiles.Count switch
        {
            0 => null,
            1 => tiles[0],
            _ => new SplitNode { Id = "visual-split", Dir = "row", Children = tiles, Sizes = tiles.Select(_ => 100.0 / tiles.Count).ToList() },
        };
        state.Windows = [window];

        var transcripts = new List<TranscriptItem>();
        var pinned = new List<string>();
        foreach (var p in root["past"]?.AsArray() ?? [])
        {
            var id = (string)p!["id"]!;
            transcripts.Add(new TranscriptItem(id, now - ((double?)p["hoursAgo"] ?? 0) * Hour, Folder(p), (string?)p["title"]));
            if ((bool?)p["pinned"] == true) pinned.Add(SidebarChats.ChatKey(id));
        }
        if (pinned.Count > 0) state.PinnedChats = pinned;
        foreach (var h in root["hidden"]?.AsArray() ?? [])
            state.HiddenThreads.Add(new HiddenThread
            {
                ClaudeSessionId = (string)h!["id"]!, Label = (string?)h["title"] ?? "", Cwd = Folder(h),
                HiddenAt = now - ((double?)h["hoursAgo"] ?? 0) * Hour,
            });
        var quota = (root["quota"]?.AsArray() ?? []).Select(q => new ProviderQuota((string)q!["source"]!,
            (q["windows"]?.AsArray() ?? []).Select(w => new QuotaWindow((string)w!["id"]!, (double?)w["minutes"],
                (double?)w["usedPercent"] ?? 0, w["resetsInHours"] is null ? null : now + (double)w["resetsInHours"]! * Hour)).ToList(), now)).ToList();

        var running = (root["running"]?.AsArray() ?? []).Select(r => new RunningClaude(
            (int?)r!["pid"] ?? 0, (string)r["id"]!, Folder(r), (string?)r["name"])).ToList();

        return new VisualFixture
        {
            State = state,
            Statuses = statuses,
            Running = running,
            Transcripts = new MemoryTranscriptSource(transcripts),
            Hosted = (root["hosted"]?.AsArray() ?? []).Select(t => (string)t!).ToList(),
            Selected = (string?)root["selected"],
            Now = now,
            Quota = quota,
        };
    }
}
