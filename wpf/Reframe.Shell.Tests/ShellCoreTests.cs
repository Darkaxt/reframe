using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ShellCoreTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static async Task<HostConnection> AttachAsync(FakeHost host, bool takeOver = false)
    {
        var result = await ShellAttach.AttachAsync(host.ProfileDir, takeOver);
        Assert.Equal(AttachOutcome.Attached, result.Outcome);
        return result.Connection!;
    }

    [Fact]
    public async Task Handshake_reads_the_welcome()
    {
        await using var host = new FakeHost();
        await using var connection = await AttachAsync(host);
        var welcome = connection.Welcome;
        Assert.Equal(1, welcome.Protocol);
        Assert.Equal("rust", welcome.Kind);
        var session = Assert.Single(welcome.Sessions);
        Assert.Equal(new HostSession("s1", 3, 42, null, @"C:\claude", "c1"), session);
        Assert.Equal(1, welcome.Services["sessions"]);
    }

    [Fact]
    public async Task A_view_gets_the_snapshot_then_data_and_takes_input()
    {
        await using var host = new FakeHost();
        await using var connection = await AttachAsync(host);
        var view = connection.CreateView("s1");
        var snapshot = new TaskCompletionSource<(string, int, int)>();
        var data = new TaskCompletionSource<string>();
        view.Snapshot += (d, cols, rows) => snapshot.TrySetResult((d, cols, rows));
        view.Data += d => data.TrySetResult(d);
        await view.OpenAsync();
        Assert.Equal(("repaint", 100, 30), await snapshot.Task.WaitAsync(Wait));
        Assert.Equal("héllo ✓", await data.Task.WaitAsync(Wait));

        await view.InputAsync("x");
        await view.ResizeAsync(120, 40);
        await view.ResizeAsync(1, 40); // not sent: the host takes only sizes over 1
        view.Close();
        var types = await RequestsAsync(host, 4);
        Assert.Equal(["view-open", "view-message", "view-message", "view-close"], types.Select(r => (string?)r["type"]));
        Assert.Equal("x", (string?)types[1]["message"]!["data"]);
        Assert.Equal(120, (int?)types[2]["message"]!["cols"]);
        Assert.DoesNotContain(host.Requests, r => (string?)r["type"] == "shutdown");
    }

    [Fact]
    public async Task Services_answer_and_tell_events()
    {
        await using var host = new FakeHost();
        await using var connection = await AttachAsync(host);
        var services = new HostServices(connection);
        var changed = new TaskCompletionSource<SessionStatus>();
        services.StatusChanged += s => changed.TrySetResult(s);

        var listed = Assert.Single(await services.ListSessionsAsync());
        Assert.Equal(("s1", "idle", "fix the build", "c1"), (listed.SessionId, listed.Status, listed.Name, listed.ClaudeSessionId));
        Assert.Equal("busy", (await changed.Task.WaitAsync(Wait)).Status);
        Assert.Equal("A title", await services.TitleAsync(@"C:\claude", "c1"));

        var failed = await Assert.ThrowsAsync<HostServiceException>(() => connection.CallAsync("nope", "x"));
        Assert.Equal("unknown-method", failed.Code);
    }

    [Fact]
    public async Task Refuses_to_attach_while_reframe_is_attached_unless_taking_over()
    {
        await using var host = new FakeHost();
        // Electron (Chromium) keeps <userData>/lockfile open, shared for reading only, while it runs.
        using (new FileStream(Path.Combine(host.ProfileDir, "lockfile"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var refused = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false);
            Assert.Equal(AttachOutcome.OtherClient, refused.Outcome);
            Assert.Null(refused.Connection);
            Assert.Empty(host.Requests);

            await using var taken = await AttachAsync(host, takeOver: true);
            Assert.True(taken.IsOpen);
        }
    }

    [Fact]
    public async Task Watches_read_only_beside_reframe_when_the_host_offers_observers()
    {
        await using var host = new FakeHost(observers: true);
        using (new FileStream(Path.Combine(host.ProfileDir, "lockfile"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var result = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false);
            Assert.Equal(AttachOutcome.Attached, result.Outcome);
            await using var connection = result.Connection!;
            Assert.True(connection.IsObserver);
            Assert.True(connection.Welcome.PrimaryAttached);
            Assert.Equal("observer", (string?)Assert.Single(host.Hellos)["role"]);

            var view = connection.CreateView("s1");
            var snapshot = new TaskCompletionSource();
            view.Snapshot += (_, _, _) => snapshot.TrySetResult();
            await view.OpenAsync();
            await snapshot.Task.WaitAsync(Wait);
            Assert.True(view.ReadOnly);
            await view.InputAsync("x"); // not sent: an observer's views are read-only
            await view.ResizeAsync(120, 40);
            view.Close();
            var requests = await RequestsAsync(host, 2);
            Assert.Equal(["view-open", "view-close"], requests.Select(r => (string?)r["type"]));
            result.Lock?.Dispose();
        }

        // Without reframe there, the shell is the primary as before; --take-over is always a primary attach.
        await using var primary = await AttachAsync(host, takeOver: true);
        Assert.False(primary.IsObserver);
        Assert.Null(host.Hellos.Last()["role"]);
    }

    [Fact]
    public async Task Applies_a_status_only_when_newer_than_the_list()
    {
        await using var host = new FakeHost(observers: true);
        await using var connection = await AttachAsync(host);
        var services = new HostServices(connection);
        var changes = new List<SessionStatus>();
        var busy = new TaskCompletionSource();
        services.StatusChanged += s =>
        {
            lock (changes) changes.Add(s);
            if (s.Status == "busy") busy.TrySetResult();
        };
        var listed = Assert.Single(await services.ListSessionsAsync());
        Assert.Equal(5, listed.Revision);
        await busy.Task.WaitAsync(Wait);
        // The "waiting" of revision 4, told after the list of revision 5, was dropped.
        lock (changes) Assert.Equal(["busy"], changes.Select(c => c.Status));
    }

    [Fact]
    public async Task Refuses_a_second_shell_on_the_profile()
    {
        await using var host = new FakeHost();
        var first = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false);
        Assert.Equal(AttachOutcome.Attached, first.Outcome);
        var second = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false);
        Assert.Equal(AttachOutcome.OtherClient, second.Outcome);
        Assert.True(first.Connection!.IsOpen);
        await first.Connection.DisposeAsync();
        first.Lock!.Dispose();
    }

    [Fact]
    public async Task Being_replaced_closes_the_connection_and_its_views()
    {
        await using var host = new FakeHost();
        var first = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false);
        var view = first.Connection!.CreateView("s1");
        var viewClosed = new TaskCompletionSource();
        view.Closed += () => viewClosed.TrySetResult();
        var replaced = new TaskCompletionSource<bool>();
        first.Connection.Closed += r => replaced.TrySetResult(r);

        await using var second = await AttachAsync(host, takeOver: true);
        Assert.True(await replaced.Task.WaitAsync(Wait));
        await viewClosed.Task.WaitAsync(Wait);
        await Assert.ThrowsAsync<HostServiceException>(() => first.Connection.CallAsync("sessions", "list"));
        first.Lock?.Dispose();
    }

    [Fact]
    public async Task No_host_and_a_wrong_token_are_reported()
    {
        var empty = Path.Combine(Path.GetTempPath(), "reframe-shell-tests", Guid.NewGuid().ToString("N"));
        Assert.Equal(AttachOutcome.NoHost, (await ShellAttach.AttachAsync(empty, false)).Outcome);

        await using var host = new FakeHost();
        File.WriteAllText(Path.Combine(host.ProfileDir, "ptyhost", "token"), new string('f', 64));
        var result = await ShellAttach.AttachAsync(host.ProfileDir, false);
        Assert.Equal(AttachOutcome.Failed, result.Outcome);
        Assert.Contains("token", result.Message);
    }

    static async Task<List<JsonObject>> RequestsAsync(FakeHost host, int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (host.Requests.Count < count && DateTime.UtcNow < deadline) await Task.Delay(20);
        return host.Requests.ToList();
    }
}
