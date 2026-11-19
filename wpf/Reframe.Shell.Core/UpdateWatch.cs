using System.Text.Json;

namespace Reframe.Shell.Core;

/// <summary>A release installed beside the running one: the deploy script records it in <c>&lt;root&gt;\active.json</c>.</summary>
public sealed record InstalledUpdate(string Version, string Executable);

/// <summary>Finds the release the deploy script installed after this shell started. Releases live in
/// <c>&lt;root&gt;\releases\&lt;version&gt;</c> and <c>&lt;root&gt;\active.json</c> names the newest one.</summary>
public static class UpdateWatch
{
    /// <summary>active.json beside the releases folder the running shell sits in, or null when it is not a managed release.</summary>
    public static string? ActiveFile(string baseDirectory)
    {
        var release = baseDirectory.TrimEnd('\\', '/');
        var releases = Path.GetDirectoryName(release);
        if (releases is null || !string.Equals(Path.GetFileName(releases), "releases", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetDirectoryName(releases) is { } root ? Path.Combine(root, "active.json") : null;
    }

    /// <summary>The installed release when it differs from the running version and its executable exists, else null.</summary>
    public static InstalledUpdate? Pending(string activeJson, string runningVersion, Func<string, bool> exists)
    {
        try
        {
            using var doc = JsonDocument.Parse(activeJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var exe = root.TryGetProperty("executable", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(exe)) return null;
            return Strip(version) == Strip(runningVersion) || !exists(exe) ? null : new InstalledUpdate(version, exe);
        }
        catch (JsonException)
        {
            return null; // being rewritten: the next check reads it whole
        }
    }

    static string Strip(string version) => version.Split('+')[0].Trim();
}
