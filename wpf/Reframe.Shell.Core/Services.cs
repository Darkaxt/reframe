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
        };
    }

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
}
