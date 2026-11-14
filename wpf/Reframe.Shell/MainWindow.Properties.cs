using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>A thread's Properties… (list entry and tile menus): its ids, folder, provider and launch options, when it
/// was created and last active, its process and host, what it uses now and what its files take on disk.</summary>
public partial class MainWindow
{
    /// <summary>The last <c>monitor.sample</c>, for the properties' "Use now".</summary>
    IReadOnlyDictionary<string, SessionUsage> lastUsage = new Dictionary<string, SessionUsage>();

    static string When(double ms) => ms <= 0 ? "unknown"
        : DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    void ShowProperties(ChatRow? chat, string? sessionId)
    {
        var record = sessionId is null ? null : state.Sessions.GetValueOrDefault(sessionId);
        var claudeSessionId = record?.ClaudeSessionId is { Length: > 0 } own ? own
            : chat?.Ref.ClaudeSessionId ?? (chat is null ? null : SidebarChats.ParseChatKey(chat.Key)?.Id);
        var title = record is not null ? LabelOf(record.Id) : chat?.Label ?? claudeSessionId ?? "Thread";
        var rows = new List<(string, string)> { ("Name", title) };
        if (record is not null) rows.Add(("Reframe session ID", record.Id));
        if (!string.IsNullOrEmpty(claudeSessionId)) rows.Add(("Claude session ID", claudeSessionId));
        var cwd = record?.Cwd ?? chat?.Chat.Cwd;
        if (!string.IsNullOrEmpty(cwd)) rows.Add(("Folder", cwd));
        if (chat?.Ref.Distro is { Length: > 0 } distro) rows.Add(("WSL distribution", distro));
        var provider = record?.Launch.Provider ?? chat?.Provider;
        rows.Add(("Model provider", provider switch { "anthropic" => "Anthropic", "chatgpt" => "ChatGPT", null => "ChatGPT (default)", _ => provider }));
        if (record is not null)
        {
            var launch = record.Launch;
            if (!string.IsNullOrEmpty(launch.Model)) rows.Add(("Model", launch.Model + (string.IsNullOrEmpty(launch.Effort) ? "" : $" ({launch.Effort} effort)")));
            if (!string.IsNullOrEmpty(launch.PermissionMode)) rows.Add(("Permission mode", launch.PermissionMode));
            if (launch.Worktree == true) rows.Add(("Worktree", string.IsNullOrEmpty(launch.WorktreeName) ? "yes" : launch.WorktreeName));
            if (launch.AddDirs is { Count: > 0 } dirs) rows.Add(("Added folders", string.Join("\n", dirs)));
            rows.Add(("Created", When(record.CreatedAt)));
            rows.Add(("Last active", When(record.LastActiveAt)));
            rows.Add(("State", Runs(record.Id) ? $"running ({StatusOf(record.Id)})" : record.Lifecycle));
        }
        else if (chat is not null && chat.Chat.LastActiveAt > 0) rows.Add(("Last active", When(chat.Chat.LastActiveAt)));
        if (sessionId is not null && Runs(sessionId) && shell.HostSessions.GetValueOrDefault(sessionId) is { } hosted)
        {
            rows.Add(("Process ID", hosted.Pid.ToString(CultureInfo.InvariantCulture)));
            if (shell.ConnectionOf(sessionId)?.Welcome is { } welcome)
                rows.Add(("PTY host", $"{(welcome.Kind == "rust" ? "Rust" : "Node")} host {welcome.Version} (pid {welcome.Pid})"));
            if (lastUsage.GetValueOrDefault(sessionId) is { } usage)
                rows.Add(("Use now", usage.Describe(max: 8)));
        }
        else if (chat?.Ref is { Kind: ListItemKind.Running, Pid: > 0 } running) rows.Add(("Process ID", running.Pid.ToString(CultureInfo.InvariantCulture)));

        var home = (sessionId is null ? null : shell.HostSessions.GetValueOrDefault(sessionId)?.ClaudeHome) ?? ClaudeRegistry.Home();
        var disk = Task.Run(() =>
        {
            var groups = string.IsNullOrEmpty(claudeSessionId) || chat?.Ref.Distro is { Length: > 0 } ? [] : ThreadFiles.Measure(home, claudeSessionId);
            var lines = groups.Select(g => (g.Name, $"{ThreadFiles.Bytes(g.Bytes)}{(g.Files > 1 ? $" in {g.Files} files" : "")}\n{g.Path}")).ToList();
            lines.Add(groups.Count == 0 ? ("", "No files of this thread in " + home) : ("Total", ThreadFiles.Bytes(groups.Sum(g => g.Bytes))));
            return lines;
        });

        CloseModal();
        var dialog = new PropertiesDialog(title, rows, disk);
        dialog.CloseRequested += CloseModal;
        ShowModal(dialog.Frame);
        if (!Capturing) Dispatcher.BeginInvoke(() => Keyboard.Focus(dialog.CloseButton), DispatcherPriority.Input);
    }
}
