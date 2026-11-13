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
/// </summary>
public sealed class Autostart(IRunKeys keys, string profileDir, string executable)
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

    public string Name { get; } = ValueName(profileDir);

    /// <summary>launchCommand: the shell, its profile and <see cref="Argument"/>.</summary>
    public string Command { get; } = $"\"{executable}\" --profile \"{Path.GetFullPath(profileDir).TrimEnd('\\')}\" {Argument}";

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
