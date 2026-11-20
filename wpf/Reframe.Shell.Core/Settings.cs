using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>A change of preferences as SettingsDialog.tsx sends it (src/shared/ipc.ts PreferencesPatch): only the set
/// fields change; "" takes a chosen claude, model, effort or permission mode back.</summary>
public sealed record PreferencesPatch
{
    public string? ArchiveRoot { get; init; }
    public bool? NameFromFolder { get; init; }
    public string? Language { get; init; }
    /// <summary>A number as the page sends it (only a whole one in range is taken).</summary>
    public double? FontSize { get; init; }
    public double? SidebarWidth { get; init; }
    public bool? SnapToGrid { get; init; }
    public bool? CheckForUpdates { get; init; }
    public bool? LookInWsl { get; init; }
    public bool? WakeForTasks { get; init; }
    public int? CloseEndedAfterHours { get; init; }
    /// <summary>The hours the setting offers (0: never).</summary>
    public static readonly int[] CloseEndedChoices = [0, 1, 3, 6, 12, 24];
    public bool? GpuAcceleration { get; init; }
    public string? PtyHost { get; init; }
    public string? CloseWindow { get; init; }
    public string? ClaudeBin { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultEffort { get; init; }
    public string? DefaultPermissionMode { get; init; }
    /// <summary>The whole list of launch profiles (replaces the stored one; invalid entries are dropped).</summary>
    public IReadOnlyList<LaunchProfile>? Profiles { get; init; }
    /// <summary>The id of a profile whose choices become its provider's defaults.</summary>
    public string? MakeDefaultProfile { get; init; }
}

/// <summary>
/// Settings as src/main/app.ts applies them (the <c>reframe:set-preferences</c> handler): each field of a patch is
/// checked as there (and as the zod schema of src/shared/state.ts allows), a value that does not pass is ignored, and
/// the rest is taken. The caller saves the state when something changed.
/// </summary>
public static class Settings
{
    /// <summary>The languages Settings offers, with their names (src/shared/i18n.ts; the shell speaks English only for now).</summary>
    public static readonly IReadOnlyList<(string Value, string Name)> LanguageNames = [("en", "English"), ("de", "Deutsch")];

    /// <summary>isArchiveRoot (src/main/archive-root.ts, Windows): an absolute folder that is not on drive C.</summary>
    public static bool IsArchiveRoot(string root)
    {
        if (string.IsNullOrEmpty(root) || !IsAbsoluteWin32(root)) return false;
        string resolved;
        try
        {
            resolved = Path.GetFullPath(root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return !Regex.IsMatch(resolved, @"^(?:\\\\[?.]\\)?c:", RegexOptions.IgnoreCase);
    }

    /// <summary>path.win32.isAbsolute: rooted at a separator, or a drive letter with one.</summary>
    static bool IsAbsoluteWin32(string p) =>
        p[0] is '\\' or '/' || p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/';

    /// <summary>fontSizeAfter (src/renderer/shortcuts.ts): "bigger", "smaller" or "reset", within the range.</summary>
    public static int FontSizeAfter(int size, string change) => change == "reset"
        ? StateValues.FontSizeNormal
        : Math.Min(StateValues.FontSizeMax, Math.Max(StateValues.FontSizeMin, size + (change == "bigger" ? 1 : -1)));

    static string Clip(string value, int max) => value.Length > max ? value[..max] : value;

    /// <summary>Applies <paramref name="patch"/> to <paramref name="preferences"/>; whether anything changed.</summary>
    public static bool Apply(Preferences preferences, PreferencesPatch patch)
    {
        var p = preferences;
        var before = Snapshot(p);
        if (patch.ArchiveRoot is { } root && IsArchiveRoot(root)) p.ArchiveRoot = root;
        if (patch.NameFromFolder is { } nameFromFolder) p.NameFromFolder = nameFromFolder;
        if (patch.Language is { } language && StateValues.Languages.Contains(language)) p.Language = language;
        if (patch.FontSize is { } size && size == Math.Floor(size) && size >= StateValues.FontSizeMin && size <= StateValues.FontSizeMax)
            p.FontSize = (int)size;
        if (patch.SidebarWidth is { } width) p.SidebarWidth = StateValues.ClampSidebarWidth(width);
        if (patch.SnapToGrid is { } snap) p.SnapToGrid = snap;
        if (patch.CheckForUpdates is { } updates) p.CheckForUpdates = updates;
        if (patch.LookInWsl is { } wsl) p.LookInWsl = wsl;
        if (patch.WakeForTasks is { } wake) p.WakeForTasks = wake;
        if (patch.CloseEndedAfterHours is { } hours && PreferencesPatch.CloseEndedChoices.Contains(hours)) p.CloseEndedAfterHours = hours;
        if (patch.GpuAcceleration is { } gpu) p.GpuAcceleration = gpu;
        if (patch.PtyHost is { } host && StateValues.PtyHosts.Contains(host)) p.PtyHost = host;
        if (patch.CloseWindow is { } close && StateValues.CloseWindow.Contains(close)) p.CloseWindow = close;
        if (patch.ClaudeBin is { } bin) p.ClaudeBin = Clip(bin.Trim(), 1024) is { Length: > 0 } b ? b : null;
        if (patch.DefaultModel is { } model) p.DefaultModel = Clip(model.Trim(), 100) is { Length: > 0 } m ? m : null;
        if (patch.DefaultEffort == "") p.DefaultEffort = null;
        else if (patch.DefaultEffort is { } effort && StateValues.EffortLevels.Contains(effort)) p.DefaultEffort = effort;
        if (patch.DefaultPermissionMode == "") p.DefaultPermissionMode = null;
        else if (patch.DefaultPermissionMode is { } mode && StateValues.PermissionModes.Contains(mode)) p.DefaultPermissionMode = mode;
        if (patch.Profiles is { } profiles) p.Profiles = CleanProfiles(profiles);
        if (patch.MakeDefaultProfile is { } defaultId && p.Profiles?.FirstOrDefault(x => x.Id == defaultId) is { } chosen)
        {
            var set = p.ProviderDefaults ??= new ProviderDefaultsSet();
            var defaults = new ProviderDefaults { Model = chosen.Model, Effort = chosen.Effort, PermissionMode = chosen.PermissionMode, AddDirs = chosen.AddDirs?.ToList() };
            if (chosen.Provider == "anthropic") set.Anthropic = defaults;
            else set.Chatgpt = defaults;
        }
        return Snapshot(p) != before;
    }

    public const int MaxProfiles = 40;

    /// <summary>The profiles that pass: a known provider, a name (at most 60 characters), valid effort and permission mode, a fresh id when
    /// one is missing or repeated; null when none is left.</summary>
    public static List<LaunchProfile>? CleanProfiles(IEnumerable<LaunchProfile> profiles)
    {
        var result = new List<LaunchProfile>();
        foreach (var raw in profiles)
        {
            if (result.Count >= MaxProfiles) break;
            var name = Clip(raw.Name.Trim(), 60);
            if (name.Length == 0 || !StateValues.Providers.Contains(raw.Provider)) continue;
            var profile = raw.Clone();
            profile.Name = name;
            profile.Id = profile.Id.Length > 0 && result.All(x => x.Id != profile.Id) ? profile.Id : Guid.NewGuid().ToString("N")[..12];
            profile.Model = profile.Model is { } m && Clip(m.Trim(), 100) is { Length: > 0 } model ? model : null;
            profile.Effort = profile.Effort is { } e && StateValues.EffortLevels.Contains(e) ? e : null;
            profile.PermissionMode = profile.PermissionMode is { } pm && StateValues.PermissionModes.Contains(pm) ? pm : null;
            profile.AddDirs = NewSession.CleanAddDirs(profile.AddDirs);
            result.Add(profile);
        }
        return result.Count > 0 ? result : null;
    }

    static string ProfilesText(Preferences p) => string.Join('\u0002', (p.Profiles ?? []).Select(x =>
        string.Join('\u0001', x.Id, x.Name, x.Provider, x.Model, x.Effort, x.PermissionMode, string.Join(';', x.AddDirs ?? []))));

    static string DefaultsText(ProviderDefaults? d) => d is null ? "" : string.Join('\u0001', d.Model, d.Effort, d.PermissionMode, string.Join(';', d.AddDirs ?? []));

    static string Snapshot(Preferences p) => string.Join('\u0001', p.ArchiveRoot, p.NameFromFolder, p.Language, p.FontSize, p.SidebarWidth,
        p.SnapToGrid, p.CheckForUpdates, p.LookInWsl, p.GpuAcceleration, p.PtyHost, p.CloseWindow, p.ClaudeBin, p.DefaultModel,
        p.DefaultEffort, p.DefaultPermissionMode, p.WakeForTasks, p.CloseEndedAfterHours, ProfilesText(p),
        DefaultsText(p.ProviderDefaults?.Anthropic), DefaultsText(p.ProviderDefaults?.Chatgpt));

    /// <summary>The claude sessions would run, as Settings shows it (findClaude's source, resolve.ts): the chosen one
    /// while it is there (null when it is not any more), else the first <c>claude</c> with a PATHEXT extension on the PATH.</summary>
    public static string? FindClaude(string? chosen, string? pathVariable, string? pathExt, Func<string, bool>? isFile = null)
    {
        isFile ??= File.Exists;
        if (!string.IsNullOrEmpty(chosen)) return isFile(chosen) ? chosen : null;
        var exts = (string.IsNullOrEmpty(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in (pathVariable ?? "").Split(';'))
        {
            var dir = raw.Trim();
            if (dir.Length >= 2 && dir[0] == '"' && dir[^1] == '"') dir = dir[1..^1];
            if (dir.Length == 0) continue;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, "claude" + ext.ToLowerInvariant());
                if (isFile(candidate)) return candidate;
            }
        }
        return null;
    }
}
