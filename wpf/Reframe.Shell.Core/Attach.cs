namespace Reframe.Shell.Core;

public enum AttachOutcome
{
    Attached,
    /// <summary>The profile has no token or no running host of protocol 1, and the shell started none (it had no host to
    /// start, or another client runs on the profile).</summary>
    NoHost,
    /// <summary>Another client is (probably) attached and <c>--take-over</c> was not given.</summary>
    OtherClient,
    Failed,
}

/// <param name="Older">Hosts of earlier releases that cannot start sessions for this shell, attached for the sessions they
/// still run (see <see cref="ShellAttach"/>).</param>
public sealed record AttachResult(AttachOutcome Outcome, string Message, HostConnection? Connection = null, IDisposable? Lock = null,
    IReadOnlyList<HostConnection>? Older = null);

/// <summary>
/// How the shell attaches to a profile's host. A primary attach replaces whichever client is attached (one primary
/// per profile). While another client (reframe) is found, the shell attaches as a read-only observer to a host that
/// offers it (<c>observer</c> in its descriptor, from 0.10.1-claudegpt.25), beside that client; to an older host it
/// refuses, and takes over only when asked to (<c>--take-over</c>, always a primary attach). With no host running and no
/// other client, the shell starts one (given a <see cref="HostStarter"/>) and attaches as the primary; a running host
/// that can start its sessions (launch.spawnPlan), even an older release's, is attached to rather than a second one
/// started (reframe's hot swap). One that cannot keeps its sessions beside a current host started for new ones. The
/// shell never ends sessions or hosts on its own.
/// </summary>
public static class ShellAttach
{
    /// <summary>
    /// Attaches to the profile's running host (the profile is reframe's user-data folder; its host folder is
    /// <c>ptyhost</c> in it), or starts one with <paramref name="start"/> when none runs. <paramref name="connect"/> is
    /// for tests; by default the descriptor's pipe.
    /// </summary>
    public static async Task<AttachResult> AttachAsync(string profileDir, bool takeOver,
        Func<HostDescriptor, string, Task<HostConnection>>? connect = null, Func<int, bool>? isAlive = null,
        HostStarter? start = null)
    {
        var hostDir = HostFiles.HostDir(profileDir);
        var token = HostFiles.ReadToken(hostDir);
        var hosts = HostFiles.ReadDescriptors(hostDir, isAlive).Where(h => h.Protocol == HostConnection.ProtocolVersion).ToList();
        var other = Presence.OtherClient(profileDir);
        // Beside another client: only as an observer, so only to hosts that offer it (an older one would be replaced).
        var observe = other is not null && !takeOver;
        if (observe) hosts = hosts.Where(h => h.Observer >= 1).ToList();
        connect ??= (host, token) => HostConnection.ConnectAsync(host.Pipe, token, observe);
        if (observe && hosts.Count == 0 && token is not null && HostFiles.ReadDescriptors(hostDir, isAlive).Count > 0)
            return new AttachResult(AttachOutcome.OtherClient,
                $"{other}; its pty host has no observer mode, so attaching would replace it. Start the shell with --take-over to do so anyway.");
        if (token is null || hosts.Count == 0)
        {
            if (start is null)
                return new AttachResult(AttachOutcome.NoHost, $"no running pty host in {hostDir}, and no pty host to start (reframe-host beside the shell, REFRAME_RUST_HOST_DIR or --host-dir)");
            if (other is not null)
                return new AttachResult(AttachOutcome.NoHost, $"{other} without a pty host the shell can attach to; it starts none beside it");
            return await StartAsync(profileDir, hostDir, takeOver, start, connect);
        }

        var shellLock = Presence.AcquireShellLock(profileDir);
        if (shellLock is null && !takeOver)
            return new AttachResult(AttachOutcome.OtherClient, "another Reframe shell runs on this profile");

        var errors = new List<string>();
        if (observe)
        {
            foreach (var host in hosts)
            {
                try
                {
                    var connection = await connect(host, token);
                    return new AttachResult(AttachOutcome.Attached,
                        $"watching read-only ({other}): the {connection.Welcome.Kind} pty host {connection.Welcome.Version} (pid {connection.Welcome.Pid})",
                        connection, shellLock);
                }
                catch (Exception e) when (IsConnectError(e))
                {
                    errors.Add($"pid {host.Pid}: {e.Message}");
                }
            }
            shellLock?.Dispose();
            return new AttachResult(AttachOutcome.Failed, "could not attach: " + string.Join("; ", errors));
        }

        // Every running host is attached: one that can start sessions for this shell is the primary; one that cannot
        // (an earlier release's, kept running across the update) keeps serving the sessions it runs, beside a current
        // host started for the new ones, and is let go once they ended (it exits by itself then).
        var connected = new List<HostConnection>();
        foreach (var host in hosts)
        {
            try
            {
                connected.Add(await connect(host, token));
            }
            catch (Exception e) when (IsConnectError(e))
            {
                errors.Add($"pid {host.Pid}: {e.Message}");
            }
        }
        var primary = connected.FirstOrDefault(c => c.CanStartSessions);
        var how = "attached to";
        if (primary is null && start is not null && other is null)
        {
            try
            {
                primary = await connect(await start(hostDir, token, CancellationToken.None), token);
                how = "started and attached to";
            }
            catch (Exception e) when (IsConnectError(e))
            {
                errors.Add("could not start a pty host: " + e.Message);
            }
        }
        primary ??= connected.FirstOrDefault();
        if (primary is null)
        {
            shellLock?.Dispose();
            return new AttachResult(AttachOutcome.Failed, "could not attach: " + string.Join("; ", errors));
        }
        var older = connected.Where(c => c != primary && c.Welcome.Sessions.Count > 0).ToList();
        foreach (var idle in connected.Where(c => c != primary && !older.Contains(c))) await idle.DisposeAsync();
        var message = $"{how} the {primary.Welcome.Kind} pty host {primary.Welcome.Version} (pid {primary.Welcome.Pid})";
        foreach (var old in older)
            message += $"; the {old.Welcome.Kind} pty host {old.Welcome.Version} (pid {old.Welcome.Pid}) keeps its {old.Welcome.Sessions.Count} sessions";
        return new AttachResult(AttachOutcome.Attached, message, primary, shellLock, older);
    }

    static bool IsConnectError(Exception e) =>
        e is IOException or TimeoutException or OperationCanceledException or HostRefusedException or UnauthorizedAccessException;

    static async Task<AttachResult> StartAsync(string profileDir, string hostDir, bool takeOver, HostStarter start,
        Func<HostDescriptor, string, Task<HostConnection>> connect)
    {
        // The lock first, so two shells never start two hosts.
        var shellLock = Presence.AcquireShellLock(profileDir);
        if (shellLock is null && !takeOver)
            return new AttachResult(AttachOutcome.OtherClient, "another Reframe shell runs on this profile");
        try
        {
            var token = HostFiles.EnsureToken(hostDir);
            var host = await start(hostDir, token, CancellationToken.None);
            var connection = await connect(host, token);
            return new AttachResult(AttachOutcome.Attached,
                $"started and attached to the {connection.Welcome.Kind} pty host {connection.Welcome.Version} (pid {connection.Welcome.Pid})",
                connection, shellLock);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or HostRefusedException or UnauthorizedAccessException)
        {
            shellLock?.Dispose();
            return new AttachResult(AttachOutcome.Failed, "could not start a pty host: " + e.Message);
        }
    }
}
