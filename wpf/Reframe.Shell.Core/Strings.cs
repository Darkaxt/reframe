using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>
/// The texts of Settings, About and the quota panel under src/shared/i18n.ts's keys, in English (the shell speaks
/// English only for now; the keys stay so the other languages can follow). <see cref="T"/> fills <c>{name}</c> as
/// i18n.ts's t() does. A few texts SettingsDialog.tsx writes inline (no key there) are under <c>inline.*</c>.
/// </summary>
public static class Strings
{
    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["keys.ctrl"] = "Ctrl",
        ["dialog.modelPlaceholder"] = "Default",
        ["dialog.default"] = "Default",
        ["quota.label"] = "Subscription quota",
        ["quota.claude"] = "Claude quota",
        ["quota.codex"] = "Codex quota",
        ["quota.resets"] = "resets in {when}",
        ["quota.resetsAt"] = "Resets {when}",
        ["quota.used"] = "{percent}% used",
        ["quota.observed"] = "As of {when}",
        ["about.title"] = "About Reframe",
        ["about.version"] = "Version {version}",
        ["about.tagline"] = "Your Claude Code sessions side by side, and all of them back after a restart.",
        ["about.github"] = "Reframe on GitHub",
        ["about.star"] = "Give Reframe a star on GitHub",
        ["about.madeWith"] = "Made with ❤️ in Austria by reframe Team aka",
        ["about.license"] = "License (MIT)",
        ["about.close"] = "Close",
        ["settings.title"] = "Settings",
        ["settings.language"] = "Language",
        ["settings.languageSystem"] = "System ({language})",
        ["settings.textSize"] = "Text size",
        ["settings.smaller"] = "Smaller",
        ["settings.bigger"] = "Larger",
        ["settings.normal"] = "Normal",
        ["settings.textSizeHint"] = "Also {bigger} and {smaller} ({normal}: normal), or the mouse wheel with {ctrl} held over a tile.",
        ["settings.gpu"] = "Use GPU acceleration",
        ["settings.gpuHint"] = "Off: Reframe and its terminals draw without the graphics card. Takes effect after restarting Reframe.",
        ["settings.gpuRestart"] = "Takes effect after restarting Reframe.",
        ["settings.ptyHost"] = "Terminal host",
        ["settings.ptyHostNode"] = "Node (default)",
        ["settings.ptyHostRust"] = "Rust (experimental)",
        ["settings.ptyHostHint"] = "Applies to new sessions after restarting Reframe; running sessions stay on their host. Experimental. REFRAME_PTYHOST overrides it.",
        ["settings.snap"] = "Snap separators to a grid",
        ["settings.snapHint"] = "While dragging, a separator lines up with the others. Hold Alt to place it freely.",
        ["settings.autostart"] = "Start with the system",
        ["settings.autostartHint"] = "When you log in, Reframe starts in the tray with its windows hidden and resumes your sessions in the background.",
        ["settings.lookInWsl"] = "Look for Claude Code in WSL",
        ["settings.lookInWslHint"] = "The session list also shows sessions from running WSL distributions. Checking them regularly keeps WSL from stopping when idle.",
        ["settings.updates"] = "Check for updates",
        ["settings.updatesHint"] = "Half a minute after starting and then every six hours, Reframe asks GitHub whether there is a newer version. Nothing else is sent.",
        ["settings.closeWindow"] = "Closing a window with sessions",
        ["settings.closeWindowAsk"] = "Ask",
        ["settings.closeWindowEnd"] = "Ends them",
        ["settings.closeWindowKeep"] = "Keeps them running in the tray",
        ["settings.closeWindowHint"] = "Ended sessions stay in the session list, where you can open them again. A window without sessions always just closes.",
        ["settings.claude"] = "Claude Code",
        ["settings.claudeChoose"] = "Choose…",
        ["settings.claudeUsePath"] = "Use the one on the PATH",
        ["settings.claudeFound"] = "From the PATH:",
        ["settings.claudeNotFound"] = "Not found on the PATH",
        ["settings.claudeChosen"] = "Chosen:",
        ["settings.claudeMissing"] = "Chosen, but not there any more:",
        ["settings.claudeHint"] = "Sessions in WSL folders run the Claude Code of their distribution.",
        ["settings.programs"] = "Programs",
        ["settings.allFiles"] = "All files",
        ["settings.defaultModel"] = "Model for new sessions",
        ["settings.defaultPermissionMode"] = "Permission mode for new sessions",
        ["settings.launchHint"] = "Filled in when you start a session, where you can change them.",
        ["settings.close"] = "Close",
        ["settings.hidden"] = "Hidden threads",
        ["settings.hiddenHint"] = "Threads hidden from Reframe are not listed, offered or notified. Their sessions are left alone.",
        ["settings.hiddenNone"] = "No hidden threads.",
        ["settings.hiddenRestore"] = "Restore",
        ["settings.hiddenRestoreAll"] = "Restore all",
        ["settings.hiddenAt"] = "Hidden {date}",
        ["dialog.cancel"] = "Cancel",
        ["tray.tooltip"] = "Reframe",
        ["title.waitingOne"] = "Reframe – a session needs you",
        ["title.waiting"] = "Reframe – {n} sessions need you",
        ["notify.done"] = "{label} is done",
        ["notify.doneBody"] = "Claude finished responding.",
        ["notify.waiting"] = "{label} needs you",
        ["notify.waitingBody"] = "Claude is waiting for your input.",
        ["tray.window"] = "Window {n}",
        ["tray.newSession"] = "New session",
        ["tray.newWindow"] = "New window",
        ["tray.terminateAll"] = "Terminate all sessions",
        ["tray.quit"] = "Quit",
        ["tray.about"] = "About Reframe",
        ["fullscreen.hint"] = "Press F11 to exit full screen",
        ["fullscreen.exit"] = "Exit full screen",
        ["closeWindow.title"] = "Close window",
        ["closeWindow.messageOne"] = "End the session in this window?",
        ["closeWindow.message"] = "End the {count} sessions in this window?",
        ["closeWindow.detail"] = "Ended, they stay in the session list and the window closes for good. Kept, they go on running and the window waits in the tray.",
        ["closeWindow.end"] = "End sessions",
        ["closeWindow.keep"] = "Keep in the tray",
        ["closeWindow.remember"] = "Don't ask again (Settings can change it)",
        ["inline.archives"] = "Chat archives",
        ["inline.archivesChoose"] = "Choose folder…",
        ["inline.archivesDefault"] = "Archives folder in this Reframe profile",
        ["inline.archivesHint"] = "Choose a non-C drive. Verified ZIPs retain exact chat recovery files.",
        ["inline.defaultEffort"] = "Default effort",
        ["inline.defaultEffortCli"] = "Default (CLI settings)",
    };

    /// <summary>t(key, values): the English text with <c>{name}</c> filled in (the key itself when unknown).</summary>
    public static string T(string key, params (string Name, object Value)[] values)
    {
        var text = En.TryGetValue(key, out var found) ? found : key;
        return values.Length == 0 ? text : Regex.Replace(text, @"\{(\w+)\}", m =>
            values.FirstOrDefault(v => v.Name == m.Groups[1].Value) is { Name: not null } hit ? Convert.ToString(hit.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : m.Value);
    }
}
