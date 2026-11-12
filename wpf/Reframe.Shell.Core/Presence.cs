namespace Reframe.Shell.Core;

/// <summary>
/// Whether another client may be attached to a profile's host. The protocol has no observer mode and does not say
/// whether a client is attached: a welcomed connection simply replaces the attached one. So the shell looks for the
/// clients it knows: reframe (Electron/Chromium holds <c>&lt;userData&gt;/lockfile</c> open, unshared, while it runs)
/// and another shell (which holds <c>ptyhost/reframe-shell.lock</c>).
/// </summary>
public static class Presence
{
    public const string ShellLockName = "reframe-shell.lock";

    /// <summary>The other client found, or null.</summary>
    public static string? OtherClient(string profileDir)
    {
        if (IsHeld(Path.Combine(profileDir, "lockfile"))) return "reframe (Electron) runs on this profile";
        if (IsHeld(Path.Combine(HostFiles.HostDir(profileDir), ShellLockName))) return "another Reframe shell runs on this profile";
        return null;
    }

    /// <summary>Holds the shell's lock for as long as the stream is open; null if another shell holds it.</summary>
    public static FileStream? AcquireShellLock(string profileDir)
    {
        var dir = HostFiles.HostDir(profileDir);
        Directory.CreateDirectory(dir);
        try
        {
            return new FileStream(Path.Combine(dir, ShellLockName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
    }

    static bool IsHeld(string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
