namespace Reframe.Shell.Core;

public enum AttachOutcome
{
    Attached,
    /// <summary>The profile has no token or no running host of protocol 1: nothing to attach to (the shell starts none).</summary>
    NoHost,
    /// <summary>Another client is (probably) attached and <c>--take-over</c> was not given.</summary>
    OtherClient,
    Failed,
}

public sealed record AttachResult(AttachOutcome Outcome, string Message, HostConnection? Connection = null, IDisposable? Lock = null);

/// <summary>
/// How the shell attaches to a profile's host. A primary attach replaces whichever client is attached (one primary
/// per profile). While another client (reframe) is found, the shell attaches as a read-only observer to a host that
/// offers it (<c>observer</c> in its descriptor, from 0.10.1-claudegpt.25), beside that client; to an older host it
/// refuses, and takes over only when asked to (<c>--take-over</c>, always a primary attach).
/// </summary>
public static class ShellAttach
{
    /// <summary>
    /// Attaches to the profile's running host (the profile is reframe's user-data folder; its host folder is
    /// <c>ptyhost</c> in it). <paramref name="connect"/> is for tests; by default the descriptor's pipe.
    /// </summary>
    public static async Task<AttachResult> AttachAsync(string profileDir, bool takeOver,
        Func<HostDescriptor, string, Task<HostConnection>>? connect = null, Func<int, bool>? isAlive = null)
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
            return new AttachResult(AttachOutcome.NoHost, $"no running pty host in {hostDir} (the shell does not start one)");

        var shellLock = Presence.AcquireShellLock(profileDir);
        if (shellLock is null && !takeOver)
            return new AttachResult(AttachOutcome.OtherClient, "another Reframe shell runs on this profile");

        var errors = new List<string>();
        foreach (var host in hosts)
        {
            try
            {
                var connection = await connect(host, token);
                var how = connection.IsObserver ? $"watching read-only ({other}):" : "attached to";
                return new AttachResult(AttachOutcome.Attached,
                    $"{how} the {connection.Welcome.Kind} pty host {connection.Welcome.Version} (pid {connection.Welcome.Pid})",
                    connection, shellLock);
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or HostRefusedException or UnauthorizedAccessException)
            {
                errors.Add($"pid {host.Pid}: {e.Message}");
            }
        }
        shellLock?.Dispose();
        return new AttachResult(AttachOutcome.Failed, "could not attach: " + string.Join("; ", errors));
    }
}
