using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

// The C# model of reframe's state.json, a port of src/shared/state.ts (the zod schema) for the WPF shell. Parsing
// (StateParser) accepts and rejects exactly what the TS parser does (src/main/state/store.ts `parse`), and fills in
// the same defaults for older files; test/state-fixtures holds the TS parser's results that the shell's tests check
// this port against. Unlike zod, which strips keys it does not know, every object keeps them in `Extra`, so a file
// written by a newer Electron build round-trips through the shell (StateJson.Write) without losing them.
// Enum values stay strings (checked against the allowed sets in StateValues), so they round-trip unchanged too.

/// <summary>Keys an object had that the schema does not know, in file order, kept for round-tripping.</summary>
public abstract class StateObject
{
    public Dictionary<string, JsonNode?> Extra { get; } = [];
}

/// <summary>The allowed values of the schema's enums (src/shared/state.ts, launch.ts, providers.ts).</summary>
public static class StateValues
{
    public const int StateVersion = 1;
    public static readonly IReadOnlyList<string> Providers = ["chatgpt", "anthropic"];
    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];
    public static readonly IReadOnlyList<string> PermissionModes = ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"];
    public static readonly IReadOnlyList<string> Sides = ["left", "right", "top", "bottom"];
    public static readonly IReadOnlyList<string> SplitDirections = ["row", "col"];
    public static readonly IReadOnlyList<string> ViewModes = ["single", "tiled"];
    public static readonly IReadOnlyList<string> Lifecycles = ["open", "ended"];
    public static readonly IReadOnlyList<string> Languages = ["system", "en", "de"];
    public static readonly IReadOnlyList<string> CloseWindow = ["ask", "end", "keep"];
    public static readonly IReadOnlyList<string> PtyHosts = ["node", "rust"];
    public const int FontSizeNormal = 13, FontSizeMin = 8, FontSizeMax = 32;
    public const int SidebarWidthNormal = 270, SidebarWidthMin = 200, SidebarWidthMax = 640;

    /// <summary>clampSidebarWidth: the stored width is clamped when used, never rejected.</summary>
    public static int ClampSidebarWidth(double? width) =>
        width is { } w && double.IsFinite(w)
            ? (int)Math.Round(Math.Min(SidebarWidthMax, Math.Max(SidebarWidthMin, w)), MidpointRounding.AwayFromZero)
            : SidebarWidthNormal;

    /// <summary>sessionLabel (src/shared/labels.ts): the user's name, else Claude Code's title, else the folder's name.</summary>
    public static string SessionLabel(string? name, string? title, string cwd)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        return cwd.Split('\\', '/').LastOrDefault(p => p.Length > 0) ?? cwd;
    }
}

public sealed class AppState : StateObject
{
    public int Version { get; set; } = StateValues.StateVersion;
    /// <summary>By reframe session id, in file order.</summary>
    public Dictionary<string, SessionRecord> Sessions { get; set; } = [];
    public List<WindowState> Windows { get; set; } = [];
    public List<string> RecentFolders { get; set; } = [];
    /// <summary>Defaulted for files from before preferences existed.</summary>
    public Preferences Preferences { get; set; } = new();
    /// <summary>Pinned chats, by Claude session id.</summary>
    public List<string>? PinnedChats { get; set; }
    public List<string>? ExpandedProjects { get; set; }
    /// <summary>Read and dropped by reframe (0.10.0-claudegpt.4 stored the derived project groups).</summary>
    public List<FolderWorkspace>? FolderWorkspaces { get; set; }
    public List<ArchiveEntry>? Archives { get; set; }
    public List<string>? WslDistros { get; set; }
    /// <summary>Threads hidden from reframe (none for files from before 0.10.1-claudegpt.27).</summary>
    public List<HiddenThread> HiddenThreads { get; set; } = [];

    /// <summary>defaultState(): one empty window, default preferences.</summary>
    public static AppState CreateDefault() => new() { Windows = [WindowState.Empty(Guid.NewGuid().ToString())] };
}

public sealed class SessionRecord : StateObject
{
    public string Id { get; set; } = "";
    /// <summary>Changes on /clear.</summary>
    public string ClaudeSessionId { get; set; } = "";
    public string Cwd { get; set; } = "";
    /// <summary>The name reframe shows; without one, Claude Code's name for the session.</summary>
    public string? Name { get; set; }
    /// <summary>The --name Claude Code gets: absent (<see cref="HasClaudeName"/> false), null for none, or a name.</summary>
    public string? ClaudeName { get; set; }
    public bool HasClaudeName { get; set; }
    public LaunchOptions Launch { get; set; } = new();
    /// <summary>"open" or "ended".</summary>
    public string Lifecycle { get; set; } = "open";
    public double CreatedAt { get; set; }
    /// <summary>When it was last used (a prompt, or Claude done or asking), in ms since the epoch.</summary>
    public double LastActiveAt { get; set; }
    public double? Pid { get; set; }
    public double? LinuxPid { get; set; }
    public bool? Used { get; set; }
}

public sealed class LaunchOptions : StateObject
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? PermissionMode { get; set; }
    public bool? Worktree { get; set; }
    public string? WorktreeName { get; set; }
    public List<string>? AddDirs { get; set; }
    public string? InitialPrompt { get; set; }
    public bool? WorktreeCreated { get; set; }
}

public sealed class Bounds : StateObject
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class WindowState : StateObject
{
    public string Id { get; set; } = "";
    public Bounds? Bounds { get; set; }
    public bool Maximized { get; set; }
    public bool? Fullscreen { get; set; }
    public bool Hidden { get; set; }
    public LayoutNode? Layout { get; set; }
    public List<DockEntry> Dock { get; set; } = [];
    public bool? SidebarOpen { get; set; }
    public string? ActiveProject { get; set; }
    /// <summary>"single" or "tiled"; absent (older files) means tiled.</summary>
    public string? ViewMode { get; set; }

    public static WindowState Empty(string id) => new() { Id = id };
}

public abstract class LayoutNode : StateObject;

public sealed class TileNode : LayoutNode
{
    public string SessionId { get; set; } = "";
}

public sealed class SplitNode : LayoutNode
{
    public string Id { get; set; } = "";
    /// <summary>"row" or "col".</summary>
    public string Dir { get; set; } = "row";
    /// <summary>At least two.</summary>
    public List<LayoutNode> Children { get; set; } = [];
    public List<double> Sizes { get; set; } = [];
}

public sealed class GroupNode : LayoutNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public LayoutNode Child { get; set; } = null!;
}

public sealed class RestoreHint : StateObject
{
    public string Anchor { get; set; } = "";
    public string Side { get; set; } = "left";
    public double Ratio { get; set; }
    public double? Share { get; set; }
}

public abstract class DockEntry : StateObject
{
    public RestoreHint? Hint { get; set; }
}

public sealed class DockTile : DockEntry
{
    public string SessionId { get; set; } = "";
}

public sealed class DockGroup : DockEntry
{
    public GroupNode Node { get; set; } = null!;
}

public sealed class ProviderDefaults : StateObject
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? PermissionMode { get; set; }
    public List<string>? AddDirs { get; set; }
}

public sealed class ProviderDefaultsSet : StateObject
{
    public ProviderDefaults? Chatgpt { get; set; }
    public ProviderDefaults? Anthropic { get; set; }
}

public sealed class Preferences : StateObject
{
    public string? ArchiveRoot { get; set; }
    public bool NameFromFolder { get; set; }
    public string Language { get; set; } = "system";
    public int FontSize { get; set; } = StateValues.FontSizeNormal;
    /// <summary>As stored; use <see cref="StateValues.ClampSidebarWidth"/>.</summary>
    public double? SidebarWidth { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool LookInWsl { get; set; }
    public string CloseWindow { get; set; } = "ask";
    public bool GpuAcceleration { get; set; }
    public string PtyHost { get; set; } = "node";
    public string? ClaudeBin { get; set; }
    public string? DefaultModel { get; set; }
    public string? DefaultEffort { get; set; }
    public string? DefaultPermissionMode { get; set; }
    public ProviderDefaultsSet? ProviderDefaults { get; set; }
}

public sealed class HiddenThread : StateObject
{
    public string ClaudeSessionId { get; set; } = "";
    /// <summary>Its title when it was hidden.</summary>
    public string Label { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Distro { get; set; }
    public double HiddenAt { get; set; }
}

public sealed class FolderLocation : StateObject
{
    public string Cwd { get; set; } = "";
    public string Provider { get; set; } = "";
    public List<string> SessionIds { get; set; } = [];
}

public sealed class FolderWorkspace : StateObject
{
    public string Id { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> SessionIds { get; set; } = [];
    public string? Provider { get; set; }
    public List<FolderLocation>? Locations { get; set; }
}

public sealed class ArchiveEntry : StateObject
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Provider { get; set; }
    public string Archive { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Operation { get; set; } = "";
    public bool? Partial { get; set; }
}
