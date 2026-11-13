using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>
/// Notifications as src/main/toast.ts and app.ts's notify() give them: the AppUserModelID the toasts are shown under,
/// the toast activator CLSID, the toast XML whose launch argument names the session, and the rules for when a session
/// that is done or needs the user gets a toast, a flashing taskbar button and a badge.
/// </summary>
public static class Notifications
{
    /// <summary>REFRAME_DISABLE_NOTIFICATIONS: set (to anything), no toast and no flashing.</summary>
    public const string DisableVariable = "REFRAME_DISABLE_NOTIFICATIONS";

    static readonly Regex AppIdPattern = new("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$");

    /// <summary>appUserModelId: the managed launcher's fixed REFRAME_APP_ID, else the executable path (the shell is
    /// never installed by electron-builder, so its <c>dev.reframe.app</c> does not apply).</summary>
    public static string AppUserModelId(string? fixedId, string executablePath)
    {
        var id = fixedId?.Trim();
        return !string.IsNullOrEmpty(id) && AppIdPattern.IsMatch(id) ? id : executablePath;
    }

    /// <summary>toastActivatorClsid: a name-based GUID per app id, braced and upper case (the same as reframe's, so the
    /// managed shortcut's CLSID routes clicks to whichever of the two runs).</summary>
    public static string ToastActivatorClsid(string appId)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"reframe-toast:{appId}"))).ToLowerInvariant();
        var variant = ((Convert.ToInt32(h[16].ToString(), 16) & 0x3) | 0x8).ToString("x");
        var guid = $"{h[..8]}-{h[8..12]}-5{h[13..16]}-{variant}{h[17..20]}-{h[20..32]}";
        return "{" + guid.ToUpperInvariant() + "}";
    }

    const string SessionArg = "reframe-session=";

    static string Xml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>sessionToastXml: the launch argument names the session, so a click from Action Center still finds it.</summary>
    public static string SessionToastXml(string sessionId, string title, string body) =>
        $"<toast launch=\"{Xml(SessionArg + Uri.EscapeDataString(sessionId))}\" activationType=\"foreground\">" +
        $"<visual><binding template=\"ToastGeneric\"><text>{Xml(title)}</text><text>{Xml(body)}</text>" +
        "</binding></visual></toast>";

    static readonly Regex ActivationPattern = new(@"(?:^|[\s&;])reframe-session=([^\s&;]+)");

    /// <summary>sessionFromActivation: the session a toast activation names, or null.</summary>
    public static string? SessionFromActivation(string? arguments)
    {
        var match = ActivationPattern.Match(arguments ?? "");
        if (!match.Success) return null;
        var raw = match.Groups[1].Value;
        // decodeURIComponent refuses a broken escape or bytes that are not UTF-8: so does this.
        if (Regex.IsMatch(raw, "%(?![0-9A-Fa-f]{2})")) return null;
        var id = Uri.UnescapeDataString(raw);
        return id.Length == 0 || id.Contains('�') ? null : id;
    }

    /// <summary>notify.done / notify.waiting and their bodies.</summary>
    public static (string Title, string Body) ToastText(string kind, string label) => kind == "done"
        ? (Strings.T("notify.done", ("label", label)), Strings.T("notify.doneBody"))
        : (Strings.T("notify.waiting", ("label", label)), Strings.T("notify.waitingBody"));

    /// <summary>waitingText: the badge's description and the tray's tooltip with sessions needing the user.</summary>
    public static string WaitingText(int count) =>
        count == 1 ? Strings.T("title.waitingOne") : Strings.T("title.waiting", ("n", count));

    /// <summary>What notify() does for a host's <c>notify</c> (done or waiting).</summary>
    public sealed record Decision(bool Flash, bool Toast, string? Skipped);

    /// <summary>
    /// notify(): nothing for a session in no window or a hidden thread; a waiting one where nobody looks flashes its
    /// window's taskbar button; a toast unless notifications are off or the user looks at the window with the tile not
    /// collapsed into the dock.
    /// </summary>
    public static Decision Decide(AppState state, string sessionId, string kind, bool attentive, bool disabled)
    {
        var win = state.Windows.FirstOrDefault(w => LayoutActions.WindowHasSession(w, sessionId));
        if (win is null || !state.Sessions.TryGetValue(sessionId, out var record)
            || HiddenThreads.HiddenIds(state.HiddenThreads).Contains(record.ClaudeSessionId))
            return new(false, false, "not shown");
        var seen = attentive && !LayoutActions.IsCollapsed(win, sessionId);
        var flash = kind == "waiting" && !seen && !disabled;
        if (disabled) return new(flash, false, "notifications off");
        if (seen) return new(flash, false, "window in focus, tile visible");
        return new(flash, true, null);
    }

    /// <summary>updateAttention: how many sessions in each window are waiting (its taskbar badge).</summary>
    public static Dictionary<string, int> AttentionCounts(AppState state, Func<string, string> statusOf) =>
        state.Windows.ToDictionary(w => w.Id, w => LayoutActions.WindowSessions(w).Count(id => statusOf(id) == "waiting"));
}
