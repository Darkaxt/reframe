using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>A session's status from the host's <c>sessions</c> service (<c>sessions.status</c> / <c>list</c>).
/// <c>Revision</c> is the host's status revision of the change (null from hosts before 0.10.1-claudegpt.25).</summary>
public sealed record SessionStatus(string SessionId, int Instance, string Status, string? Name, string? ClaudeSessionId,
    string? Cwd, string? Notify, long? Revision = null)
{
    public static SessionStatus From(JsonNode node)
    {
        var registry = node["registry"];
        var status = (string?)node["status"] ?? "starting";
        // Protocol version 2: idle with background work still running reads as "background" (sessionView's status).
        if (status == "idle" && (bool?)node["background"] == true) status = "background";
        return new SessionStatus((string)node["sessionId"]!, (int?)node["instance"] ?? 0, status,
            (string?)registry?["name"], (string?)node["claudeSessionId"] ?? (string?)registry?["sessionId"],
            (string?)registry?["cwd"], (string?)node["notify"], (long?)node["revision"]);
    }
}

public sealed record TranscriptItem(string SessionId, double ModifiedAt, string? Cwd, string? Title);

/// <summary>The v1 services the shell uses: <c>sessions</c>, <c>transcripts</c> and <c>state</c> (read only, for now).</summary>
public sealed class HostServices
{
    readonly HostConnection connection;
    readonly object gate = new();
    /// <summary>The newest revision applied per session, and the revision of the last list.</summary>
    readonly Dictionary<string, long> applied = [];
    long snapshot = -1;

    public HostServices(HostConnection connection)
    {
        this.connection = connection;
        connection.ServiceEvent += (service, name, data) =>
        {
            if (service == "sessions" && name == "status" && data is not null)
            {
                var status = SessionStatus.From(data);
                if (Accept(status, fromList: false)) StatusChanged?.Invoke(status);
            }
            else if (service == "monitor" && name == "warning" && MonitorWarning.From(data) is { } warning) WarningRaised?.Invoke(warning);
            else if (service == "monitor" && name == "cleared" && (string?)data?["id"] is { } cleared) WarningCleared?.Invoke(cleared);
        };
    }

    /// <summary>The <c>monitor</c> service's warning events (version 2), on the connection's reading thread.</summary>
    public event Action<MonitorWarning>? WarningRaised;
    public event Action<string>? WarningCleared;

    /// <summary>Whether the host raises warnings of runaway processes (<c>monitor</c> version 2).</summary>
    public bool OffersWarnings => connection.Welcome.Services.GetValueOrDefault("monitor") >= 2;

    /// <summary><c>monitor.warnings {}</c>: those raised and not cleared yet.</summary>
    public async Task<List<MonitorWarning>> MonitorWarningsAsync() =>
        OffersWarnings ? MonitorWarning.ParseList(await connection.CallAsync("monitor", "warnings", new JsonObject())) : [];

    /// <summary><c>monitor.kill { id }</c>: ends what the warning is about; how many processes were ended.</summary>
    public async Task<int> MonitorKillAsync(string warningId) =>
        (int?)(await connection.CallAsync("monitor", "kill", new JsonObject { ["id"] = warningId }))?["killed"] ?? 0;

    /// <summary>Raised on the connection's reading thread, only for a change newer than what was applied (an event
    /// the host told before the list it answered is older than that list, and dropped).</summary>
    public event Action<SessionStatus>? StatusChanged;

    public bool Offers(string service) => connection.Welcome.Services.ContainsKey(service);

    /// <summary>The revision rule (docs/ptyhost-protocol.md, "sessions"): an event applies only when its revision is
    /// newer than the list's and than the session's last applied change. Without revisions (an older host), all apply.</summary>
    bool Accept(SessionStatus status, bool fromList)
    {
        if (status.Revision is not { } revision) return true;
        lock (gate)
        {
            if (!fromList && revision <= snapshot) return false;
            if (applied.TryGetValue(status.SessionId, out var last) && revision <= last) return false;
            applied[status.SessionId] = revision;
            return true;
        }
    }

    /// <summary>The statuses as listed, without those already overtaken by a newer event.</summary>
    public async Task<IReadOnlyList<SessionStatus>> ListSessionsAsync()
    {
        if (!Offers("sessions")) return [];
        var result = await connection.CallAsync("sessions", "list");
        if ((long?)result?["revision"] is { } revision)
            lock (gate) snapshot = Math.Max(snapshot, revision);
        return (result?["sessions"]?.AsArray() ?? []).Where(n => n is not null).Select(n => SessionStatus.From(n!))
            .Where(s => Accept(s, fromList: true)).ToList();
    }

    public async Task<(IReadOnlyList<TranscriptItem> Items, bool HasMore)> ListTranscriptsAsync(string home, int limit, string? query = null)
    {
        if (!Offers("transcripts")) return ([], false);
        var parameters = new JsonObject { ["home"] = home, ["limit"] = limit, ["exclude"] = new JsonArray() };
        if (query is not null) parameters["query"] = query;
        var result = await connection.CallAsync("transcripts", "list", parameters);
        var items = (result?["items"]?.AsArray() ?? []).Where(n => n is not null).Select(n => new TranscriptItem(
            (string)n!["sessionId"]!, (double?)n["modifiedAt"] ?? 0, (string?)n["cwd"], (string?)n["title"])).ToList();
        return (items, (bool?)result?["hasMore"] ?? false);
    }

    /// <summary>Claude Code's title of a session, or null.</summary>
    public async Task<string?> TitleAsync(string home, string claudeSessionId)
    {
        if (!Offers("transcripts")) return null;
        var result = await connection.CallAsync("transcripts", "title", new JsonObject { ["home"] = home, ["sessionId"] = claudeSessionId });
        return result is JsonValue v && v.TryGetValue<string>(out var title) ? title : null;
    }

    /// <summary><c>quota.codex { localAppData }</c>: whether LLM Coordinator is installed, and its <c>usage.snapshot</c>
    /// answer (null when it gave none). Nothing without the service.</summary>
    public async Task<(bool Coordinator, JsonNode? Usage, string? Error)> QuotaCodexAsync(string? localAppData)
    {
        if (!Offers("quota")) return (false, null, null);
        var result = await connection.CallAsync("quota", "codex", new JsonObject { ["localAppData"] = localAppData });
        return ((bool?)result?["coordinator"] ?? false, result?["usage"]?.DeepClone(), (string?)result?["error"]);
    }

    /// <summary><c>monitor.sample {}</c> (Rust host only): what each running session uses now; empty without the service.</summary>
    public async Task<Dictionary<string, SessionUsage>> MonitorSampleAsync() =>
        Offers("monitor") ? SessionUsage.ParseSample(await connection.CallAsync("monitor", "sample", new JsonObject())) : [];

    /// <summary>The <c>overview</c> service (Rust host only), or null without it.</summary>
    public IOverviewSource? Overview => Offers("overview") ? new HostOverviewSource(connection) : null;

    /// <summary><c>overview.configure { cache, homes }</c> (never as an observer: the host refuses it). Its status.</summary>
    public async Task<OverviewStatus> ConfigureOverviewAsync(JsonObject parameters) =>
        OverviewStatus.Parse(await connection.CallAsync("overview", "configure", parameters));

    sealed class HostOverviewSource(HostConnection connection) : IOverviewSource
    {
        public Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null) =>
            connection.CallAsync("overview", method, parameters ?? new JsonObject());
    }

    /// <summary><c>state.read { file }</c>: the file's text (null when it is not there) and the host's revision of it.</summary>
    public async Task<(string? Content, long? Revision)> ReadStateAsync(string file)
    {
        var result = await connection.CallAsync("state", "read", new JsonObject { ["file"] = file });
        return ((string?)result?["content"], (long?)result?["revision"]);
    }

    /// <summary><c>state.read</c> with the revision required (the store's form).</summary>
    public async Task<(string? Content, long Revision)> ReadStateRevisionAsync(string file)
    {
        var (content, revision) = await ReadStateAsync(file);
        return (content, revision ?? throw new HostServiceException("bad-response", "state.read without a revision"));
    }

    /// <summary><c>state.write { file, content, revision, backup }</c>: the new revision; <c>conflict</c> when stale.</summary>
    public async Task<long> WriteStateAsync(string file, string content, long revision, bool backup)
    {
        var result = await connection.CallAsync("state", "write", new JsonObject
        {
            ["file"] = file, ["content"] = content, ["revision"] = revision, ["backup"] = backup,
        });
        return (long?)result?["revision"] ?? throw new HostServiceException("bad-response", "state.write without a revision");
    }

    // ---------- wsl (docs/ptyhost-protocol.md, "cleanup, wsl and history") ----------

    /// <summary><c>wsl.running</c>: the distributions that run now; none without the service.</summary>
    public async Task<IReadOnlyList<string>> WslRunningAsync()
    {
        if (!Offers("wsl")) return [];
        var result = await connection.CallAsync("wsl", "running", new JsonObject());
        return (result?.AsArray() ?? []).Select(n => (string?)n).OfType<string>().ToList();
    }

    /// <summary><c>wsl.info</c>: where claude and its home are in <paramref name="distro"/> (null: none; asking starts it).</summary>
    public async Task<WslInfo?> WslInfoAsync(string distro) =>
        Offers("wsl") ? WslInfo.From(await connection.CallAsync("wsl", "info", new JsonObject { ["distro"] = distro })) : null;

    /// <summary><c>wsl.isClaude</c>: whether Linux process <paramref name="pid"/> of <paramref name="distro"/> is a claude.</summary>
    public async Task<bool> WslIsClaudeAsync(string distro, int pid) =>
        Offers("wsl") && (bool?)await connection.CallAsync("wsl", "isClaude", new JsonObject { ["distro"] = distro, ["pid"] = pid }) == true;

    /// <summary><c>wsl.stopClaude</c>: ends that claude as Ctrl+C would; "failed" when it did not go (or is no claude).</summary>
    public async Task<string> WslStopClaudeAsync(string distro, int pid) =>
        Offers("wsl") ? (string?)await connection.CallAsync("wsl", "stopClaude", new JsonObject { ["distro"] = distro, ["pid"] = pid }) ?? "failed" : "failed";

    /// <summary>Whether the host keeps distributions running (<c>wsl</c> version 2).</summary>
    public bool CanKeepWsl => connection.Welcome.Services.TryGetValue("wsl", out var version) && version >= 2;

    /// <summary><c>wsl.keep</c>: starts <paramref name="distro"/> and keeps it running until <see cref="WslReleaseAsync"/>.</summary>
    public async Task<WslInfo?> WslKeepAsync(string distro) =>
        CanKeepWsl ? WslInfo.From(await connection.CallAsync("wsl", "keep", new JsonObject { ["distro"] = distro })) : null;

    /// <summary><c>wsl.release</c>: lets go of the distributions the host keeps running.</summary>
    public async Task WslReleaseAsync()
    {
        if (CanKeepWsl) await connection.CallAsync("wsl", "release", new JsonObject());
    }

    // ---------- history ----------

    static JsonObject HelperParams(HistoryHelper helper, string home) =>
        new() { ["backend"] = helper.Backend, ["cwd"] = helper.Cwd, ["home"] = home };

    /// <summary><c>history.prepare</c>: each chat's plan (read only), in an operation folder of its own under <paramref name="root"/>.</summary>
    public async Task<IReadOnlyList<JsonObject>> HistoryPrepareAsync(HistoryHelper helper, string home, string root, IEnumerable<string> ids)
    {
        var p = HelperParams(helper, home);
        p["root"] = root;
        p["ids"] = new JsonArray([.. ids.Select(id => (JsonNode)id!)]);
        return Outcomes(await connection.CallAsync("history", "prepare", p));
    }

    /// <summary><c>history.retire</c>: archives or deletes the prepared chats (jobs: <c>{ prepared, mode, metadata }</c>).</summary>
    public async Task<IReadOnlyList<JsonObject>> HistoryRetireAsync(HistoryHelper helper, string home, JsonArray jobs)
    {
        var p = HelperParams(helper, home);
        p["jobs"] = jobs;
        return Outcomes(await connection.CallAsync("history", "retire", p));
    }

    /// <summary><c>history.restore</c>: puts an archived chat's files back.</summary>
    public async Task HistoryRestoreAsync(HistoryHelper helper, string home, ArchiveEntry entry)
    {
        var p = HelperParams(helper, home);
        p["id"] = entry.Id;
        p["operation"] = entry.Operation;
        p["archive"] = entry.Archive;
        p["sha256"] = entry.Sha256;
        await connection.CallAsync("history", "restore", p);
    }

    static List<JsonObject> Outcomes(JsonNode? result) =>
        (result as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();
}

/// <summary>A WSL distribution's answer to the probe (<c>WslInfo</c>): claude (null: none) and Claude Code's home there.</summary>
public sealed record WslInfo(string Distro, string? Claude, string? ClaudeHome)
{
    public static WslInfo? From(JsonNode? node) => node is JsonObject o
        ? new WslInfo((string?)o["distro"] ?? "", (string?)o["claude"], (string?)o["claudeHome"])
        : null;
}

/// <summary>history_backend.py (absolute) and the folder it runs in (the profile), as the <c>history</c> service takes them.</summary>
public sealed record HistoryHelper(string Backend, string Cwd);
