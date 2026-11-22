using System.IO;
using System.Windows;
using Reframe.Shell.Core;

namespace Reframe.Shell;

public partial class App : Application
{
    /// <summary>reframe's user-data folder to attach to; by default a test profile of the shell's own.</summary>
    public static string ProfileDir { get; private set; } = "";
    /// <summary>Attach as the primary client even while another client is attached (it is replaced: one primary per
    /// profile). Without it, the shell watches read-only beside that client when the host offers observers.</summary>
    public static bool TakeOver { get; private set; }
    /// <summary>Diagnostics (<c>--diag-capture &lt;dir&gt;</c>): renders the window's states at a fixed size, saves each
    /// as &lt;state id&gt;.png in the folder (PrintWindow, so the native terminal shows too) and exits. For the visual
    /// parity report (tools/visual); never used by a normal start.</summary>
    public static string? CaptureDir { get; private set; }
    /// <summary>With --diag-capture: a visual fixture (tools/visual/fixtures/*.json) to render instead of the profile's
    /// state, with the host's sessions of the same ids as its terminals.</summary>
    public static string? FixtureFile { get; private set; }
    /// <summary>Starts the profile's Rust host when none runs (from <c>--host-dir</c>, REFRAME_RUST_HOST_DIR or
    /// <c>reframe-host</c> beside the shell); null when there is none to start, and in diagnostics (which only attach).</summary>
    public static HostStarter? Starter { get; private set; }
    public static Func<int, bool>? IsCurrentHost { get; private set; }
    /// <summary>Started by the system (<c>--autostart</c>, the login entry Settings writes): the windows stay in the tray,
    /// as reframe's startedBySystem().</summary>
    public static bool StartedBySystem { get; private set; }

    SingleInstance? instance;

    /// <summary><c>--relaunch &lt;launcher.vbs&gt;</c> with <c>--after-pid</c> (the tray's Reload): once that shell has gone, run the
    /// managed launcher through wscript, as the shortcut does, and exit. Never a window.</summary>
    public const string RelaunchArgument = "--relaunch";

    /// <summary>Whether this run draws with the graphics card (the setting as it was at start).</summary>
    public static bool GpuAccelerated { get; private set; } = true;

    /// <summary>"Use GPU acceleration" off (the default): WPF and the terminals' AtlasEngine draw in software (WARP),
    /// as Electron's disableHardwareAcceleration did; NVIDIA driver resets were seen while reframe used the GPU.</summary>
    static void ApplyGpuPreference()
    {
        GpuAccelerated = GpuPreference.Saved(Path.Combine(ProfileDir, "state.json"));
        if (GpuAccelerated) return;
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Microsoft.Terminal.Wpf.TerminalControl.SetDefaultSoftwareRendering(true);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A console helper (ConsoleHelper.cs): one request in another console, no window, then gone.
        if (e.Args.Length == 2 && e.Args[0] == ConsoleHelperClient.Argument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(ConsoleHelperMain.Run(e.Args[1]));
            return;
        }
        string? hostDir = null, profile = null;
        var quit = false;
        var afterPid = 0;
        string? relaunch = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--host-dir" && i + 1 < e.Args.Length) hostDir = Path.GetFullPath(e.Args[++i]);
            else
            if (e.Args[i] == "--profile" && i + 1 < e.Args.Length) profile = e.Args[++i];
            else if (e.Args[i] == "--quit") quit = true;
            else if (e.Args[i] == "--take-over") TakeOver = true;
            else if (e.Args[i] == RelaunchArgument && i + 1 < e.Args.Length) relaunch = e.Args[++i];
            else if (e.Args[i] == "--after-pid" && i + 1 < e.Args.Length) afterPid = int.TryParse(e.Args[++i], out var pid) ? pid : 0;
            else if (e.Args[i] == Autostart.Argument) StartedBySystem = true;
            else if (e.Args[i] == "--diag-capture" && i + 1 < e.Args.Length) CaptureDir = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--diag-fixture" && i + 1 < e.Args.Length) FixtureFile = Path.GetFullPath(e.Args[++i]);
        }
        // The profile and routes as reframe.exe took them: --profile, the managed launcher's REFRAME_USER_DATA, or a
        // direct start's reframe-config.json (LaunchConfig), whose routes go into this process's environment (and so
        // into the host it starts). Neither given: the shell's own test profile.
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry v in Environment.GetEnvironmentVariables()) env[(string)v.Key] = v.Value as string;
        var before = new Dictionary<string, string?>(env, StringComparer.OrdinalIgnoreCase);
        var (resolved, applied) = LaunchConfig.Resolve(profile, env, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        foreach (var (key, value) in env)
            if (!before.TryGetValue(key, out var old) || old != value) Environment.SetEnvironmentVariable(key, string.IsNullOrEmpty(value) ? null : value);
        foreach (var problem in applied?.Problems ?? []) System.Diagnostics.Trace.WriteLine($"{LaunchConfig.FileName}: {problem}");
        ProfileDir = resolved ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe Shell Test");
        base.OnStartup(e);
        // Started by the shell it replaces (an update): wait for it to let go of the profile's single-instance lock.
        if (afterPid > 0)
            try
            {
                System.Diagnostics.Process.GetProcessById(afterPid).WaitForExit(relaunch is null ? 20000 : 60000);
            }
            catch (ArgumentException)
            {
                // already gone
            }
        if (relaunch is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = 1;
            if (Path.IsPathFullyQualified(relaunch) && relaunch.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase) && File.Exists(relaunch))
                try
                {
                    var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wscript.exe"))
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(relaunch)) ?? Path.GetDirectoryName(relaunch)!,
                    };
                    foreach (var a in new[] { "//B", "//Nologo", relaunch }) start.ArgumentList.Add(a);
                    using var _ = System.Diagnostics.Process.Start(start);
                    code = 0;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            Shutdown(code);
            return;
        }
        if (quit)
        {
            // reframe.exe --quit (maintenance/windows/stop-managed.ps1): the profile's running shell quits as its tray's
            // Quit does (its sessions keep running in the host). Never a window; nothing running is success.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!SingleInstance.Running(ProfileDir)) { Shutdown(0); return; }
            SingleInstance.RequestQuitAsync(ProfileDir, TimeSpan.FromSeconds(10))
                .ContinueWith(t => Dispatcher.InvokeAsync(() => Shutdown(t.Result ? 0 : 1)));
            return;
        }
        if (CaptureDir is not { } dir)
        {
            // One shell per profile: a second launch shows the first one's windows (revealWindows) and exits.
            var shell = new ShellController();
            instance = SingleInstance.TryAcquire(ProfileDir, () => Dispatcher.InvokeAsync(shell.Reveal),
                () => Dispatcher.InvokeAsync(() => shell.QuitAsync()));
            if (instance is null)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SingleInstance.ActivateFirstAsync(ProfileDir, TimeSpan.FromSeconds(5)).ContinueWith(_ => Dispatcher.InvokeAsync(Shutdown));
                return;
            }
            FileLog.Start(ProfileDir, AboutDialog.Version);
            UiWatchdog.Start(Dispatcher);
            ApplyGpuPreference();
            System.Diagnostics.Trace.WriteLine($"gpu: {(GpuAccelerated ? "hardware" : "software")} rendering");
            if (HostLauncher.FindSource(hostDir, AppContext.BaseDirectory) is { } source)
            {
                var launcher = new HostLauncher(source, $"shell-{typeof(App).Assembly.GetName().Version}");
                Starter = launcher.StartAsync;
                IsCurrentHost = launcher.RunsCurrentBuild;
            }
            KeepEntryPoints();
            // Closing the last window leaves the shell in the tray; only the tray's Quit ends it.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await shell.StartAsync(StartedBySystem);
                }
                catch (Exception error)
                {
                    // A start that fails must not leave a process with no window holding the profile (every later
                    // launch hands over to it and nothing opens): say why, then end.
                    System.Diagnostics.Trace.WriteLine($"reframe could not start: {error}");
                    MessageBox.Show($"Reframe could not start: {error.Message}\n\nThe details are in main.log.", "Reframe",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                }
            });
            return;
        }
        var window = new MainWindow(new ShellController(), null);
        // Diagnostics: never shown as a window; the captures run in a popup, then the shell exits.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await window.CaptureAsync(dir, FixtureFile);
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "capture-error.txt"), error + Environment.NewLine);
            }
            finally
            {
                Shutdown();
            }
        });
    }

    /// <summary>The managed launcher's fixed app id (REFRAME_APP_ID), or null.</summary>
    public static string? ManagedAppId =>
        Environment.GetEnvironmentVariable("REFRAME_APP_ID") is { Length: > 0 } id && Notifications.AppUserModelId(id, "") == id.Trim() ? id.Trim() : null;

    /// <summary>A managed start keeps reframe's entry points pointing at the running executable, as reframe.exe did: the
    /// Start menu shortcut and Reframe.lnk with the app id and toast CLSID (ManagedShortcuts), and an existing login
    /// entry of the app id (Autostart.Repoint). Best effort; never in a test or diagnostics start.</summary>
    static void KeepEntryPoints()
    {
        if (ManagedAppId is not { } appId || Environment.ProcessPath is not { } exe) return;
        try
        {
            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry v in Environment.GetEnvironmentVariables()) env[(string)v.Key] = v.Value as string;
            foreach (var line in ManagedShortcuts.Apply(ManagedShortcuts.Wanted(env, appId, exe))) System.Diagnostics.Trace.WriteLine($"shortcut {line}");
            if (new Autostart(new RegistryRunKeys(), ProfileDir, exe, appId).Repoint()) System.Diagnostics.Trace.WriteLine("autostart entry repointed");
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"entry points not kept: {error.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (instance is not null) System.Diagnostics.Trace.WriteLine($"reframe stopped (exit {e.ApplicationExitCode})");
        instance?.Dispose();
        System.Diagnostics.Trace.Flush();
        base.OnExit(e);
    }
}
