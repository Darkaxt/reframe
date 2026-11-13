using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Reframe.Shell.Core;

/// <summary>A running host's descriptor, <c>hosts/&lt;pid&gt;.json</c> in the profile's host folder.</summary>
/// <param name="Observer">The host's observer version (0: none), so a client knows before its hello that it can attach
/// without replacing the attached one (docs/ptyhost-protocol.md, "Observers").</param>
public sealed record HostDescriptor(int Pid, string Pipe, int Protocol, string Version, string Kind, int Observer = 0);

/// <summary>Reads a profile's host folder (<c>&lt;userData&gt;/ptyhost</c>) as src/main/ptyhost-files.ts does.</summary>
public static class HostFiles
{
    public static string HostDir(string profileDir) => Path.Combine(profileDir, "ptyhost");

    /// <summary>The profile's token, or null when there is none (yet).</summary>
    public static string? ReadToken(string hostDir)
    {
        try
        {
            var token = File.ReadAllText(Path.Combine(hostDir, "token")).Trim();
            return token.Length == 64 && token.All(Uri.IsHexDigit) && token == token.ToLowerInvariant() ? token : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The profile's token, made on first use as src/main/ptyhost-files.ts ensureToken does (only before the shell starts
    /// a host): 32 random bytes in hex, in a file only the current user can access, written whole or not at all.
    /// </summary>
    public static string EnsureToken(string hostDir)
    {
        if (ReadToken(hostDir) is { } existing) return existing;
        Directory.CreateDirectory(hostDir);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var file = Path.Combine(hostDir, "token");
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        using (var stream = new FileInfo(temp).Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
        using (var writer = new StreamWriter(stream)) writer.Write(token);
        File.Move(temp, file, overwrite: true);
        return token;
    }

    /// <summary>Descriptors of hosts whose process still runs.</summary>
    public static List<HostDescriptor> ReadDescriptors(string hostDir, Func<int, bool>? isAlive = null)
    {
        isAlive ??= Alive;
        var found = new List<HostDescriptor>();
        string[] files;
        try { files = Directory.GetFiles(Path.Combine(hostDir, "hosts"), "*.json"); }
        catch (DirectoryNotFoundException) { return found; }
        foreach (var file in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var d = new HostDescriptor(
                    root.GetProperty("pid").GetInt32(),
                    root.GetProperty("pipe").GetString() ?? "",
                    root.GetProperty("protocol").GetInt32(),
                    root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    root.TryGetProperty("kind", out var k) ? k.GetString() ?? "node" : "node",
                    root.TryGetProperty("observer", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var ov) ? ov : 0);
                if (d.Pid == pid && isAlive(pid)) found.Add(d);
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return found;
    }

    internal static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
