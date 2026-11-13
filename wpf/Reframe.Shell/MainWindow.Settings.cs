using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Settings (gear, Ctrl+,), About (info) and the subscription quota at the foot of the session list. Preferences are
/// applied as app.ts's set-preferences (Settings.Apply) and saved through the state store; <see cref="PreferencesApplied"/>
/// tells the rest of the window (the terminals' text size) about a change. The quota comes from the host's
/// <c>quota.codex</c> (LLM Coordinator's usage snapshot, its <c>providers</c> for Claude too): the shell reads no
/// credentials and asks no usage API. The Claude status line's quota is not available to the shell (Claude Code tells it
/// reframe's status line command, not the shell).
/// </summary>
public partial class MainWindow
{
    /// <summary>LLM Coordinator's usage is read every five minutes (app.ts watchCoordinatorQuota).</summary>
    static readonly TimeSpan QuotaPoll = TimeSpan.FromMinutes(5);

    readonly QuotaBoard quota = new();
    ModalHost? modal;
    SettingsDialog? settingsDialog;
    Autostart? autostart;
    bool? gpuAtStart;
    DispatcherTimer? quotaTimer;
    DateTime quotaReadAt = DateTime.MinValue;

    /// <summary>Raised after preferences changed and were saved (S6 applies the text size to the terminals).</summary>
    public event Action<Preferences>? PreferencesApplied;

    void SettingsKeys(KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenSettings();
        }
    }

    bool Capturing => App.CaptureDir is not null;

    SettingsContext SettingsContextOf()
    {
        gpuAtStart ??= state.Preferences.GpuAcceleration;
        // The shell's own login entry, named after its profile, unless managed (S14): then reframe's own (the app id's);
        // none in a capture.
        if (!Capturing && Environment.ProcessPath is { } exe) autostart ??= new Autostart(new RegistryRunKeys(), App.ProfileDir, exe, App.ManagedAppId);
        return new SettingsContext
        {
            State = () => state,
            Set = SetPreferences,
            Restore = RestoreHidden,
            Autostart = Capturing ? null : autostart,
            GpuAccelerated = gpuAtStart.Value,
            FindClaude = Capturing ? null : chosen => Settings.FindClaude(chosen, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT")),
            Capture = Capturing,
        };
    }

    /// <summary>reframe:set-preferences: applied as app.ts checks it, saved when something changed.</summary>
    bool SetPreferences(PreferencesPatch patch)
    {
        if (!Writable || !Settings.Apply(state.Preferences, patch)) return false;
        Saved();
        // The terminals take the new text size at once (S6's path for a saved size).
        if (patch.FontSize is not null) FontSizeLoaded();
        PreferencesApplied?.Invoke(state.Preferences);
        return true;
    }

    /// <summary>reframe:restore-hidden-threads: the threads (all of them for null) listed again.</summary>
    void RestoreHidden(IReadOnlyCollection<string>? ids)
    {
        if (!Writable || !ShellActions.Restore(state, ids)) return;
        Saved();
        settingsDialog?.Refresh();
    }

    void OpenSettings()
    {
        if (modal is not null) return;
        var dialog = new SettingsDialog(SettingsContextOf());
        settingsDialog = dialog;
        dialog.CloseRequested += CloseModal;
        ShowModal(dialog.Frame);
    }

    void OpenAbout()
    {
        if (modal is not null) return;
        var dialog = new AboutDialog();
        dialog.CloseRequested += CloseModal;
        ShowModal(dialog.Frame);
        if (!Capturing) Dispatcher.BeginInvoke(() => Keyboard.Focus(dialog.CloseButton), DispatcherPriority.Input);
    }

    void ShowModal(FrameworkElement frame)
    {
        modal = ModalHost.Show(frame, this, Capturing ? root : null);
        modal.Closed += () =>
        {
            modal = null;
            settingsDialog = null;
        };
    }

    void CloseModal() => modal?.Close();

    // ---------- quota ----------

    /// <summary>Reads the Coordinator's quota now and every five minutes (while it is installed), and shows the
    /// "resets in" times anew every minute.</summary>
    void StartQuota()
    {
        if (services?.Offers("quota") != true) return;
        quotaTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        quotaTimer.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow - quotaReadAt >= QuotaPoll) await ReadQuotaAsync();
            else ShowQuota();
        };
        quotaTimer.Start();
        _ = ReadQuotaAsync();
    }

    async Task ReadQuotaAsync()
    {
        if (services is null) return;
        quotaReadAt = DateTime.UtcNow;
        try
        {
            var (coordinator, usage) = await services.QuotaCodexAsync(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (usage is null) System.Diagnostics.Trace.WriteLine($"quota: no usage read (Coordinator installed: {coordinator})");
            if (!coordinator)
            {
                // No Coordinator: nothing to watch (as app.ts, which watches only an installed one).
                quotaTimer?.Stop();
                return;
            }
            quota.ApplyCoordinator(usage, DateTimeOffset.Now.ToUnixTimeMilliseconds());
        }
        catch (HostServiceException e)
        {
            System.Diagnostics.Trace.WriteLine($"quota: {e.Code} {e.Message}");
            SetStatus(status + $" (quota: {e.Message})");
        }
        ShowQuota();
    }

    void ShowQuota() => sidebar.Quota.Show(quota.Shown, Now);

    // ---------- capture ----------

    /// <summary>The "empty" fixture's states after main-empty: the quota (set as Electron's test hook sets it), About,
    /// and Settings one screenful per page.</summary>
    async Task CaptureDialogsAsync(HwndSource popup, string dir)
    {
        foreach (var q in fixture?.Quota ?? []) quota.Set(q);
        ShowQuota();
        await DiagCapture.SettleAsync(Dispatcher, 300);
        if (sidebar.Quota.IsVisible)
        {
            DiagCapture.Save(popup, root, dir, "quota");
            DiagCapture.Save(popup, sidebar.Quota, dir, "quota-panel");
        }

        OpenAbout();
        await DiagCapture.SettleAsync(Dispatcher, 500);
        DiagCapture.Save(popup, root, dir, "about", modal?.Overlay);
        CloseModal();

        OpenSettings();
        await DiagCapture.SettleAsync(Dispatcher, 500);
        if (settingsDialog is { } dialog)
        {
            var scroll = dialog.Scroll;
            var pages = Math.Max(1, (int)Math.Ceiling(scroll.ExtentHeight / Math.Max(1, scroll.ViewportHeight)));
            for (var i = 0; i < pages; i++)
            {
                scroll.ScrollToVerticalOffset(Math.Min(i * scroll.ViewportHeight, scroll.ScrollableHeight));
                await DiagCapture.SettleAsync(Dispatcher, 300);
                DiagCapture.Save(popup, root, dir, $"settings-{i + 1}", modal?.Overlay);
            }
        }
        CloseModal();
    }

    /// <summary>The "hidden" fixture: Settings opened (Ctrl+,) and scrolled to its Hidden threads.</summary>
    async Task CaptureHiddenAsync(HwndSource popup, string dir)
    {
        OpenSettings();
        await DiagCapture.SettleAsync(Dispatcher, 500);
        if (settingsDialog is not { } dialog) return;
        dialog.Scroll.ScrollToEnd();
        await DiagCapture.SettleAsync(Dispatcher, 300);
        DiagCapture.Save(popup, root, dir, "hidden-threads", modal?.Overlay);
        DiagCapture.Save(popup, dialog.HiddenSection, dir, "hidden-threads-component", modal?.Overlay);
        CloseModal();
    }
}
