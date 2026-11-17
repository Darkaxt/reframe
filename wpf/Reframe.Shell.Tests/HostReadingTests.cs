using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>What the host sends that the shell cannot handle must not end the reading (0.11.0-claudegpt.64): a session
/// ended with 0xC000013A once made the exit message throw, the reading stopped on a connection that still looked open,
/// and every terminal, service answer and hook of that host waited forever; its welcome then failed the next start.</summary>
public class HostReadingTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    sealed class Pair : IAsyncDisposable
    {
        public readonly NamedPipeServerStream Server;
        public readonly NamedPipeClientStream Client;
        readonly StreamReader reader;

        Pair(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
            reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        }

        public static async Task<Pair> OpenAsync()
        {
            var name = $"reframe-reading-test-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return new Pair(server, client);
        }

        public async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            await Server.WriteAsync(bytes);
            await Server.FlushAsync();
        }

        public async Task<JsonObject> ReceiveAsync() =>
            (JsonObject)JsonNode.Parse(await reader.ReadLineAsync().WaitAsync(Wait) ?? throw new IOException("closed"))!;

        public async ValueTask DisposeAsync()
        {
            reader.Dispose();
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    static JsonObject Welcome(JsonNode? exitCode) => new()
    {
        ["type"] = "welcome", ["protocol"] = 1, ["version"] = "test", ["pid"] = 7, ["kind"] = "rust",
        ["sessions"] = new JsonArray(new JsonObject { ["sessionId"] = "s1", ["instance"] = 1, ["pid"] = 9, ["exitCode"] = exitCode }),
        ["services"] = new JsonObject { ["monitor"] = 1 },
    };

    static async Task<HostConnection> HandshakeAsync(Pair pair, JsonObject welcome)
    {
        var shaking = HostConnection.HandshakeAsync(pair.Client, FakeHost.Token);
        Assert.Equal("hello", (string?)(await pair.ReceiveAsync())["type"]);
        await pair.SendAsync(welcome);
        return await shaking.WaitAsync(Wait);
    }

    [Fact]
    public async Task An_exit_code_beyond_an_int_is_read_as_windows_shows_it()
    {
        await using var pair = await Pair.OpenAsync();
        await using var connection = await HandshakeAsync(pair, Welcome(3221225786));
        Assert.Equal(unchecked((int)0xC000013A), Assert.Single(connection.Welcome.Sessions).ExitCode);
    }

    [Fact]
    public async Task An_unreadable_welcome_refuses_the_host_instead_of_failing_the_start()
    {
        await using var pair = await Pair.OpenAsync();
        var welcome = Welcome(null);
        welcome["sessions"]![0]!["instance"] = "one";
        await Assert.ThrowsAsync<HostRefusedException>(() => HandshakeAsync(pair, welcome));
    }

    [Fact]
    public async Task A_message_whose_handling_throws_does_not_end_the_reading()
    {
        await using var pair = await Pair.OpenAsync();
        await using var connection = await HandshakeAsync(pair, Welcome(null));
        connection.ServiceEvent += (_, _, _) => throw new InvalidOperationException("a handler failed");

        var call = connection.CallAsync("monitor", "sample", new JsonObject());
        var request = await pair.ReceiveAsync();
        await pair.SendAsync(new JsonObject { ["type"] = "svc-event", ["service"] = "monitor", ["event"] = "x", ["data"] = null });
        await pair.SendAsync(new JsonObject { ["type"] = "svc-result", ["id"] = request["id"]!.DeepClone(), ["ok"] = true, ["result"] = new JsonObject { ["fine"] = true } });

        var result = await call.WaitAsync(Wait);
        Assert.Equal(true, (bool?)result?["fine"]);
        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task A_connection_whose_reading_ends_is_closed_and_fails_its_calls()
    {
        await using var pair = await Pair.OpenAsync();
        await using var connection = await HandshakeAsync(pair, Welcome(null));
        var call = connection.CallAsync("monitor", "sample", new JsonObject());
        await pair.ReceiveAsync();
        pair.Server.Disconnect();

        await Assert.ThrowsAsync<HostServiceException>(() => call.WaitAsync(Wait));
        Assert.False(connection.IsOpen);
    }
}
