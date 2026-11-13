using System.Diagnostics;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Host start, hot swap and the single instance (stage 4, S3).</summary>
public class HostStartTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static string NewProfile() => Path.Combine(Path.GetTempPath(), "reframe-shell-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Starts_a_host_when_none_runs_and_attaches_as_the_primary()
    {
        var profile = NewProfile();
        FakeHost? started = null;
        var calls = 0;
        HostStarter start = (hostDir, token, _) =>
        {
            calls++;
            Assert.Equal(Path.Combine(profile, "ptyhost"), hostDir);
            Assert.Equal(token, File.ReadAllText(Path.Combine(hostDir, "token")));
            started = new FakeHost(profileDir: profile);
            return Task.FromResult(started.Descriptor);
        };
        try
        {
            var result = await ShellAttach.AttachAsync(profile, takeOver: false, start: start);
            Assert.Equal(AttachOutcome.Attached, result.Outcome);
            Assert.StartsWith("started and attached", result.Message);
            Assert.Equal(1, calls);
            Assert.False(result.Connection!.IsObserver);
            var hello = Assert.Single(started!.Hellos);
            Assert.NotEqual("observer", (string?)hello["role"]);
            Assert.Matches("^[0-9a-f]{64}$", HostFiles.ReadToken(Path.Combine(profile, "ptyhost")));
            await result.Connection.DisposeAsync();
            result.Lock!.Dispose();
            // Leaving never ends the host's sessions.
            Assert.DoesNotContain(started.Requests, r => (string?)r["type"] == "shutdown");
        }
        finally
        {
            if (started is not null) await started.DisposeAsync();
        }
    }

    [Fact]
    public async Task Attaches_to_a_running_host_of_an_older_release_instead_of_starting_one()
    {
        await using var host = new FakeHost(launch: true);
        var result = await ShellAttach.AttachAsync(host.ProfileDir, takeOver: false,
            start: (_, _, _) => throw new InvalidOperationException("started a second host"));
        Assert.Equal(AttachOutcome.Attached, result.Outcome);
        Assert.StartsWith("attached to", result.Message);
        await result.Connection!.DisposeAsync();
        result.Lock!.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_that_cannot_start_sessions_keeps_its_own_beside_a_started_one(bool running)
    {
        // A host from before launch.spawnPlan (an earlier release's, kept running across the update).
        await using var old = new FakeHost(session: running);
        FakeHost? started = null;
        try
        {
            var result = await ShellAttach.AttachAsync(old.ProfileDir, takeOver: false, isAlive: _ => true, start: (_, _, _) =>
            {
                started = new FakeHost(profileDir: old.ProfileDir, launch: true, session: false, pid: 1);
                return Task.FromResult(started.Descriptor);
            });
            Assert.True(result.Outcome == AttachOutcome.Attached, result.Message);
            Assert.StartsWith("started and attached", result.Message);
            Assert.True(result.Connection!.CanStartSessions);
            Assert.Empty(result.Connection.Welcome.Sessions);
            if (running)
            {
                // Its sessions stay where they run: attached, never ended.
                var older = Assert.Single(result.Older!);
                Assert.True(older.IsOpen);
                Assert.Equal("s1", Assert.Single(older.Welcome.Sessions).SessionId);
                Assert.Contains("keeps its 1 sessions", result.Message);
                await older.DisposeAsync();
            }
            else Assert.Empty(result.Older!);
            Assert.DoesNotContain(old.Requests, r => (string?)r["type"] is "shutdown" or "dispose");
            await result.Connection.DisposeAsync();
            result.Lock!.Dispose();
        }
        finally
        {
            if (started is not null) await started.DisposeAsync();
        }
    }

    [Fact]
    public async Task Starts_no_host_while_reframe_holds_the_profile()
    {
        var profile = NewProfile();
        Directory.CreateDirectory(profile);
        try
        {
            using (new FileStream(Path.Combine(profile, "lockfile"), FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var result = await ShellAttach.AttachAsync(profile, takeOver: false,
                    start: (_, _, _) => throw new InvalidOperationException("started a host beside reframe"));
                Assert.Equal(AttachOutcome.NoHost, result.Outcome);
                Assert.Contains("Electron", result.Message);
            }
            Assert.False(File.Exists(Path.Combine(profile, "ptyhost", "token")));
        }
        finally
        {
            Directory.Delete(profile, true);
        }
    }

    [Fact]
    public async Task A_failed_start_is_reported_and_frees_the_lock()
    {
        var profile = NewProfile();
        var result = await ShellAttach.AttachAsync(profile, false, start: (_, _, _) => throw new TimeoutException("the pty host did not start in time"));
        Assert.Equal(AttachOutcome.Failed, result.Outcome);
        Assert.Contains("did not start", result.Message);
        using var shellLock = Presence.AcquireShellLock(profile);
        Assert.NotNull(shellLock);
    }

    [Fact]
    public void The_token_is_made_once_and_kept()
    {
        var dir = Path.Combine(NewProfile(), "ptyhost");
        var token = HostFiles.EnsureToken(dir);
        Assert.Matches("^[0-9a-f]{64}$", token);
        Assert.Equal(token, HostFiles.EnsureToken(dir));
        Assert.Equal(token, HostFiles.ReadToken(dir));
        var rules = new FileInfo(Path.Combine(dir, "token")).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        var rule = Assert.Single(rules.Cast<System.Security.AccessControl.FileSystemAccessRule>());
        Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.False(rule.IsInherited);
    }

    [Fact]
    public async Task A_second_launch_activates_the_first()
    {
        var profile = NewProfile();
        var activated = new TaskCompletionSource();
        using (var first = SingleInstance.TryAcquire(profile, () => activated.TrySetResult()))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryAcquire(profile, () => { }));
            // Another spelling of the same folder is the same profile.
            Assert.Null(SingleInstance.TryAcquire(profile.ToUpperInvariant() + "\\", () => { }));
            using (var other = SingleInstance.TryAcquire(NewProfile(), () => { })) Assert.NotNull(other);
            Assert.True(await SingleInstance.ActivateFirstAsync(profile, Wait));
            await activated.Task.WaitAsync(Wait);
        }
        Assert.False(await SingleInstance.ActivateFirstAsync(profile, TimeSpan.FromMilliseconds(300)));
        using var again = SingleInstance.TryAcquire(profile, () => { });
        Assert.NotNull(again);
    }

    /// <summary>The real Rust host (CI sets REFRAME_RUST_HOST_DIR to the reframe-host artifact): start, welcome, primary.</summary>
    [Fact]
    public async Task Starts_the_rust_host_and_attaches_as_the_primary()
    {
        if (HostLauncher.FindSource(Environment.GetEnvironmentVariable("REFRAME_RUST_HOST_DIR"), AppContext.BaseDirectory) is not { } source)
            return; // no host artifact here
        var profile = NewProfile();
        var launcher = new HostLauncher(source, "shell-test");
        var result = await ShellAttach.AttachAsync(profile, takeOver: false, start: launcher.StartAsync);
        var pid = result.Connection?.Welcome.Pid;
        try
        {
            Assert.True(result.Outcome == AttachOutcome.Attached, result.Message);
            Assert.Equal("rust", result.Connection!.Welcome.Kind);
            Assert.False(result.Connection.IsObserver);
            var descriptor = Assert.Single(HostFiles.ReadDescriptors(Path.Combine(profile, "ptyhost")));
            Assert.Equal(pid, descriptor.Pid);
            Assert.True(File.Exists(Path.Combine(profile, "ptyhost", "runtimes", $"shell-rust-{HostLauncher.Hash(source)[..12]}", HostLauncher.Exe)));

            // A later client (here a take-over) finds the running host: no second host is started.
            var again = await ShellAttach.AttachAsync(profile, takeOver: true,
                start: (_, _, _) => throw new InvalidOperationException("started a second host"));
            Assert.True(again.Outcome == AttachOutcome.Attached, again.Message);
            Assert.False(again.Connection!.IsObserver);
            Assert.Equal(pid, again.Connection.Welcome.Pid);
            await again.Connection.DisposeAsync();
            await result.Connection.DisposeAsync();
            result.Lock!.Dispose();
        }
        finally
        {
            if (pid is { } p)
                try { using var process = Process.GetProcessById(p); process.Kill(true); process.WaitForExit(5000); }
                catch (ArgumentException) { }
            try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
