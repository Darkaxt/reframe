using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>One line above the quota at the foot of the session list: whether the local proxy of the ChatGPT threads answers.
/// Hidden while it is not watched or not yet checked.</summary>
sealed class ProxyPanel : Border
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    readonly TextBlock text = new() { Style = (Style)Application.Current.FindResource("Text11"), VerticalAlignment = VerticalAlignment.Center };
    readonly Border dot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    public ProxyPanel()
    {
        BorderBrush = B("borderBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(12, 6, 6, 6);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(text);
        Child = row;
        Visibility = Visibility.Collapsed;
    }

    public void Show(ProxyState state, string? where, DateTimeOffset? checkedAt)
    {
        Visibility = state == ProxyState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        var up = state == ProxyState.Up;
        dot.Background = B(up ? "okBrush" : "dangerBrush");
        text.Text = up ? "Proxy online" : "Proxy offline";
        text.Foreground = up ? B("mutedBrush") : B("dangerBrush");
        ToolTip = $"{where}{(checkedAt is { } at ? $"\nChecked {at.LocalDateTime:HH:mm:ss}" : "")}";
        AutomationProperties.SetName(this, text.Text);
    }
}
