using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

public enum StateParseKind { State, Newer, Rejected }

/// <summary>What a state file's text holds: a state, one from a newer reframe, or nothing usable (and why).</summary>
public sealed record StateParseResult(StateParseKind Kind, AppState? State = null, string? Reason = null);

/// <summary>The schema's checks, failing at the first problem (where, and what).</summary>
public sealed class StateFormatException(string path, string message) : Exception($"{(path.Length > 0 ? path : "(file)")}: {message}")
{
    public string Path { get; } = path;
}

/// <summary>
/// Parses state.json the way src/main/state/store.ts `parse` does with appStateSchema (zod 4): the same files are
/// accepted, rejected or taken for a newer reframe's, and the same defaults are filled in. Unknown keys are kept
/// (see <see cref="StateObject.Extra"/>) where zod drops them.
/// </summary>
public static class StateParser
{
    static readonly JsonDocumentOptions Options = new() { MaxDepth = 1000 };

    public static StateParseResult Parse(string text)
    {
        // JSON.parse does not skip a byte order mark (the file is read as UTF-8 text with it); JsonDocument would.
        if (text.Length > 0 && text[0] == '﻿') return new(StateParseKind.Rejected, Reason: "not JSON (byte order mark)");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Options);
        }
        catch (JsonException error)
        {
            return new(StateParseKind.Rejected, Reason: $"not JSON ({error.Message})");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Props(root).TryGetValue("version", out var version)
                && version.ValueKind == JsonValueKind.Number && version.GetDouble() > StateValues.StateVersion)
                return new(StateParseKind.Newer);
            try
            {
                return new(StateParseKind.State, ReadState(root));
            }
            catch (StateFormatException error)
            {
                return new(StateParseKind.Rejected, Reason: error.Message);
            }
        }
    }

    /// <summary>An object's properties; a repeated key's last value wins, at its first place (as JSON.parse).</summary>
    static Dictionary<string, JsonElement> Props(JsonElement element)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject()) props[p.Name] = p.Value;
        return props;
    }

    /// <summary>One object being read: the keys taken by the schema; the rest go to Extra.</summary>
    sealed class Obj
    {
        readonly Dictionary<string, JsonElement> props;
        readonly HashSet<string> known = new(StringComparer.Ordinal);
        public string Path { get; }

        public Obj(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new StateFormatException(path, "expected object");
            props = Props(element);
            Path = path;
        }

        public string At(string key) => Path.Length == 0 ? key : $"{Path}.{key}";

        /// <summary>The value, or null when the key is not there (JSON has no undefined).</summary>
        public JsonElement? Get(string key)
        {
            known.Add(key);
            return props.TryGetValue(key, out var v) ? v : null;
        }

        public JsonElement Required(string key) => Get(key) ?? throw new StateFormatException(At(key), "required");

        public void KeepExtra(StateObject target)
        {
            foreach (var (key, value) in props)
                if (!known.Contains(key)) target.Extra[key] = ToNode(value);
        }

        public string Str(string key) => String(Required(key), At(key));
        public string? OptStr(string key) => Get(key) is { } v ? String(v, At(key)) : null;
        public double Num(string key) => Number(Required(key), At(key));
        public double? OptNum(string key) => Get(key) is { } v ? Number(v, At(key)) : null;
        public bool Bool(string key) => Boolean(Required(key), At(key));
        public bool? OptBool(string key) => Get(key) is { } v ? Boolean(v, At(key)) : null;
        public string Enum(string key, IReadOnlyList<string> values) => EnumValue(Required(key), At(key), values);
        public string? OptEnum(string key, IReadOnlyList<string> values) => Get(key) is { } v ? EnumValue(v, At(key), values) : null;
        public List<string> Strings(string key) => StringList(Required(key), At(key));
        public List<string>? OptStrings(string key) => Get(key) is { } v ? StringList(v, At(key)) : null;

        public List<T>? OptArray<T>(string key, Func<JsonElement, string, T> item) =>
            Get(key) is { } v ? ListOf(v, At(key), item) : null;
        public List<T> Array<T>(string key, Func<JsonElement, string, T> item) => ListOf(Required(key), At(key), item);
    }

    /// <summary>A kept value as a node (a repeated key's last value wins here too).</summary>
    internal static JsonNode? ToNode(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(Props(v).Select(p => KeyValuePair.Create(p.Key, ToNode(p.Value)))),
        JsonValueKind.Array => new JsonArray(v.EnumerateArray().Select(ToNode).ToArray()),
        JsonValueKind.Null => null,
        _ => JsonValue.Create(v.Clone()),
    };

    static string String(JsonElement v, string path) =>
        v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new StateFormatException(path, "expected string");

    static double Number(JsonElement v, string path)
    {
        if (v.ValueKind != JsonValueKind.Number) throw new StateFormatException(path, "expected number");
        // A number too big for a double is Infinity to JSON.parse, which z.number() refuses.
        if (!v.TryGetDouble(out var d) || !double.IsFinite(d)) throw new StateFormatException(path, "expected number, received Infinity");
        return d == 0 ? 0 : d; // -0 is written as 0 by JSON.stringify.
    }

    static bool Boolean(JsonElement v, string path) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new StateFormatException(path, "expected boolean"),
    };

    static string EnumValue(JsonElement v, string path, IReadOnlyList<string> values)
    {
        var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : null;
        return s is not null && values.Contains(s) ? s : throw new StateFormatException(path, $"expected one of {string.Join("|", values)}");
    }

    static List<string> StringList(JsonElement v, string path) => ListOf(v, path, String);

    static List<T> ListOf<T>(JsonElement v, string path, Func<JsonElement, string, T> item)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new StateFormatException(path, "expected array");
        var list = new List<T>();
        var i = 0;
        foreach (var e in v.EnumerateArray()) list.Add(item(e, $"{path}.{i++}"));
        return list;
    }

    static T Nullable<T>(JsonElement v, Func<JsonElement, string, T> read, string path) where T : class? =>
        v.ValueKind == JsonValueKind.Null ? null! : read(v, path);

    static AppState ReadState(JsonElement element)
    {
        var o = new Obj(element, "");
        var version = o.Required("version");
        if (version.ValueKind != JsonValueKind.Number || version.GetDouble() != StateValues.StateVersion)
            throw new StateFormatException("version", "expected 1");
        var state = new AppState { Version = StateValues.StateVersion };
        var sessions = o.Required("sessions");
        if (sessions.ValueKind != JsonValueKind.Object) throw new StateFormatException("sessions", "expected record");
        foreach (var (key, value) in Props(sessions)) state.Sessions[key] = ReadSession(value, $"sessions.{key}");
        state.Windows = o.Array("windows", ReadWindow);
        state.RecentFolders = o.Strings("recentFolders");
        state.Preferences = o.Get("preferences") is { } p ? ReadPreferences(p, "preferences") : new Preferences();
        state.PinnedChats = o.OptStrings("pinnedChats");
        state.ExpandedProjects = o.OptStrings("expandedProjects");
        state.FolderWorkspaces = o.OptArray("folderWorkspaces", ReadFolderWorkspace);
        state.Archives = o.OptArray("archives", ReadArchive);
        state.WslDistros = o.OptStrings("wslDistros");
        state.HiddenThreads = o.OptArray("hiddenThreads", ReadHiddenThread) ?? [];
        state.TakeoverExclusions = o.OptArray("takeoverExclusions", ReadTakeoverExclusion);
        o.KeepExtra(state);
        return state;
    }

    static SessionRecord ReadSession(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var s = new SessionRecord
        {
            Id = o.Str("id"),
            ClaudeSessionId = o.Str("claudeSessionId"),
            Cwd = o.Str("cwd"),
            Name = o.OptStr("name"),
        };
        if (o.Get("claudeName") is { } claudeName)
        {
            s.HasClaudeName = true;
            s.ClaudeName = Nullable(claudeName, String, o.At("claudeName"));
        }
        s.Launch = ReadLaunch(o.Required("launch"), o.At("launch"));
        s.Lifecycle = o.Enum("lifecycle", StateValues.Lifecycles);
        s.CreatedAt = o.Num("createdAt");
        s.LastActiveAt = o.Num("lastActiveAt");
        s.Pid = o.OptNum("pid");
        s.LinuxPid = o.OptNum("linuxPid");
        s.Used = o.OptBool("used");
        o.KeepExtra(s);
        return s;
    }

    static LaunchOptions ReadLaunch(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var l = new LaunchOptions
        {
            Provider = o.OptEnum("provider", StateValues.Providers),
            Model = o.OptStr("model"),
            Effort = o.OptEnum("effort", StateValues.EffortLevels),
            PermissionMode = o.OptEnum("permissionMode", StateValues.PermissionModes),
            Worktree = o.OptBool("worktree"),
            WorktreeName = o.OptStr("worktreeName"),
            AddDirs = o.OptStrings("addDirs"),
            InitialPrompt = o.OptStr("initialPrompt"),
            WorktreeCreated = o.OptBool("worktreeCreated"),
        };
        o.KeepExtra(l);
        return l;
    }

    static WindowState ReadWindow(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var w = new WindowState
        {
            Id = o.Str("id"),
            Bounds = Nullable(o.Required("bounds"), ReadBounds, o.At("bounds")),
            Maximized = o.Bool("maximized"),
            Fullscreen = o.OptBool("fullscreen"),
            Hidden = o.Bool("hidden"),
            Layout = Nullable(o.Required("layout"), ReadLayout, o.At("layout")),
            Dock = o.Array("dock", ReadDock),
            SidebarOpen = o.OptBool("sidebarOpen"),
            ActiveProject = o.OptStr("activeProject"),
            ViewMode = o.OptEnum("viewMode", StateValues.ViewModes),
        };
        o.KeepExtra(w);
        return w;
    }

    static Bounds ReadBounds(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var b = new Bounds { X = o.Num("x"), Y = o.Num("y"), Width = o.Num("width"), Height = o.Num("height") };
        o.KeepExtra(b);
        return b;
    }

    /// <summary>A layout node as state.json has it (a visual fixture's layout).</summary>
    public static LayoutNode LayoutOf(JsonElement element) => ReadLayout(element, "layout");

    /// <summary>A dock entry as state.json has it (a visual fixture's dock).</summary>
    public static DockEntry DockOf(JsonElement element) => ReadDock(element, "dock");

    /// <summary>layoutNodeSchema: a union of tile, split and group, told apart by their literal kind.</summary>
    static LayoutNode ReadLayout(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var kind = o.Get("kind") is { ValueKind: JsonValueKind.String } k ? k.GetString() : null;
        LayoutNode node;
        switch (kind)
        {
            case "tile":
                node = new TileNode { SessionId = o.Str("sessionId") };
                break;
            case "split":
                var split = new SplitNode { Id = o.Str("id"), Dir = o.Enum("dir", StateValues.SplitDirections) };
                split.Children = o.Array("children", ReadLayout);
                if (split.Children.Count < 2) throw new StateFormatException(o.At("children"), "expected at least 2 items");
                split.Sizes = o.Array("sizes", Number);
                node = split;
                break;
            case "group":
                return ReadGroup(element, path);
            default:
                throw new StateFormatException(path, "invalid layout node");
        }
        o.KeepExtra(node);
        return node;
    }

    static GroupNode ReadGroup(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        if (o.Get("kind") is not { ValueKind: JsonValueKind.String } k || k.GetString() != "group")
            throw new StateFormatException(o.At("kind"), "expected \"group\"");
        var g = new GroupNode { Id = o.Str("id"), Name = o.Str("name"), Color = o.Str("color") };
        g.Child = ReadLayout(o.Required("child"), o.At("child"));
        o.KeepExtra(g);
        return g;
    }

    static RestoreHint ReadHint(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var h = new RestoreHint
        {
            Anchor = o.Str("anchor"),
            Side = o.Enum("side", StateValues.Sides),
            Ratio = o.Num("ratio"),
            Share = o.OptNum("share"),
        };
        o.KeepExtra(h);
        return h;
    }

    static DockEntry ReadDock(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var kind = o.Get("kind") is { ValueKind: JsonValueKind.String } k ? k.GetString() : null;
        DockEntry entry = kind switch
        {
            "tile" => new DockTile { SessionId = o.Str("sessionId") },
            "group" => new DockGroup { Node = ReadGroup(o.Required("node"), o.At("node")) },
            _ => throw new StateFormatException(path, "invalid dock entry"),
        };
        entry.Hint = Nullable(o.Required("hint"), ReadHint, o.At("hint"));
        o.KeepExtra(entry);
        return entry;
    }

    static ProviderDefaults ReadProviderDefaults(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var d = new ProviderDefaults
        {
            Model = o.OptStr("model"),
            Effort = o.OptEnum("effort", StateValues.EffortLevels),
            PermissionMode = o.OptEnum("permissionMode", StateValues.PermissionModes),
            AddDirs = o.OptStrings("addDirs"),
        };
        o.KeepExtra(d);
        return d;
    }

    static Preferences ReadPreferences(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var p = new Preferences
        {
            ArchiveRoot = o.OptStr("archiveRoot"),
            NameFromFolder = o.Bool("nameFromFolder"),
            Language = o.OptEnum("language", StateValues.Languages) ?? "system",
        };
        if (o.OptNum("fontSize") is { } size)
        {
            // z.number().int() (a safe integer in zod 4) between min and max.
            if (Math.Floor(size) != size || Math.Abs(size) > 9007199254740991) throw new StateFormatException(o.At("fontSize"), "expected int");
            if (size < StateValues.FontSizeMin || size > StateValues.FontSizeMax) throw new StateFormatException(o.At("fontSize"), "out of range");
            p.FontSize = (int)size;
        }
        p.SidebarWidth = o.OptNum("sidebarWidth");
        p.SnapToGrid = o.OptBool("snapToGrid") ?? true;
        p.CheckForUpdates = o.OptBool("checkForUpdates") ?? true;
        p.LookInWsl = o.OptBool("lookInWsl") ?? false;
        p.CloseWindow = o.OptEnum("closeWindow", StateValues.CloseWindow) ?? "ask";
        p.GpuAcceleration = o.OptBool("gpuAcceleration") ?? false;
        p.PtyHost = o.OptEnum("ptyHost", StateValues.PtyHosts) ?? "node";
        p.ClaudeBin = o.OptStr("claudeBin");
        p.DefaultModel = o.OptStr("defaultModel");
        p.DefaultEffort = o.OptEnum("defaultEffort", StateValues.EffortLevels);
        p.DefaultPermissionMode = o.OptEnum("defaultPermissionMode", StateValues.PermissionModes);
        if (o.Get("providerDefaults") is { } pd)
        {
            var po = new Obj(pd, o.At("providerDefaults"));
            var set = new ProviderDefaultsSet
            {
                Chatgpt = po.Get("chatgpt") is { } c ? ReadProviderDefaults(c, po.At("chatgpt")) : null,
                Anthropic = po.Get("anthropic") is { } a ? ReadProviderDefaults(a, po.At("anthropic")) : null,
            };
            po.KeepExtra(set);
            p.ProviderDefaults = set;
        }
        o.KeepExtra(p);
        return p;
    }

    static TakeoverExclusion ReadTakeoverExclusion(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var x = new TakeoverExclusion { ClaudeSessionId = o.Str("claudeSessionId"), Label = o.Str("label"), Cwd = o.Str("cwd"), ExcludedAt = o.Num("excludedAt") };
        o.KeepExtra(x);
        return x;
    }

    static HiddenThread ReadHiddenThread(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var h = new HiddenThread
        {
            ClaudeSessionId = o.Str("claudeSessionId"),
            Label = o.Str("label"),
            Cwd = o.Str("cwd"),
            Distro = o.OptStr("distro"),
            HiddenAt = o.Num("hiddenAt"),
        };
        o.KeepExtra(h);
        return h;
    }

    static FolderWorkspace ReadFolderWorkspace(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var f = new FolderWorkspace
        {
            Id = o.Str("id"),
            Cwd = o.Str("cwd"),
            Name = o.Str("name"),
            SessionIds = o.Strings("sessionIds"),
            Provider = o.OptEnum("provider", StateValues.Providers),
            Locations = o.OptArray("locations", (e, p) =>
            {
                var lo = new Obj(e, p);
                var l = new FolderLocation
                {
                    Cwd = lo.Str("cwd"),
                    Provider = lo.Enum("provider", StateValues.Providers),
                    SessionIds = lo.Strings("sessionIds"),
                };
                lo.KeepExtra(l);
                return l;
            }),
        };
        o.KeepExtra(f);
        return f;
    }

    static ArchiveEntry ReadArchive(JsonElement element, string path)
    {
        var o = new Obj(element, path);
        var a = new ArchiveEntry
        {
            Id = o.Str("id"),
            Label = o.Str("label"),
            Cwd = o.Str("cwd"),
            Provider = o.OptEnum("provider", StateValues.Providers),
            Archive = o.Str("archive"),
            Sha256 = o.Str("sha256"),
            Operation = o.Str("operation"),
            Partial = o.OptBool("partial"),
        };
        o.KeepExtra(a);
        return a;
    }
}
