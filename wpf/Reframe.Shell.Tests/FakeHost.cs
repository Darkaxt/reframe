using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;

namespace Reframe.Shell.Tests;

/// <summary>
/// An in-process stand-in for the PTY host: a profile folder with a token and a descriptor, and a named pipe that
/// speaks the protocol's handshake, one session's view (snapshot, then data), the sessions, transcripts and state (read) services.
/// With <c>observers</c> it is a host from 0.10.1-claudegpt.25: it advertises observers (descriptor and welcome), an
/// observer's hello replaces nobody, and the sessions service gives revisions.
/// </summary>
sealed class FakeHost : IAsyncDisposable
{
    public const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    readonly CancellationTokenSource stop = new();
    readonly Task serving;
    NamedPipeServerStream? current;

    public string ProfileDir { get; }
    /// <summary>What the host's descriptor file says.</summary>
    public HostDescriptor Descriptor { get; }
    public string PipeName { get; } = $"reframe-ptyhost-test-{Guid.NewGuid():N}";
    /// <summary>Every request the host got after a hello, in order.</summary>
    public ConcurrentQueue<JsonObject> Requests { get; } = new();
    /// <summary>Every hello, in order.</summary>
    public ConcurrentQueue<JsonObject> Hellos { get; } = new();
    readonly bool observers;
    /// <summary>What <c>state.read</c> serves, by file name (the text, or none there).</summary>
    public ConcurrentDictionary<string, string> StateFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    readonly string token;

    /// <param name="profileDir">A profile whose token is there already (a host the shell starts); by default a new
    /// one with <see cref="Token"/>.</param>
    public FakeHost(bool observers = false, string? profileDir = null)
    {
        this.observers = observers;
        ProfileDir = profileDir ?? Path.Combine(Path.GetTempPath(), "reframe-shell-tests", Guid.NewGuid().ToString("N"));
        var hostDir = Path.Combine(ProfileDir, "ptyhost");
        Directory.CreateDirectory(Path.Combine(hostDir, "hosts"));
        if (profileDir is null) File.WriteAllText(Path.Combine(hostDir, "token"), Token);
        token = File.ReadAllText(Path.Combine(hostDir, "token")).Trim();
        var pid = Environment.ProcessId;
        File.WriteAllText(Path.Combine(hostDir, "hosts", $"{pid}.json"), new JsonObject
        {
            ["pid"] = pid, ["pipe"] = $@"\\.\pipe\{PipeName}", ["protocol"] = 1, ["version"] = "test",
            ["startedAt"] = 0, ["sessionPids"] = new JsonArray(), ["kind"] = "rust",
            ["observer"] = observers ? 1 : null,
        }.ToJsonString());
        Descriptor = new HostDescriptor(pid, $@"\\.\pipe\{PipeName}", 1, "test", "rust", observers ? 1 : 0);
        serving = Task.Run(ServeAsync);
    }

    async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(stop.Token); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); return; }
            _ = Task.Run(() => ClientAsync(pipe));
        }
    }

    async Task ClientAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var welcomed = false;
        try
        {
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                var message = JsonNode.Parse(line)!.AsObject();
                if (!welcomed)
                {
                    if ((string?)message["token"] != token)
                    {
                        await SendAsync(pipe, new JsonObject { ["type"] = "refused", ["reason"] = "token", ["protocol"] = 1 });
                        return;
                    }
                    welcomed = true;
                    Hellos.Enqueue(message);
                    var observer = observers && (string?)message["role"] == "observer";
                    if (!observer)
                    {
                        var previous = Interlocked.Exchange(ref current, pipe);
                        if (previous is not null)
                        {
                            await SendAsync(previous, new JsonObject { ["type"] = "replaced" });
                            previous.Dispose();
                        }
                    }
                    var services = new JsonObject { ["transcripts"] = 1, ["sessions"] = 1, ["state"] = 1 };
                    if (observers) services["observer"] = 1;
                    await SendAsync(pipe, new JsonObject
                    {
                        ["type"] = "welcome", ["protocol"] = 1, ["version"] = "test", ["pid"] = Environment.ProcessId,
                        ["hooks"] = new JsonObject { ["port"] = 1, ["token"] = "h" }, ["kind"] = "rust",
                        ["sessions"] = new JsonArray(new JsonObject
                        {
                            ["sessionId"] = "s1", ["instance"] = 3, ["pid"] = 42, ["exitCode"] = null,
                            // Started directly (no meta.launched): the host tells Claude Code's id itself.
                            ["meta"] = new JsonObject { ["claudeHome"] = @"C:\claude" }, ["claudeSessionId"] = "c1",
                        }),
                        ["services"] = services,
                        ["role"] = observer ? "observer" : "primary", ["primary"] = true,
                    });
                    continue;
                }
                Requests.Enqueue(message);
                await AnswerAsync(pipe, message, observers);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    async Task AnswerAsync(Stream pipe, JsonObject request, bool revisions)
    {
        switch ((string?)request["type"])
        {
            case "view-open":
                var viewId = (int)request["viewId"]!;
                await SendAsync(pipe, new JsonObject
                {
                    ["type"] = "view", ["viewId"] = viewId,
                    ["message"] = new JsonObject { ["type"] = "snapshot", ["data"] = "repaint", ["cols"] = 100, ["rows"] = 30 },
                });
                await SendAsync(pipe, new JsonObject
                {
                    ["type"] = "view", ["viewId"] = viewId, ["message"] = new JsonObject { ["type"] = "data", ["data"] = "héllo ✓" },
                });
                break;
            case "svc":
                var id = (int)request["id"]!;
                var key = $"{request["service"]}.{request["method"]}";
                JsonObject answer = key switch
                {
                    "sessions.list" => Ok(id, new JsonObject
                    {
                        ["sessions"] = new JsonArray(new JsonObject
                        {
                            ["sessionId"] = "s1", ["instance"] = 3, ["status"] = "idle",
                            ["registry"] = new JsonObject { ["pid"] = 42, ["sessionId"] = "c1", ["name"] = "fix the build" },
                            ["revision"] = revisions ? 5 : null,
                        }),
                        ["revision"] = revisions ? 5 : null,
                    }),
                    "transcripts.title" => Ok(id, JsonValue.Create("A title")),
                    "state.read" => Ok(id, new JsonObject
                    {
                        ["revision"] = 7,
                        ["content"] = StateFiles.TryGetValue((string)request["params"]!["file"]!, out var text) ? text : null,
                    }),
                    _ => new JsonObject
                    {
                        ["type"] = "svc-result", ["id"] = id, ["ok"] = false,
                        ["error"] = new JsonObject { ["code"] = "unknown-method", ["message"] = key },
                    },
                };
                await SendAsync(pipe, answer);
                if (key == "sessions.list")
                {
                    // With revisions: one the list already holds (told late, so dropped), then a newer one.
                    if (revisions)
                        await SendAsync(pipe, new JsonObject
                        {
                            ["type"] = "svc-event", ["service"] = "sessions", ["event"] = "status",
                            ["data"] = new JsonObject { ["sessionId"] = "s1", ["instance"] = 3, ["status"] = "waiting", ["revision"] = 4 },
                        });
                    await SendAsync(pipe, new JsonObject
                    {
                        ["type"] = "svc-event", ["service"] = "sessions", ["event"] = "status",
                        ["data"] = new JsonObject { ["sessionId"] = "s1", ["instance"] = 3, ["status"] = "busy", ["revision"] = revisions ? 6 : null },
                    });
                }
                break;
        }
    }

    static JsonObject Ok(int id, JsonNode? result) => new() { ["type"] = "svc-result", ["id"] = id, ["ok"] = true, ["result"] = result };

    static async Task SendAsync(Stream pipe, JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        current?.Dispose();
        await serving;
        try { Directory.Delete(ProfileDir, true); } catch (IOException) { }
    }
}
