using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>A claude running outside reframe, as Claude Code's session registry has it.</summary>
/// <summary>A claude running outside reframe, as Claude Code's session registry has it; <c>NameSource</c> "user" for a
/// name the user gave it.</summary>
public sealed record RunningClaude(int Pid, string SessionId, string? Cwd, string? Name, double? StartedAt = null, string? NameSource = null);

/// <summary>A project folder's sessions as <c>transcripts.list</c> groups them (<c>folders[].locations</c>).</summary>
public sealed record TranscriptLocation(string Cwd, IReadOnlyList<string> SessionIds);

/// <summary>One page of <c>transcripts.list</c>: the items, whether there are more, and every listed session's project.</summary>
public sealed record TranscriptPage(IReadOnlyList<TranscriptItem> Items, bool HasMore, IReadOnlyList<TranscriptLocation> Folders);

/// <summary>Claude Code's transcripts (on Windows): the host's <c>transcripts</c> service, or a fixture.</summary>
public interface ITranscriptSource
{
    Task<TranscriptPage> ListAsync(string query, int limit, IReadOnlyCollection<string> exclude, string? folder);
    /// <summary>One past session (<c>info</c>), or null.</summary>
    Task<TranscriptItem?> FindAsync(string sessionId);
    /// <summary>Every transcript's modification time (<c>times</c>).</summary>
    Task<IReadOnlyDictionary<string, double>> TimesAsync();
}

/// <summary>What the session list is made of: reframe's state, how its sessions are doing, what runs outside reframe
/// and the transcripts.</summary>
public sealed class ListSources
{
    public required AppState State { get; init; }
    /// <summary>A session's status (sessionView's status): the host's, else ended.</summary>
    public required Func<SessionRecord, string> StatusOf { get; init; }
    /// <summary>Whether reframe itself runs the session (sessions.keepsRunning).</summary>
    public Func<string, bool> RunsInReframe { get; init; } = _ => false;
    /// <summary>A session's label (labelOf); by default sessionLabel of its name, Claude Code's name, its folder.</summary>
    public Func<SessionRecord, string>? LabelOf { get; init; }
    public IReadOnlyList<RunningClaude> Running { get; init; } = [];
    public required ITranscriptSource Transcripts { get; init; }
    /// <summary>Claude Code in each running WSL distribution that has it (claudeSources' WSL sources).</summary>
    public IReadOnlyList<WslSource> Wsl { get; init; } = [];
    /// <summary>The distributions Claude Code was found in before that do not run now (wslStopped).</summary>
    public IReadOnlyList<string> WslStopped { get; init; } = [];
}

/// <summary>A running WSL distribution's Claude Code: its transcripts (Linux folders) and the share its folders are
/// reached under from Windows (<c>wslWindowsPath</c>).</summary>
public sealed record WslSource(string Distro, ITranscriptSource Transcripts, string Share)
{
    /// <summary>wslWindowsPath (src/shared/wsl.ts): a Linux path as Windows reaches it under the share.</summary>
    public static string WindowsPath(string distro, string linuxPath, string share) =>
        string.Join('\\', new[] { share.TrimEnd('\\', '/'), distro }.Concat(linuxPath.Split('/').Where(p => p != "")));

    public string WindowsPath(string linuxPath) => WindowsPath(Distro, linuxPath, Share);
}

/// <summary>
/// The session list as main assembles it (<c>sessionList</c> in src/main/app.ts) for Claude Code on Windows: reframe's
/// sessions (not hidden), claudes running elsewhere, past sessions from the transcripts, pinned chats, and the
/// projects all of them make; past sessions in running WSL distributions (<see cref="ListSources.Wsl"/>) too. Claudes
/// running in WSL are not listed yet (the shell has no WSL registry reader; stage S12).
/// </summary>
public static class SessionListBuilder
{
    public const int Page = 20;

    public static async Task<SessionList> BuildAsync(ListSources sources, string query, int limit, string? folder = null)
    {
        var state = sources.State;
        var q = query.Trim().ToLowerInvariant();
        bool Matches(params string?[] fields) => q == "" || fields.Any(f => f is not null && f.ToLowerInvariant().Contains(q));
        bool InFolder(string cwd) => folder is null || Workspaces.WorkspaceKey(cwd) == Workspaces.WorkspaceKey(folder);
        var hidden = HiddenThreads.HiddenIds(state.HiddenThreads);
        var records = HiddenThreads.WithoutHidden(state.Sessions.Values, hidden, r => r.ClaudeSessionId);
        var recordedProviders = new Dictionary<string, string>();
        foreach (var record in records) recordedProviders[SidebarChats.ChatKey(record.ClaudeSessionId)] = record.Launch.Provider ?? "chatgpt";
        string ProviderOf(string id) => recordedProviders.GetValueOrDefault(SidebarChats.ChatKey(id), "chatgpt");
        string? KnownProvider(string id) => recordedProviders.TryGetValue(SidebarChats.ChatKey(id), out var p) ? p : null;

        var outside = sources.Running;
        var ownIds = records.Select(r => r.ClaudeSessionId).ToHashSet();
        var resumedElsewhere = outside.Select(r => r.SessionId).ToHashSet();
        var usedOutside = new Dictionary<string, double>();
        if (outside.Count > 0)
        {
            var times = await sources.Transcripts.TimesAsync();
            foreach (var entry in outside)
                if ((times.TryGetValue(entry.SessionId, out var t) ? t : entry.StartedAt) is { } time && time != 0) usedOutside[entry.SessionId] = time;
        }

        var labelOf = sources.LabelOf ?? (r => StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd));
        var app = records.Select(record =>
            {
                var window = state.Windows.FirstOrDefault(w => Layouts.WindowSessions(w).Contains(record.Id));
                var elsewhere = resumedElsewhere.Contains(record.ClaudeSessionId) && !sources.RunsInReframe(record.Id);
                return new ListItem
                {
                    Kind = ListItemKind.App,
                    ClaudeSessionId = record.ClaudeSessionId,
                    Provider = record.Launch.Provider ?? "chatgpt",
                    Id = record.Id,
                    Label = labelOf(record),
                    Cwd = record.Cwd,
                    Status = sources.StatusOf(record),
                    Lifecycle = record.Lifecycle,
                    WindowId = window?.Id,
                    WindowNumber = window is null ? null : state.Windows.IndexOf(window) + 1,
                    LastActiveAt = Math.Max(record.LastActiveAt, elsewhere ? usedOutside.GetValueOrDefault(record.ClaudeSessionId) : 0),
                    Elsewhere = elsewhere,
                };
            })
            .Where(item => Matches(item.Label, item.Cwd) && InFolder(item.Cwd))
            // Sessions in a window first, then the rest; most recently used first within each.
            .OrderBy(item => item.WindowId is null ? 1 : 0).ThenByDescending(item => item.LastActiveAt)
            .ToList();

        var running = outside
            .Where(e => e.Cwd is not null && !ownIds.Contains(e.SessionId) && !hidden.Contains(e.SessionId))
            .Select(e => new ListItem
            {
                Kind = ListItemKind.Running,
                Pid = e.Pid,
                ClaudeSessionId = e.SessionId,
                Cwd = e.Cwd!,
                Name = e.Name,
                Provider = KnownProvider(e.SessionId),
                LastActiveAt = usedOutside.GetValueOrDefault(e.SessionId),
            })
            .Where(item => Matches(item.Name, item.Cwd) && InFolder(item.Cwd))
            .ToList();

        var exclude = ownIds.Concat(running.Select(r => r.ClaudeSessionId)).Concat(hidden).ToHashSet();
        var page = await sources.Transcripts.ListAsync(query, limit, exclude, folder);
        var found = page.Items.Where(i => !string.IsNullOrEmpty(i.Cwd))
            .Select(i => new ListItem
            {
                Kind = ListItemKind.Found,
                ClaudeSessionId = i.SessionId,
                Cwd = i.Cwd!,
                Title = i.Title,
                ModifiedAt = i.ModifiedAt,
                Provider = KnownProvider(i.SessionId),
            })
            .OrderByDescending(i => i.ModifiedAt)
            .ToList();
        var wslPages = new List<(WslSource Source, TranscriptPage Page)>();
        foreach (var source in sources.Wsl)
        {
            var wslPage = await source.Transcripts.ListAsync(query, limit, exclude, null);
            wslPages.Add((source, wslPage));
            found.AddRange(wslPage.Items.Where(i => !string.IsNullOrEmpty(i.Cwd))
                .Select(i => new ListItem
                {
                    Kind = ListItemKind.Found,
                    ClaudeSessionId = i.SessionId,
                    Cwd = source.WindowsPath(i.Cwd!),
                    Title = i.Title,
                    ModifiedAt = i.ModifiedAt,
                    Distro = source.Distro,
                    Provider = KnownProvider(i.SessionId),
                })
                .Where(i => InFolder(i.Cwd)));
        }
        found = found.OrderByDescending(i => i.ModifiedAt).ToList();
        var hasMore = found.Count > limit || page.HasMore || wslPages.Any(p => p.Page.HasMore);

        var pinnedItems = new List<ListItem>();
        var knownKeys = records.Select(r => SidebarChats.ChatKey(r.ClaudeSessionId))
            .Concat(outside.Select(e => SidebarChats.ChatKey(e.SessionId))).ToHashSet();
        foreach (var key in state.PinnedChats ?? [])
        {
            if (SidebarChats.ParseChatKey(key) is not { } pin || hidden.Contains(pin.Id) || knownKeys.Contains(key) ||
                found.Any(item => SidebarChats.ChatKey(item.ClaudeSessionId, item.Distro) == key))
                continue;
            var wsl = pin.Distro is null ? null : sources.Wsl.FirstOrDefault(w => w.Distro.ToLowerInvariant() == pin.Distro);
            if (pin.Distro is not null && wsl is null) continue; // its distribution does not run
            var info = await (wsl?.Transcripts ?? sources.Transcripts).FindAsync(pin.Id);
            if (string.IsNullOrEmpty(info?.Cwd)) continue;
            var pinCwd = wsl?.WindowsPath(info.Cwd) ?? info.Cwd;
            if (!Matches(info.Title, pinCwd) || !InFolder(pinCwd)) continue;
            pinnedItems.Add(new ListItem
            {
                Kind = ListItemKind.Found, ClaudeSessionId = pin.Id, Cwd = pinCwd, Title = info.Title, ModifiedAt = info.ModifiedAt,
                Distro = wsl?.Distro,
            });
        }

        var projects = Workspaces.Build(
            records.Where(r => r.Cwd != "").Select(r => (r.Cwd, r.ClaudeSessionId, (string?)(r.Launch.Provider ?? "chatgpt")))
                .Concat(outside.Where(e => e.Cwd is not null).Select(e => (e.Cwd!, e.SessionId, (string?)ProviderOf(e.SessionId))))
                .Concat(page.Folders.SelectMany(l => l.SessionIds.Select(id => (l.Cwd, id, (string?)ProviderOf(id)))))
                .Concat(wslPages.SelectMany(p => p.Page.Folders.SelectMany(l =>
                    l.SessionIds.Select(id => (p.Source.WindowsPath(l.Cwd), id, (string?)ProviderOf(id)))))));
        var expanded = (state.ExpandedProjects ?? []).Select(Workspaces.ExpandedWorkspaceKey).Distinct().ToList();

        return new SessionList
        {
            PinnedChats = state.PinnedChats ?? [],
            PinnedItems = pinnedItems,
            ExpandedProjects = expanded,
            FolderWorkspaces = projects,
            App = app,
            Running = running,
            Found = found.Take(limit).ToList(),
            HasMore = hasMore,
            Archives = state.Archives ?? [],
            WslStopped = [.. sources.WslStopped],
        };
    }
}

/// <summary>Sessions in a layout and a window (leaves, dockSessions, windowSessions in src/shared).</summary>
public static class Layouts
{
    public static IEnumerable<string> Leaves(LayoutNode? node) => node switch
    {
        TileNode tile => [tile.SessionId],
        GroupNode group => Leaves(group.Child),
        SplitNode split => split.Children.SelectMany(Leaves),
        _ => [],
    };

    public static IEnumerable<string> DockSessions(DockEntry entry) => entry switch
    {
        DockTile tile => [tile.SessionId],
        DockGroup group => Leaves(group.Node),
        _ => [],
    };

    public static List<string> WindowSessions(WindowState window) =>
        Leaves(window.Layout).Concat(window.Dock.SelectMany(DockSessions)).ToList();
}

/// <summary>The host's <c>transcripts</c> service for one Claude Code home.</summary>
public sealed class HostTranscriptSource(HostConnection connection, string home) : ITranscriptSource
{
    bool Offered => connection.Welcome.Services.ContainsKey("transcripts");

    public async Task<TranscriptPage> ListAsync(string query, int limit, IReadOnlyCollection<string> exclude, string? folder)
    {
        if (!Offered) return new([], false, []);
        var parameters = new JsonObject
        {
            ["home"] = home, ["limit"] = limit, ["exclude"] = new JsonArray(exclude.Select(e => (JsonNode)e!).ToArray()),
        };
        if (query.Trim() != "") parameters["query"] = query;
        if (folder is not null) parameters["folder"] = folder;
        var result = await connection.CallAsync("transcripts", "list", parameters);
        var items = (result?["items"]?.AsArray() ?? []).Where(n => n is not null).Select(n => Item(n!)).ToList();
        var folders = (result?["folders"]?.AsArray() ?? []).Where(n => n is not null)
            .SelectMany(f => f!["locations"]?.AsArray() ?? [])
            .Where(l => l is not null && (string?)l["cwd"] is not null)
            .Select(l => new TranscriptLocation((string)l!["cwd"]!,
                (l["sessionIds"]?.AsArray() ?? []).Select(s => (string?)s).OfType<string>().ToList()))
            .ToList();
        return new(items, (bool?)result?["hasMore"] ?? false, folders);
    }

    public async Task<TranscriptItem?> FindAsync(string sessionId)
    {
        if (!Offered) return null;
        var result = await connection.CallAsync("transcripts", "info", new JsonObject { ["home"] = home, ["sessionId"] = sessionId });
        return result is JsonObject item ? Item(item) : null;
    }

    public async Task<IReadOnlyDictionary<string, double>> TimesAsync()
    {
        if (!Offered) return new Dictionary<string, double>();
        var result = await connection.CallAsync("transcripts", "times", new JsonObject { ["home"] = home });
        return result is JsonObject times
            ? times.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => (double)p.Value!)
            : new Dictionary<string, double>();
    }

    static TranscriptItem Item(JsonNode n) =>
        new((string)n["sessionId"]!, (double?)n["modifiedAt"] ?? 0, (string?)n["cwd"], (string?)n["title"]);
}

/// <summary>Transcripts held in memory, listed as the host's TranscriptIndex.list does (rust/reframe-transcripts):
/// newest first without the excluded; every remaining session with a folder counts for <c>folders</c>, before the
/// folder and query filters; the query must be in the title or the folder.</summary>
public sealed class MemoryTranscriptSource(IEnumerable<TranscriptItem> items) : ITranscriptSource
{
    readonly List<TranscriptItem> items = items.OrderByDescending(i => i.ModifiedAt).ToList();

    public Task<TranscriptPage> ListAsync(string query, int limit, IReadOnlyCollection<string> exclude, string? folder)
    {
        var q = query.Trim().ToLowerInvariant();
        var key = string.IsNullOrEmpty(folder) ? null : Workspaces.WorkspaceKey(folder);
        var listed = new List<TranscriptItem>();
        var chats = new List<(string Cwd, string Id)>();
        var hasMore = false;
        foreach (var item in items.Where(i => !exclude.Contains(i.SessionId)))
        {
            var cwd = string.IsNullOrEmpty(item.Cwd) ? null : item.Cwd;
            if (cwd is not null) chats.Add((cwd, item.SessionId));
            if (key is not null && (cwd is null || Workspaces.WorkspaceKey(cwd) != key)) continue;
            if (q != "" && !(item.Title?.ToLowerInvariant().Contains(q) ?? false) && !(item.Cwd?.ToLowerInvariant().Contains(q) ?? false)) continue;
            if (listed.Count >= limit) hasMore = true;
            else listed.Add(item);
        }
        var folders = chats.GroupBy(c => (Workspaces.FolderKey(c.Cwd)))
            .Select(g => new TranscriptLocation(g.First().Cwd, g.Select(c => c.Id).Distinct().ToList())).ToList();
        return Task.FromResult(new TranscriptPage(listed, hasMore, folders));
    }

    public Task<TranscriptItem?> FindAsync(string sessionId) => Task.FromResult(items.FirstOrDefault(i => i.SessionId == sessionId));

    public Task<IReadOnlyDictionary<string, double>> TimesAsync() =>
        Task.FromResult<IReadOnlyDictionary<string, double>>(items.ToDictionary(i => i.SessionId, i => i.ModifiedAt));
}
