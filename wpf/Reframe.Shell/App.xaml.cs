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
        ProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe Shell Test");
        string? hostDir = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--host-dir" && i + 1 < e.Args.Length) hostDir = Path.GetFullPath(e.Args[++i]);
            else
            if (e.Args[i] == "--profile" && i + 1 < e.Args.Length) ProfileDir = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--take-over") TakeOver = true;
            else if (e.Args[i] == Autostart.Argument) StartedBySystem = true;
            else if (e.Args[i] == "--diag-capture" && i + 1 < e.Args.Length) CaptureDir = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--diag-fixture" && i + 1 < e.Args.Length) FixtureFile = Path.GetFullPath(e.Args[++i]);
        }
        base.OnStartup(e);
        if (CaptureDir is not { } dir)
        {
            // One shell per profile: a second launch shows the first one's windows (revealWindows) and exits.
            var shell = new ShellController();
            instance = SingleInstance.TryAcquire(ProfileDir, () => Dispatcher.InvokeAsync(shell.Reveal));
            if (instance is null)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SingleInstance.ActivateFirstAsync(ProfileDir, TimeSpan.FromSeconds(5)).ContinueWith(_ => Dispatcher.InvokeAsync(Shutdown));
                return;
            }
            if (HostLauncher.FindSource(hostDir, AppContext.BaseDirectory) is { } source)
                Starter = new HostLauncher(source, $"shell-{typeof(App).Assembly.GetName().Version}").StartAsync;
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

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
