using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Reframe.Shell;

/// <summary>Cheap diagnostics of the start: what the UI thread builds while UI Automation follows every new element.
/// Lines go to main.log for the first two minutes, then only the slow ones.</summary>
public static class StartupTrace
{
    static readonly DateTime started = Process.GetCurrentProcess().StartTime;

    /// <summary>Time since the process started.</summary>
    public static TimeSpan Uptime => DateTime.Now - started;

    /// <summary>Within the first two minutes.</summary>
    public static bool Early => Uptime < TimeSpan.FromMinutes(2);

    /// <summary>Logs while <see cref="Early"/>, or when the work took at least <paramref name="slowMs"/>.</summary>
    public static void Log(string line, long elapsedMs, long slowMs = 100)
    {
        if (Early || elapsedMs >= slowMs) Trace.WriteLine($"{line} at +{Uptime.TotalSeconds:0.0}s");
    }

    /// <summary>The visuals under (and including) an element.</summary>
    public static int CountVisuals(DependencyObject? root)
    {
        if (root is null) return 0;
        var count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            count++;
            if (node is not Visual and not System.Windows.Media.Media3D.Visual3D) continue;
            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return count;
    }
}
