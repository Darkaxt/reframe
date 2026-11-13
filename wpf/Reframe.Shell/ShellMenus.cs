using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Builds a WPF ContextMenu (styled as Windows 11's dark native menu, Theme/Controls.xaml) from the specs of
/// <see cref="ContextMenus"/>. An item whose action the shell does not do yet (<paramref name="supported"/> false) is
/// shown disabled, in its place.</summary>
static class ShellMenus
{
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    public static ContextMenu Build(IEnumerable<MenuSpec> items, Func<string, bool> supported, Action<string> run)
    {
        var menu = new ContextMenu { Style = S("ShellContextMenu") };
        AutomationProperties.SetName(menu, "Context menu");
        foreach (var item in items) menu.Items.Add(Item(item, supported, run));
        return menu;
    }

    static object Item(MenuSpec spec, Func<string, bool> supported, Action<string> run)
    {
        if (spec.Type == "separator") return new Separator { Style = S("MenuSeparator") };
        var item = new MenuItem
        {
            Style = S("ShellMenuItem"), Header = spec.Label, Tag = spec.Type,
            IsChecked = spec.Checked == true, InputGestureText = spec.Accelerator ?? "",
        };
        AutomationProperties.SetName(item, spec.Label);
        if (spec.Submenu is not null)
        {
            foreach (var child in spec.Submenu) item.Items.Add(Item(child, supported, run));
            item.IsEnabled = spec.Enabled;
            return item;
        }
        item.IsEnabled = spec.Enabled && spec.Action is { } action && supported(action);
        if (spec.Action is { } a) item.Click += (_, _) => run(a);
        return item;
    }
}

/// <summary>Measures its child without a size limit, so a child turned by a LayoutTransform (the dock's vertical
/// labels) gets its full length instead of WPF's fit into the narrow slot (which cut "android-push" to "an…").</summary>
sealed class Unbounded : System.Windows.Controls.Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Child.DesiredSize;
    }
}
