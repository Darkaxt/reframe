using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The window's part of session control: the New Session dialog, the questions a start asks, and a session's
/// tile built anew when it starts, ends or fails (ShellController.Sessions.cs does the starting).</summary>
public partial class MainWindow
{
    NewSessionDialog? newSessionDialog;

    /// <summary>The toolbar's and empty state's New session, Ctrl+Shift+T, the tray's item and a project's "+".</summary>
    public void OpenNewSession() => OpenNewSession(null);

    internal void OpenNewSession(NewSessionPreset? preset)
    {
        if (modal is not null || !Writable) return;
        var (roots, recent) = shell.DialogDefaults();
        var dialog = newSessionDialog = new NewSessionDialog(state.Preferences, roots, recent, preset);
        dialog.CloseRequested += CloseModal;
        dialog.Create = request => shell.CreateSessionAsync(WindowId, request, AskToCreateFolder);
        ShowModal(dialog.Frame);
        modal!.Closed += () => newSessionDialog = null;
        if (!Capturing) Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(dialog.Folder);
            dialog.Folder.CaretIndex = dialog.Folder.Text.Length;
        }, DispatcherPriority.Input);
    }

    /// <summary>offerToCreateFolder: whether to create the missing folder.</summary>
    bool AskToCreateFolder(string folder) => CloseWindowDialog.Confirm(this, null,
        Strings.T("dialog.createFolder.message", ("folder", folder)), null, Strings.T("dialog.createFolder.create"));

    /// <summary>confirmTakeover: whether to end the session in the other terminal and continue it here.</summary>
    public bool ConfirmTakeover(string label)
    {
        BringForward();
        return CloseWindowDialog.Confirm(this, Strings.T("takeover.title"), Strings.T("takeover.message", ("label", label)),
            Strings.T("takeover.detail"), Strings.T("takeover.confirm"));
    }

    /// <summary>The session started, ended or failed: its tile shows its terminal or the card anew.</summary>
    public void SessionChanged(string sessionId)
    {
        if (discarded || !tiles.ContainsKey(sessionId)) return;
        DisposeTile(sessionId);
        Relayout();
        if (selected == sessionId) SelectTile(sessionId);
    }

    /// <summary>The "hidden" fixture's New Session states (e2e/visual-reference.spec.ts: Anthropic, then ChatGPT, then
    /// Start with a folder that does not exist).</summary>
    async Task CaptureNewSessionAsync(HwndSource popup, string dir)
    {
        OpenNewSession();
        if (newSessionDialog is not { } dialog) return;
        dialog.ProviderBox.SelectedIndex = StateValues.Providers.ToList().IndexOf("anthropic");
        await DiagCapture.SettleAsync(Dispatcher, 500);
        DiagCapture.Save(popup, root, dir, "new-session-claude", modal?.Overlay);
        dialog.ProviderBox.SelectedIndex = StateValues.Providers.ToList().IndexOf("chatgpt");
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "new-session-chatgpt", modal?.Overlay);
        dialog.Folder.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "no-such-folder");
        await dialog.SubmitAsync();
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "new-session-error", modal?.Overlay);
        CloseModal();
    }

    /// <summary>The "sessions" fixture's error tile: old-checkout (its folder gone) opened from the list.</summary>
    async Task CaptureErrorTileAsync(HwndSource popup, string dir)
    {
        if (!state.Sessions.ContainsKey("vs-old-checkout")) return;
        Diag.SetFocused(sidebar.Search, false);
        sidebar.Search.Text = "";
        await RefreshAsync();
        ShowSession("vs-old-checkout");
        await DiagCapture.SettleAsync(Dispatcher, 1500);
        DiagCapture.Save(popup, root, dir, "error-tile");
        if (tiles.GetValueOrDefault("vs-old-checkout") is { } view) DiagCapture.Save(popup, view.Ring, dir, "error-tile-component");
    }
}
