using System.Diagnostics;
using System.Text.Json;

namespace Reframe.Shell.Core;

/// <summary>A running host's descriptor, <c>hosts/&lt;pid&gt;.json</c> in the profile's host folder.</summary>
public sealed record HostDescriptor(int Pid, string Pipe, int Protocol, string Version, string Kind);

/// <summary>Reads a profile's host folder (<c>&lt;userData&gt;/ptyhost</c>) as src/main/ptyhost-files.ts does.</summary>
public static class HostFiles
{
    public static string HostDir(string profileDir) => Path.Combine(profileDir, "ptyhost");

    /// <summary>The profile's token, or null. Unlike reframe, the shell never makes one: no token, no host to attach to.</summary>
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
                    root.TryGetProperty("kind", out var k) ? k.GetString() ?? "node" : "node");
                if (d.Pid == pid && isAlive(pid)) found.Add(d);
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return found;
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
