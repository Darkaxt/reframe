using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>A tile header's meters: CPU, GPU and RAM of the thread (its process and everything it started), each a
/// small icon and its value, as the Taskbar Thermals widget shows them. Hidden until the host tells what the session
/// uses (a session of a host without <c>monitor</c>, or one that ended, shows none).</summary>
sealed class UsageMeters
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    readonly (VectorIcon Icon, TextBlock Value) cpu, gpu, ram;

    public StackPanel Element { get; }

    public UsageMeters()
    {
        Element = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        cpu = Meter("cpu");
        gpu = Meter("gpu");
        ram = Meter("ram");
        ToolTipService.SetShowDuration(Element, 30000);
    }

    (VectorIcon, TextBlock) Meter(string kind)
    {
        var icon = new VectorIcon { Kind = kind, Size = 13, Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Style = S("Text12"), Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Element.Children.Count > 0 ? 8 : 0, 0, 0, 0) };
        pair.Children.Add(icon);
        pair.Children.Add(value);
        Element.Children.Add(pair);
        return (icon, value);
    }

    /// <summary>The session's use now, or null to hide the meters.</summary>
    public void Show(SessionUsage? usage)
    {
        if (usage is null)
        {
            Element.Visibility = Visibility.Collapsed;
            return;
        }
        Set(cpu, SessionUsage.Percent(usage.Cpu), Level(usage.Cpu));
        Set(gpu, SessionUsage.Percent(usage.Gpu), Level(usage.Gpu));
        // Memory is shown plainly: what is a lot depends on the machine.
        Set(ram, SessionUsage.Bytes(usage.Memory), "mutedBrush");
        Element.ToolTip = usage.Describe();
        AutomationProperties.SetName(Element, $"CPU {SessionUsage.Percent(usage.Cpu)}, GPU {SessionUsage.Percent(usage.Gpu)}, RAM {SessionUsage.Bytes(usage.Memory)}");
        Element.Visibility = Visibility.Visible;
    }

    /// <summary>Muted while modest, the warning colour from half the machine, danger from 85%.</summary>
    static string Level(double percent) => percent >= 85 ? "dangerBrush" : percent >= 50 ? "warnBrush" : "mutedBrush";

    static void Set((VectorIcon Icon, TextBlock Value) meter, string text, string brush)
    {
        meter.Value.Text = text;
        meter.Value.Foreground = meter.Icon.Foreground = B(brush);
    }
}
