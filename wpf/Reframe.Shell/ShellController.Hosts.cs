using System.Diagnostics;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Hosts of earlier releases (ShellAttach's Older): one that cannot start sessions for this shell (a host from
/// before launch.spawnPlan, kept running across an update) keeps running the sessions it has, shown and typed into as
/// before, while new and resumed sessions start in the current host. Once its last session ended it is let go, and it
/// exits by itself (a host with no client and no session ends after 10 s).</summary>
sealed partial class ShellController
{
    /// <summary>The sessions an older host runs, by session id.</summary>
    readonly Dictionary<string, HostConnection> olderHosts = [];

    /// <summary>The connection to the host that runs a session: an older host's, else the current one.</summary>
    public HostConnection? ConnectionOf(string sessionId) => olderHosts.GetValueOrDefault(sessionId) ?? Connection;

    /// <summary>Ends a session in its host (protocol <c>dispose</c>); an older host left with none is let go after that.</summary>
    void EndInHost(string sessionId, HostSession hosted)
    {
        var host = ConnectionOf(sessionId);
        var lastInOlder = olderHosts.Remove(sessionId) && !olderHosts.ContainsValue(host!);
        if (host is not { IsOpen: true }) return;
        _ = EndAsync(host);

        async Task EndAsync(HostConnection connection)
        {
            await connection.DisposeSessionAsync(sessionId, hosted.Instance);
            if (lastInOlder) await LetGoAsync(connection);
        }
    }

    /// <summary>A session no longer in its older host (it exited, or starts in the current host now).</summary>
    void LeaveOlderHost(string sessionId)
    {
        if (olderHosts.Remove(sessionId, out var older) && !olderHosts.ContainsValue(older)) _ = LetGoAsync(older);
    }

    async Task LetGoAsync(HostConnection older)
    {
        Trace.WriteLine($"pty host {older.Welcome.Version} (pid {older.Welcome.Pid}) runs none of the open sessions any more: let go");
        await older.DisposeAsync();
    }

    /// <summary>An older host gone (or taken over by another client): its sessions end here.</summary>
    void OlderHostClosed(HostConnection older)
    {
        foreach (var id in olderHosts.Where(e => e.Value == older).Select(e => e.Key).ToList())
        {
            olderHosts.Remove(id);
            HostSessions.Remove(id);
            HostStatus.Remove(id);
            SessionChanged(id);
        }
    }

    async Task LetGoOfOlderHostsAsync()
    {
        foreach (var older in olderHosts.Values.Distinct().ToList()) await older.DisposeAsync();
        olderHosts.Clear();
    }
}
