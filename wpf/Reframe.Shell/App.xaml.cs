using System.IO;
using System.Windows;

namespace Reframe.Shell;

public partial class App : Application
{
    /// <summary>reframe's user-data folder to attach to; by default a test profile of the shell's own.</summary>
    public static string ProfileDir { get; private set; } = "";
    /// <summary>Attach even while another client is attached (it is replaced: the protocol has one client per profile).</summary>
    public static bool TakeOver { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe Shell Test");
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--profile" && i + 1 < e.Args.Length) ProfileDir = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--take-over") TakeOver = true;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
