using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Reframe.Shell.Core;

/// <summary>The two values Windows starts programs at login from (per user), as Electron's setLoginItemSettings uses
/// them: the command in <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, and whether Task Manager's Startup
/// page lets it run in <c>…\Explorer\StartupApproved\Run</c>.</summary>
public interface IRunKeys
{
    string? GetRun(string name);
    void SetRun(string name, string command);
    void DeleteRun(string name);
    byte[]? GetApproved(string name);
    void SetApproved(string name, byte[] value);
    void DeleteApproved(string name);
}

/// <summary>The current user's real Run keys.</summary>
public sealed class RegistryRunKeys : IRunKeys
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static object? Get(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name);
    }

    static void Set(string key, string name, object value, RegistryValueKind kind)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, true);
        k.SetValue(name, value, kind);
    }

    static void Delete(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key, true);
        k?.DeleteValue(name, false);
    }

    public string? GetRun(string name) => Get(Run, name) as string;
    public void SetRun(string name, string command) => Set(Run, name, command, RegistryValueKind.String);
    public void DeleteRun(string name) => Delete(Run, name);
    public byte[]? GetApproved(string name) => Get(Approved, name) as byte[];
    public void SetApproved(string name, byte[] value) => Set(Approved, name, value, RegistryValueKind.Binary);
    public void DeleteApproved(string name) => Delete(Approved, name);
}

/// <summary>
/// Starting with the system (src/main/autostart.ts): Electron keeps its login item in the user's Run key, under its
/// AppUserModelID, as <c>"&lt;exe&gt;" --autostart</c>. The shell uses the same keys, but only ever its own value,
/// named after its profile (<see cref="ValueName"/>), so it never changes reframe's own entry (or another profile's):
/// the shell runs on a test profile until the cutover (S14) points the real entry at it.
/// A managed start (<paramref name="appId"/>, REFRAME_APP_ID) uses reframe's own entry instead: the value Electron named
/// after that app id, with <c>"&lt;exe&gt;" --autostart</c> (a direct start reads reframe-config.json beside the executable),
/// so the cutover takes the user's existing login entry over (<see cref="Repoint"/>).
/// </summary>
public sealed class Autostart(IRunKeys keys, string profileDir, string executable, string? appId = null)
{
    /// <summary>AUTOSTART_ARG: marks a start by the system.</summary>
    public const string Argument = "--autostart";

    /// <summary>StartupApproved's "enabled" value as Windows writes it (an odd first byte means disabled there).</summary>
    static readonly byte[] EnabledValue = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The Run value of the shell on <paramref name="profileDir"/>: "Reframe Shell" and a hash of the profile's
    /// full path (case-insensitive), so it never names reframe's or another profile's entry.</summary>
    public static string ValueName(string profileDir)
    {
        var full = Path.GetFullPath(profileDir).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..12].ToLowerInvariant();
        return $"Reframe Shell {hash}";
    }

    public string Name { get; } = string.IsNullOrEmpty(appId) ? ValueName(profileDir) : appId;

    /// <summary>launchCommand: the shell, its profile (unless managed: then reframe-config.json gives it) and
    /// <see cref="Argument"/>.</summary>
    public string Command { get; } = string.IsNullOrEmpty(appId)
        ? $"\"{executable}\" --profile \"{Path.GetFullPath(profileDir).TrimEnd('\\')}\" {Argument}"
        : $"\"{executable}\" {Argument}";

    /// <summary>At a start: an existing entry of this name that starts something else (an earlier release, Electron's
    /// reframe.exe) is pointed at this executable; whether Task Manager lets it run stays as it is. No entry: nothing
    /// is written. True when it changed the entry.</summary>
    public bool Repoint()
    {
        var current = keys.GetRun(Name);
        if (current is null || current == Command) return false;
        keys.SetRun(Name, Command);
        return true;
    }

    /// <summary>getLoginItemSettings().openAtLogin: the entry is there and not disabled in Task Manager.</summary>
    public bool Enabled
    {
        get
        {
            if (keys.GetRun(Name) is null) return false;
            return keys.GetApproved(Name) is not { Length: > 0 } approved || approved[0] % 2 == 0;
        }
    }

    /// <summary>setLoginItemSettings({ openAtLogin }): the entry written (and enabled), or taken out.</summary>
    public void Set(bool enabled)
    {
        if (enabled)
        {
            keys.SetRun(Name, Command);
            keys.SetApproved(Name, EnabledValue);
        }
        else
        {
            keys.DeleteRun(Name);
            keys.DeleteApproved(Name);
        }
    }
}
