using Microsoft.Win32;

namespace Reframe.Shell.Core;

/// <summary>One of Windows 11's per-icon tray records (<c>HKCU\Control Panel\NotifyIconSettings\&lt;key&gt;</c>):
/// the executable that added the icon and whether it is shown on the taskbar (null: the user never chose).</summary>
public sealed record TrayIconEntry(string Key, string ExecutablePath, int? IsPromoted);

public interface ITrayIconSettings
{
    IReadOnlyList<TrayIconEntry> Entries();
    void SetPromoted(string key, int value);
    void Delete(string key);
}

/// <summary>The current user's real records.</summary>
public sealed class RegistryTrayIconSettings : ITrayIconSettings
{
    const string Root = @"Control Panel\NotifyIconSettings";

    public IReadOnlyList<TrayIconEntry> Entries()
    {
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        if (root is null) return [];
        var entries = new List<TrayIconEntry>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("ExecutablePath") is not string path) continue;
            entries.Add(new TrayIconEntry(name, path, key.GetValue("IsPromoted") is int promoted ? promoted : null));
        }
        return entries;
    }

    public void SetPromoted(string key, int value)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\{key}", true);
        k?.SetValue("IsPromoted", value, RegistryValueKind.DWord);
    }

    public void Delete(string key)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Root, true);
        root?.DeleteSubKeyTree(key, false);
    }
}

/// <summary>
/// Windows keeps "show this icon on the taskbar" per executable path, and every release runs from its own version
/// folder (releases\&lt;version&gt;\reframe.exe), so each update's icon would start hidden in the overflow again. The
/// release's own record takes the choice of the newest other release the user decided for (only "shown" needs
/// writing: hidden is Windows' default), unless the user already chose for this one. Records of releases whose
/// executable is gone (pruned) are removed. Only sibling releases of the same executable name are touched.
/// </summary>
public static class TrayPromotion
{
    public sealed record Plan(string? Promote, IReadOnlyList<string> Remove, bool OwnFound);

    public static Plan Decide(IReadOnlyList<TrayIconEntry> entries, string executable, Func<string, bool> exists)
    {
        var own = Path.GetFullPath(executable);
        var name = Path.GetFileName(own);
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(own));
        bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        bool Sibling(TrayIconEntry e) =>
            Same(Path.GetFileName(e.ExecutablePath), name) && parent is not null
            && Same(Path.GetDirectoryName(Path.GetDirectoryName(e.ExecutablePath)) ?? "", parent);

        var mine = entries.FirstOrDefault(e => Same(e.ExecutablePath, own));
        var others = entries.Where(e => e != mine && Sibling(e)).ToList();
        var remove = others.Where(e => !exists(e.ExecutablePath)).Select(e => e.Key).ToList();
        string? promote = null;
        if (mine is { IsPromoted: null })
        {
            var latest = others.Where(e => e.IsPromoted is not null)
                .OrderByDescending(e => VersionKey(e.ExecutablePath), VersionComparer).FirstOrDefault();
            if (latest?.IsPromoted == 1) promote = mine.Key;
        }
        return new Plan(promote, remove, mine is not null);
    }

    /// <summary>The numbers in the version folder's name, in order ("0.11.0-claudegpt.35" → 0, 11, 0, 35).</summary>
    static long[] VersionKey(string executable)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(executable)) ?? "";
        return System.Text.RegularExpressions.Regex.Matches(folder, @"\d+")
            .Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToArray();
    }

    static readonly Comparer<long[]> VersionComparer = Comparer<long[]>.Create((a, b) =>
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    });

    /// <summary>Applies <see cref="Decide"/>; false while Windows has not written this release's record yet.</summary>
    public static bool Apply(ITrayIconSettings settings, string executable, Func<string, bool> exists, Action<string> log)
    {
        var plan = Decide(settings.Entries(), executable, exists);
        foreach (var key in plan.Remove) settings.Delete(key);
        if (plan.Remove.Count > 0) log($"tray: removed {plan.Remove.Count} record(s) of releases no longer installed");
        if (plan.Promote is { } promote)
        {
            settings.SetPromoted(promote, 1);
            log("tray: shown on the taskbar, as for the previous release");
        }
        return plan.OwnFound;
    }
}
