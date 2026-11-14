using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>A context menu item as Electron's Menu has it (src/main/menus.ts): type <c>normal</c>, <c>separator</c>,
/// <c>submenu</c>, <c>checkbox</c> or <c>radio</c>. <see cref="Action"/> names what a click does in the shell.</summary>
public sealed record MenuSpec(string Type, string Label = "", bool Enabled = true, bool? Checked = null,
    IReadOnlyList<MenuSpec>? Submenu = null, string? Action = null, string? Accelerator = null)
{
    public static readonly MenuSpec Separator = new("separator");

    /// <summary>The structured form both sides dump for the visual report: type, label, enabled, checked, accelerator, submenu.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["type"] = Type };
        if (Type == "separator") return o;
        o["label"] = Label;
        o["enabled"] = Enabled;
        if (Type is "checkbox" or "radio") o["checked"] = Checked ?? false;
        if (Accelerator is not null) o["accelerator"] = Accelerator;
        if (Submenu is not null) o["submenu"] = new JsonArray([.. Submenu.Select(s => (JsonNode)s.ToJson())]);
        return o;
    }

    public static JsonArray ToJson(IEnumerable<MenuSpec> items) => new([.. items.Select(i => (JsonNode)i.ToJson())]);
}

/// <summary>What chatActions (src/main/app.ts) looks at for one list entry or tile.</summary>
/// <param name="Record">The session's record, for an entry of reframe's (null: a found or running chat).</param>
/// <param name="Hideable">Not a session of reframe's that still runs or is open (it must be ended first).</param>
/// <param name="Wsl">The chat lives in WSL (a found one with a distro, or a record whose folder is in WSL).</param>
/// <param name="Inactive">No reframe session of it runs and no history operation is going on.</param>
public sealed record ChatMenuContext(ListItemKind Kind, SessionRecord? Record, string ChatKey, bool Pinned, bool Hideable,
    bool Wsl, bool Inactive, bool BatchRunning = false);

/// <summary>The context menus of src/main/menus.ts and app.ts (English texts of src/shared/i18n.ts), as specs.</summary>
public static class ContextMenus
{
    static string ProviderName(string p) => p == "chatgpt" ? "ChatGPT" : p == "anthropic" ? "Anthropic" : "Provider not selected";

    /// <summary>chatActions: pin, hide and, for an inactive chat on Windows, provider, model, archive and delete.</summary>
    public static List<MenuSpec> ChatActions(ChatMenuContext c)
    {
        if (c.Kind == ListItemKind.App && c.Record is null) return [];
        List<MenuSpec> pin =
        [
            MenuSpec.Separator,
            new("normal", c.Pinned ? "Unpin chat" : "Pin chat", Action: "pin"),
            new("normal", c.Hideable ? "Hide from Reframe…" : "Hide from Reframe (end chat first)", c.Hideable, Action: "hide"),
        ];
        if (c.Kind == ListItemKind.Running) return [new("normal", "Take over…", Action: "takeOver"), .. pin];
        var tid = c.Record?.ClaudeSessionId ?? (c.Kind == ListItemKind.Found ? SidebarChats.ParseChatKey(c.ChatKey)?.Id : null);
        if (string.IsNullOrEmpty(tid) || c.Wsl) return pin;
        var current = c.Record?.Launch.Provider ?? (c.Record is not null ? "chatgpt" : null);
        return
        [
            .. pin,
            MenuSpec.Separator,
            new("submenu", c.Inactive ? "Model provider" : "Model provider (end chat first)", Submenu:
                [.. StateValues.Providers.Select(p => new MenuSpec("radio", ProviderName(p), c.Inactive, current == p, Action: "provider:" + p))]),
            new("normal", "Change model…", c.Inactive && !c.BatchRunning, Action: "changeModel"),
            new("normal", c.Inactive ? "Archive chat…" : "Archive chat (end chat first)", c.Inactive, Action: "archive"),
            new("normal", c.Inactive ? "Delete chat…" : "Delete chat (end chat first)", c.Inactive, Action: "delete"),
        ];
    }

    /// <summary>batchChatActions: the selection's provider (no radio: they may differ), model, archive and delete.</summary>
    public static List<MenuSpec> BatchChatActions(bool batchRunning) =>
    [
        MenuSpec.Separator,
        new("submenu", "Model provider", !batchRunning, Submenu:
            [.. StateValues.Providers.Select(p => new MenuSpec("normal", ProviderName(p), Action: "batchProvider:" + p))]),
        new("normal", "Change model…", !batchRunning, Action: "batchModel"),
        new("normal", "Archive selected…", !batchRunning, Action: "batchArchive"),
        new("normal", "Delete selected…", !batchRunning, Action: "batchDelete"),
    ];

    /// <summary>listMenu: open, rename (a single session of reframe's), the chat actions, then the group openings.</summary>
    public static List<MenuSpec> ListMenu(int count, bool rename, IEnumerable<MenuSpec> chatActions) =>
    [
        new("normal", count > 1 ? $"Open {count} sessions" : "Open", Action: "open"),
        .. rename ? [new MenuSpec("normal", "Rename", Action: "rename")] : Array.Empty<MenuSpec>(),
        .. chatActions,
        MenuSpec.Separator,
        new("normal", "Open in a new group", Action: "group"),
        new("normal", "Open in a new group in a new window", Action: "groupInNewWindow"),
        .. count == 1 ? [MenuSpec.Separator, Properties] : Array.Empty<MenuSpec>(),
    ];

    /// <summary>The thread's properties (the WPF shell's: ids, dates, process, disk usage).</summary>
    public static readonly MenuSpec Properties = new("normal", "Properties…", Action: "properties");

    /// <summary>tileMenu: rename, the chat actions, then new group / add to group (the window's other groups) / remove from group.</summary>
    public static List<MenuSpec> TileMenu(IEnumerable<MenuSpec> chatActions, bool inGroup, IReadOnlyList<GroupNode> otherGroups) =>
    [
        new("normal", "Rename", Action: "rename"),
        .. chatActions,
        MenuSpec.Separator,
        new("normal", "New group", !inGroup, Action: "newGroup"),
        new("submenu", "Add to group", otherGroups.Count > 0, Submenu: [.. otherGroups.Select(g => new MenuSpec("normal", g.Name, Action: "addToGroup:" + g.Id))]),
        new("normal", "Remove from group", inGroup, Action: "removeFromGroup"),
        MenuSpec.Separator,
        Properties,
    ];

    /// <summary>colorItems: the palette as checkboxes, the group's colour checked.</summary>
    public static List<MenuSpec> ColorMenu(GroupNode group) =>
        [.. GroupColors.All.Select(c => new MenuSpec("checkbox", c.Label, Checked: c.Hex == group.Color, Action: "color:" + c.Hex))];

    /// <summary>groupMenu: rename, colour, dissolve, end all.</summary>
    public static List<MenuSpec> GroupMenu(GroupNode group) =>
    [
        new("normal", "Rename", Action: "renameGroup"),
        new("submenu", "Color", Submenu: ColorMenu(group)),
        MenuSpec.Separator,
        new("normal", "Dissolve group", Action: "dissolve"),
        new("normal", "End all sessions", Action: "endAll"),
    ];
}
