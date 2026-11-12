using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>A session's status from the host's <c>sessions</c> service (<c>sessions.status</c> / <c>list</c>).</summary>
public sealed record SessionStatus(string SessionId, int Instance, string Status, string? Name, string? ClaudeSessionId,
    string? Cwd, string? Notify)
{
    public static SessionStatus From(JsonNode node)
    {
        var registry = node["registry"];
        return new SessionStatus((string)node["sessionId"]!, (int?)node["instance"] ?? 0, (string?)node["status"] ?? "starting",
            (string?)registry?["name"], (string?)registry?["sessionId"], (string?)registry?["cwd"], (string?)node["notify"]);
    }
}

public sealed record TranscriptItem(string SessionId, double ModifiedAt, string? Cwd, string? Title);

/// <summary>The v1 services the shell uses: <c>sessions</c> and <c>transcripts</c>.</summary>
public sealed class HostServices
{
    readonly HostConnection connection;

    public HostServices(HostConnection connection)
    {
        this.connection = connection;
        connection.ServiceEvent += (service, name, data) =>
        {
            if (service == "sessions" && name == "status" && data is not null) StatusChanged?.Invoke(SessionStatus.From(data));
        };
    }

    /// <summary>Raised on the connection's reading thread.</summary>
    public event Action<SessionStatus>? StatusChanged;

    public bool Offers(string service) => connection.Welcome.Services.ContainsKey(service);

    public async Task<IReadOnlyList<SessionStatus>> ListSessionsAsync()
    {
        if (!Offers("sessions")) return [];
        var result = await connection.CallAsync("sessions", "list");
        return (result?["sessions"]?.AsArray() ?? []).Where(n => n is not null).Select(n => SessionStatus.From(n!)).ToList();
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
}
