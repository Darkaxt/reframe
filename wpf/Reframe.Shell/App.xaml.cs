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
    /// <summary>Started by the system (<c>--autostart</c>, the login entry Settings writes): the windows stay in the tray,
    /// as reframe's startedBySystem().</summary>
    public static bool StartedBySystem { get; private set; }

    SingleInstance? instance;

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
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--host-dir" && i + 1 < e.Args.Length) hostDir = Path.GetFullPath(e.Args[++i]);
            else
            if (e.Args[i] == "--profile" && i + 1 < e.Args.Length) profile = e.Args[++i];
            else if (e.Args[i] == "--quit") quit = true;
            else if (e.Args[i] == "--take-over") TakeOver = true;
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
            if (HostLauncher.FindSource(hostDir, AppContext.BaseDirectory) is { } source)
                Starter = new HostLauncher(source, $"shell-{typeof(App).Assembly.GetName().Version}").StartAsync;
            KeepEntryPoints();
            // Closing the last window leaves the shell in the tray; only the tray's Quit ends it.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () => await shell.StartAsync(StartedBySystem));
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
        instance?.Dispose();
        base.OnExit(e);
    }
}
