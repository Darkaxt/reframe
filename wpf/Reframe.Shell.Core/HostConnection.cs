using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>A session the host ran when the shell attached (from the welcome).</summary>
public sealed record HostSession(string SessionId, int Instance, int Pid, int? ExitCode, string? ClaudeHome, string? ClaudeSessionId);

public sealed record Welcome(int Protocol, string Version, int Pid, string Kind, IReadOnlyList<HostSession> Sessions,
    IReadOnlyDictionary<string, int> Services);

/// <summary>A failed service request (<c>code</c> as the host gives it), or <c>unavailable</c>: not asked or not answered.</summary>
public sealed class HostServiceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class HostRefusedException(string reason) : Exception($"the pty host refused: {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>
/// One connection to a PTY host (docs/ptyhost-protocol.md), as src/main/ptyhost-client.ts talks to it: newline-
/// delimited JSON, a hello, then requests and events. Events are raised on the reading thread.
/// </summary>
public sealed class HostConnection : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    readonly Stream stream;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ConcurrentDictionary<int, HostView> views = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> calls = new();
    int nextView;
    int nextCall;
    int closed;

    public Welcome Welcome { get; private set; } = null!;

    /// <summary>A service event: service, event, data.</summary>
    public event Action<string, string, JsonNode?>? ServiceEvent;
    /// <summary>A host event other than views and services (spawned, exit, hook, error...).</summary>
    public event Action<JsonObject>? HostEvent;
    /// <summary>The connection is gone; true: another client of the profile took over (<c>replaced</c>).</summary>
    public event Action<bool>? Closed;

    HostConnection(Stream stream) => this.stream = stream;

    /// <summary>Connects to a host's pipe (<c>\\.\pipe\...</c> as its descriptor gives it) and shakes hands.</summary>
    public static async Task<HostConnection> ConnectAsync(string pipe, string token, CancellationToken cancel = default)
    {
        const string prefix = @"\\.\pipe\";
        var name = pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? pipe[prefix.Length..] : pipe;
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(timeout.Token);
            return await HandshakeAsync(client, token, timeout.Token);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Shakes hands over a connected stream.</summary>
    public static async Task<HostConnection> HandshakeAsync(Stream stream, string token, CancellationToken cancel = default)
    {
        var connection = new HostConnection(stream);
        await connection.SendAsync(new JsonObject { ["type"] = "hello", ["token"] = token, ["protocol"] = ProtocolVersion });
        var reader = new LineReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancel) ?? throw new IOException("the pty host closed the connection");
            if (Parse(line) is not { } first) continue;
            var type = (string?)first["type"];
            if (type == "refused") throw new HostRefusedException((string?)first["reason"] ?? "unknown");
            if (type != "welcome") throw new HostRefusedException(type ?? "unknown");
            connection.Welcome = ReadWelcome(first);
            break;
        }
        _ = Task.Run(() => connection.ReadLoopAsync(reader));
        return connection;
    }

    static Welcome ReadWelcome(JsonObject w)
    {
        var sessions = new List<HostSession>();
        foreach (var s in w["sessions"]?.AsArray() ?? [])
        {
            if (s is null) continue;
            var meta = s["meta"];
            sessions.Add(new HostSession((string)s["sessionId"]!, (int)s["instance"]!, (int?)s["pid"] ?? 0,
                (int?)s["exitCode"], (string?)meta?["claudeHome"], (string?)meta?["launched"]?["claudeSessionId"]));
        }
        var services = new Dictionary<string, int>();
        if (w["services"] is JsonObject offered)
            foreach (var (name, version) in offered)
                if (version is JsonValue v && v.TryGetValue<int>(out var n)) services[name] = n;
        return new Welcome((int?)w["protocol"] ?? 0, (string?)w["version"] ?? "", (int?)w["pid"] ?? 0,
            (string?)w["kind"] ?? "node", sessions, services);
    }

    async Task ReadLoopAsync(LineReader reader)
    {
        var replaced = false;
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (Parse(line) is not { } message) continue;
                if ((string?)message["type"] == "replaced") replaced = true;
                else Received(message);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        Close(replaced);
    }

    void Received(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "view":
                if (views.TryGetValue((int)message["viewId"]!, out var view) && message["message"] is JsonObject m) view.Received(m);
                break;
            case "view-closed":
                if (views.TryRemove((int)message["viewId"]!, out var gone)) gone.Ended();
                break;
            case "svc-result":
                if (!calls.TryRemove((int)message["id"]!, out var call)) break;
                if ((bool?)message["ok"] == true) call.TrySetResult(message["result"]?.DeepClone());
                else call.TrySetException(new HostServiceException((string?)message["error"]?["code"] ?? "io",
                    (string?)message["error"]?["message"] ?? ""));
                break;
            case "svc-event":
                ServiceEvent?.Invoke((string?)message["service"] ?? "", (string?)message["event"] ?? "", message["data"]);
                break;
            case "welcome" or "refused":
                break;
            default:
                HostEvent?.Invoke(message);
                break;
        }
    }

    void Close(bool replaced)
    {
        if (Interlocked.Exchange(ref closed, 1) == 1) return;
        foreach (var call in calls.Values) call.TrySetException(new HostServiceException("unavailable", "the pty host connection closed"));
        calls.Clear();
        foreach (var view in views.Values) view.Ended();
        views.Clear();
        stream.Dispose();
        Closed?.Invoke(replaced);
    }

    public bool IsOpen => Volatile.Read(ref closed) == 0;

    /// <summary>Opens a view of a session's terminal: it gets a snapshot, then live output. Wire its events first.</summary>
    public HostView CreateView(string sessionId)
    {
        var view = new HostView(this, Interlocked.Increment(ref nextView), sessionId);
        views[view.ViewId] = view;
        return view;
    }

    internal Task OpenViewAsync(HostView view) =>
        SendAsync(new JsonObject { ["type"] = "view-open", ["viewId"] = view.ViewId, ["sessionId"] = view.SessionId });

    internal void CloseView(HostView view)
    {
        if (views.TryRemove(view.ViewId, out _)) _ = SendAsync(new JsonObject { ["type"] = "view-close", ["viewId"] = view.ViewId });
    }

    internal Task ViewMessageAsync(int viewId, JsonObject message) =>
        SendAsync(new JsonObject { ["type"] = "view-message", ["viewId"] = viewId, ["message"] = message });

    /// <summary>Asks a service of the host (one its welcome advertises).</summary>
    public async Task<JsonNode?> CallAsync(string service, string method, JsonObject? parameters = null)
    {
        if (!IsOpen) throw new HostServiceException("unavailable", "no pty host attached");
        var id = Interlocked.Increment(ref nextCall);
        var call = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls[id] = call;
        await SendAsync(new JsonObject
        {
            ["type"] = "svc", ["id"] = id, ["service"] = service, ["method"] = method, ["params"] = parameters ?? new JsonObject(),
        });
        if (!IsOpen) call.TrySetException(new HostServiceException("unavailable", "the pty host connection closed"));
        return await call.Task;
    }

    async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writeLock.WaitAsync();
        try
        {
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { Close(false); }
        finally { writeLock.Release(); }
    }

    /// <summary>Leaves the host with its sessions running (reframe's <c>keep</c>): never sends <c>shutdown</c>.</summary>
    public ValueTask DisposeAsync()
    {
        Close(false);
        return ValueTask.CompletedTask;
    }

    static JsonObject? Parse(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Splits the stream into lines (UTF-8, <c>\n</c>), keeping multi-byte characters whole across reads.</summary>
    sealed class LineReader(Stream stream)
    {
        readonly byte[] buffer = new byte[64 * 1024];
        readonly MemoryStream pending = new();

        public async Task<string?> ReadLineAsync(CancellationToken cancel)
        {
            while (true)
            {
                var data = pending.GetBuffer();
                var at = Array.IndexOf(data, (byte)'\n', 0, (int)pending.Length);
                if (at >= 0)
                {
                    var line = Encoding.UTF8.GetString(data, 0, at);
                    var rest = (int)pending.Length - at - 1;
                    Buffer.BlockCopy(data, at + 1, data, 0, rest);
                    pending.SetLength(rest);
                    return line;
                }
                var read = await stream.ReadAsync(buffer, cancel);
                if (read == 0) return null;
                pending.Write(buffer, 0, read);
            }
        }
    }
}

/// <summary>
/// A view of a session's terminal. Closing it only detaches the view; the session goes on in the host.
/// Events are raised on the connection's reading thread.
/// </summary>
public sealed class HostView
{
    readonly HostConnection connection;
    int ended;

    internal HostView(HostConnection connection, int viewId, string sessionId)
    {
        this.connection = connection;
        ViewId = viewId;
        SessionId = sessionId;
    }

    public int ViewId { get; }
    public string SessionId { get; }

    /// <summary>The mirror's repaint: data for a terminal reset and resized to cols × rows.</summary>
    public event Action<string, int, int>? Snapshot;
    public event Action<string>? Data;
    public event Action<int>? Exit;
    /// <summary>The view is gone: closed, its session disposed, or the connection lost.</summary>
    public event Action? Closed;

    /// <summary>Sends <c>view-open</c>; the host answers with the snapshot (or waits for the session's spawn).</summary>
    public Task OpenAsync() => connection.OpenViewAsync(this);

    internal void Received(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "snapshot": Snapshot?.Invoke((string?)message["data"] ?? "", (int?)message["cols"] ?? 80, (int?)message["rows"] ?? 24); break;
            case "data": Data?.Invoke((string?)message["data"] ?? ""); break;
            case "exit": Exit?.Invoke((int?)message["exitCode"] ?? 0); break;
        }
    }

    internal void Ended()
    {
        if (Interlocked.Exchange(ref ended, 1) == 0) Closed?.Invoke();
    }

    public Task InputAsync(string data) =>
        connection.ViewMessageAsync(ViewId, new JsonObject { ["type"] = "input", ["data"] = data });

    /// <summary>Resizes the session's ConPTY and mirror; the host takes it only if both are more than 1.</summary>
    public Task ResizeAsync(int cols, int rows) => cols > 1 && rows > 1
        ? connection.ViewMessageAsync(ViewId, new JsonObject { ["type"] = "resize", ["cols"] = cols, ["rows"] = rows })
        : Task.CompletedTask;

    /// <summary>Detaches the view (<c>view-close</c>); never ends the session.</summary>
    public void Close()
    {
        connection.CloseView(this);
        Ended();
    }
}
