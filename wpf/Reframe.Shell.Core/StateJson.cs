using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>
/// The state as JSON, keys in the schema's order (as zod gives them, and so as reframe writes them). With
/// <c>keepUnknown</c> the keys the schema does not know follow each object's own; without, the result is what the TS
/// parser makes of the same file (the shared fixtures' form).
/// </summary>
public static class StateJson
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string ToText(AppState state, bool keepUnknown = true) => Write(state, keepUnknown).ToJsonString(Indented);

    public static JsonObject Write(AppState state, bool keepUnknown = true)
    {
        var w = new Writer(keepUnknown);
        var sessions = new JsonObject();
        foreach (var (key, session) in state.Sessions) sessions[key] = w.Session(session);
        var o = new JsonObject
        {
            ["version"] = state.Version,
            ["sessions"] = sessions,
            ["windows"] = new JsonArray(state.Windows.Select(w.Window).ToArray()),
            ["recentFolders"] = Strings(state.RecentFolders),
            ["preferences"] = w.Preferences(state.Preferences),
        };
        Opt(o, "pinnedChats", state.PinnedChats);
        Opt(o, "expandedProjects", state.ExpandedProjects);
        if (state.FolderWorkspaces is { } folders)
            o["folderWorkspaces"] = new JsonArray(folders.Select(w.FolderWorkspace).ToArray());
        if (state.Archives is { } archives) o["archives"] = new JsonArray(archives.Select(w.Archive).ToArray());
        Opt(o, "wslDistros", state.WslDistros);
        o["hiddenThreads"] = new JsonArray(state.HiddenThreads.Select(w.HiddenThread).ToArray());
        return w.Done(o, state);
    }

    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    static JsonNode? Num(double value) => JsonValue.Create(value);

    static void Opt(JsonObject o, string key, string? value) { if (value is not null) o[key] = value; }
    static void Opt(JsonObject o, string key, bool? value) { if (value is { } v) o[key] = v; }
    static void Opt(JsonObject o, string key, double? value) { if (value is { } v) o[key] = Num(v); }
    static void Opt(JsonObject o, string key, List<string>? value) { if (value is not null) o[key] = Strings(value); }

    sealed class Writer(bool keepUnknown)
    {
        public JsonObject Done(JsonObject o, StateObject source)
        {
            if (keepUnknown)
                foreach (var (key, value) in source.Extra)
                    if (!o.ContainsKey(key)) o[key] = value?.DeepClone();
            return o;
        }

        public JsonNode Session(SessionRecord s)
        {
            var o = new JsonObject { ["id"] = s.Id, ["claudeSessionId"] = s.ClaudeSessionId, ["cwd"] = s.Cwd };
            Opt(o, "name", s.Name);
            if (s.HasClaudeName) o["claudeName"] = s.ClaudeName;
            o["launch"] = Launch(s.Launch);
            o["lifecycle"] = s.Lifecycle;
            o["createdAt"] = Num(s.CreatedAt);
            o["lastActiveAt"] = Num(s.LastActiveAt);
            Opt(o, "pid", s.Pid);
            Opt(o, "linuxPid", s.LinuxPid);
            Opt(o, "used", s.Used);
            return Done(o, s);
        }

        JsonNode Launch(LaunchOptions l)
        {
            var o = new JsonObject();
            Opt(o, "provider", l.Provider);
            Opt(o, "model", l.Model);
            Opt(o, "effort", l.Effort);
            Opt(o, "permissionMode", l.PermissionMode);
            Opt(o, "worktree", l.Worktree);
            Opt(o, "worktreeName", l.WorktreeName);
            Opt(o, "addDirs", l.AddDirs);
            Opt(o, "initialPrompt", l.InitialPrompt);
            Opt(o, "worktreeCreated", l.WorktreeCreated);
            return Done(o, l);
        }

        public JsonNode Window(WindowState w)
        {
            var o = new JsonObject
            {
                ["id"] = w.Id,
                ["bounds"] = w.Bounds is { } b
                    ? Done(new JsonObject { ["x"] = Num(b.X), ["y"] = Num(b.Y), ["width"] = Num(b.Width), ["height"] = Num(b.Height) }, b)
                    : null,
                ["maximized"] = w.Maximized,
            };
            Opt(o, "fullscreen", w.Fullscreen);
            o["hidden"] = w.Hidden;
            o["layout"] = w.Layout is { } layout ? Layout(layout) : null;
            o["dock"] = new JsonArray(w.Dock.Select(Dock).ToArray());
            Opt(o, "sidebarOpen", w.SidebarOpen);
            Opt(o, "activeProject", w.ActiveProject);
            Opt(o, "viewMode", w.ViewMode);
            return Done(o, w);
        }

        JsonNode Layout(LayoutNode node) => node switch
        {
            TileNode t => Done(new JsonObject { ["kind"] = "tile", ["sessionId"] = t.SessionId }, t),
            SplitNode s => Done(new JsonObject
            {
                ["kind"] = "split",
                ["id"] = s.Id,
                ["dir"] = s.Dir,
                ["children"] = new JsonArray(s.Children.Select(Layout).ToArray()),
                ["sizes"] = new JsonArray(s.Sizes.Select(Num).ToArray()),
            }, s),
            GroupNode g => Group(g),
            _ => throw new ArgumentException($"unknown layout node {node.GetType().Name}"),
        };

        JsonObject Group(GroupNode g) => Done(new JsonObject
        {
            ["kind"] = "group", ["id"] = g.Id, ["name"] = g.Name, ["color"] = g.Color, ["child"] = Layout(g.Child),
        }, g);

        JsonNode? Hint(RestoreHint? h)
        {
            if (h is null) return null;
            var o = new JsonObject { ["anchor"] = h.Anchor, ["side"] = h.Side, ["ratio"] = Num(h.Ratio) };
            Opt(o, "share", h.Share);
            return Done(o, h);
        }

        JsonNode Dock(DockEntry entry) => entry switch
        {
            DockTile t => Done(new JsonObject { ["kind"] = "tile", ["sessionId"] = t.SessionId, ["hint"] = Hint(t.Hint) }, t),
            DockGroup g => Done(new JsonObject { ["kind"] = "group", ["node"] = Group(g.Node), ["hint"] = Hint(g.Hint) }, g),
            _ => throw new ArgumentException($"unknown dock entry {entry.GetType().Name}"),
        };

        JsonNode ProviderDefaults(ProviderDefaults d)
        {
            var o = new JsonObject();
            Opt(o, "model", d.Model);
            Opt(o, "effort", d.Effort);
            Opt(o, "permissionMode", d.PermissionMode);
            Opt(o, "addDirs", d.AddDirs);
            return Done(o, d);
        }

        public JsonNode Preferences(Preferences p)
        {
            var o = new JsonObject();
            Opt(o, "archiveRoot", p.ArchiveRoot);
            o["nameFromFolder"] = p.NameFromFolder;
            o["language"] = p.Language;
            o["fontSize"] = p.FontSize;
            Opt(o, "sidebarWidth", p.SidebarWidth);
            o["snapToGrid"] = p.SnapToGrid;
            o["checkForUpdates"] = p.CheckForUpdates;
            o["lookInWsl"] = p.LookInWsl;
            o["closeWindow"] = p.CloseWindow;
            o["gpuAcceleration"] = p.GpuAcceleration;
            o["ptyHost"] = p.PtyHost;
            Opt(o, "claudeBin", p.ClaudeBin);
            Opt(o, "defaultModel", p.DefaultModel);
            Opt(o, "defaultEffort", p.DefaultEffort);
            Opt(o, "defaultPermissionMode", p.DefaultPermissionMode);
            if (p.ProviderDefaults is { } set)
            {
                var d = new JsonObject();
                if (set.Chatgpt is { } c) d["chatgpt"] = ProviderDefaults(c);
                if (set.Anthropic is { } a) d["anthropic"] = ProviderDefaults(a);
                o["providerDefaults"] = Done(d, set);
            }
            return Done(o, p);
        }

        public JsonNode HiddenThread(HiddenThread h)
        {
            var o = new JsonObject { ["claudeSessionId"] = h.ClaudeSessionId, ["label"] = h.Label, ["cwd"] = h.Cwd };
            Opt(o, "distro", h.Distro);
            o["hiddenAt"] = Num(h.HiddenAt);
            return Done(o, h);
        }

        public JsonNode FolderWorkspace(FolderWorkspace f)
        {
            var o = new JsonObject { ["id"] = f.Id, ["cwd"] = f.Cwd, ["name"] = f.Name, ["sessionIds"] = Strings(f.SessionIds) };
            Opt(o, "provider", f.Provider);
            if (f.Locations is { } locations)
                o["locations"] = new JsonArray(locations.Select(l => (JsonNode?)Done(new JsonObject
                {
                    ["cwd"] = l.Cwd, ["provider"] = l.Provider, ["sessionIds"] = Strings(l.SessionIds),
                }, l)).ToArray());
            return Done(o, f);
        }

        public JsonNode Archive(ArchiveEntry a)
        {
            var o = new JsonObject { ["id"] = a.Id, ["label"] = a.Label, ["cwd"] = a.Cwd };
            Opt(o, "provider", a.Provider);
            o["archive"] = a.Archive;
            o["sha256"] = a.Sha256;
            o["operation"] = a.Operation;
            Opt(o, "partial", a.Partial);
            return Done(o, a);
        }
    }
}
