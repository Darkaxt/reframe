using System.ComponentModel;
using System.Diagnostics;

namespace Reframe.Shell.Core;

/// <summary>Where the proxy's own supervisor and its hidden launcher are installed.</summary>
public sealed record ProxyInstall(string Directory, string StartScript);

public enum ProxyStop { NotRunning, Stopped, TimedOut }

/// <summary>
/// Stops and starts the local proxy the way its own tooling does, never by killing a process: the proxy runs under its
/// supervisor (<c>supervise-proxy.ps1</c>), which stops only the child it owns when the named stop event is signaled
/// (<c>stop-proxy.ps1</c> does the same), and starts through <c>start-hidden.vbs</c>. A proxy no supervisor runs
/// (started by hand) is left alone.
/// </summary>
public static class ProxyControl
{
    public const string Instance = "ClaudeCodeProxySupervisor";

    /// <summary>The installation under <c>%LOCALAPPDATA%\Programs\claude-code-proxy</c>, or null when it has no supervisor
    /// and launcher (then Reframe only watches the proxy).</summary>
    public static ProxyInstall? Find(string localAppData)
    {
        if (string.IsNullOrEmpty(localAppData)) return null;
        var dir = Path.Combine(localAppData, "Programs", "claude-code-proxy");
        var start = Path.Combine(dir, "start-hidden.vbs");
        return File.Exists(start) && File.Exists(Path.Combine(dir, "supervise-proxy.ps1")) ? new ProxyInstall(dir, start) : null;
    }

    /// <summary>Signals the supervisor to stop its child (without relaunching it), then waits until the supervisor is gone.</summary>
    public static ProxyStop Stop(string instance, TimeSpan wait)
    {
        EventWaitHandle stop;
        try
        {
            stop = EventWaitHandle.OpenExisting($@"Local\{instance}-stop");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return ProxyStop.NotRunning;
        }
        using (stop) stop.Set();
        Mutex mutex;
        try
        {
            mutex = Mutex.OpenExisting($@"Local\{instance}-mutex");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return ProxyStop.Stopped;
        }
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(wait)) return ProxyStop.TimedOut;
            }
            catch (AbandonedMutexException)
            {
            }
            mutex.ReleaseMutex();
            return ProxyStop.Stopped;
        }
    }

    /// <summary>The launcher's arguments: wscript hidden, the script, the scratch folder (on D: or E:) and the skill file.</summary>
    public static List<string> StartArguments(ProxyInstall install, string scratch, string skill) =>
        ["//B", "//Nologo", install.StartScript, scratch, skill];

    /// <summary>Starts the supervisor (and so the proxy) hidden; null when started, else why not. The launcher lives as long
    /// as the proxy, so it is not waited for.</summary>
    public static string? Start(ProxyInstall install, string scratch, string skill)
    {
        if (!File.Exists(skill)) return $"The proxy's launcher needs {skill}, which is missing.";
        try
        {
            Directory.CreateDirectory(scratch);
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wscript.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in StartArguments(install, scratch, skill)) info.ArgumentList.Add(argument);
            Process.Start(info)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return $"The proxy's launcher did not start: {e.Message}";
        }
    }
}
