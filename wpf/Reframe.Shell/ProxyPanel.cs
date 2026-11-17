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

    readonly Button restart = Glyph("E72C", "Restart the proxy");
    readonly Button stopStart = Glyph("E71A", "Stop the proxy");
    bool up;

    /// <summary>"stop", "start" or "restart" asked by the row's buttons.</summary>
    public Action<string>? Act { get; set; }

    static Button Glyph(string code, string name)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolbarIcon"), Margin = new Thickness(0, 0, 2, 0), Visibility = Visibility.Collapsed,
            Content = new TextBlock { Text = ((char)Convert.ToInt32(code, 16)).ToString(), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    static void Set(Button button, string glyph, string name)
    {
        ((TextBlock)button.Content).Text = ((char)Convert.ToInt32(glyph, 16)).ToString();
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
    }

    public ProxyPanel()
    {
        BorderBrush = B("borderBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(12, 6, 6, 6);
        var row = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(restart);
        buttons.Children.Add(stopStart);
        row.Children.Add(buttons);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(dot);
        label.Children.Add(text);
        row.Children.Add(label);
        Child = row;
        restart.Click += (_, _) => Act?.Invoke("restart");
        stopStart.Click += (_, _) => Act?.Invoke(up ? "stop" : "start");
        Visibility = Visibility.Collapsed;
    }

    public void Show(ProxyState state, string? where, DateTimeOffset? checkedAt, bool controllable = false, string? busy = null)
    {
        Visibility = state == ProxyState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        up = state == ProxyState.Up;
        dot.Background = B(up ? "okBrush" : "dangerBrush");
        text.Text = busy ?? (up ? "Proxy online" : "Proxy offline");
        text.Foreground = up || busy is not null ? B("mutedBrush") : B("dangerBrush");
        var show = controllable ? Visibility.Visible : Visibility.Collapsed;
        restart.Visibility = up ? show : Visibility.Collapsed;
        stopStart.Visibility = show;
        restart.IsEnabled = stopStart.IsEnabled = busy is null;
        Set(restart, "E72C", "Restart the proxy");
        if (up) Set(stopStart, "E71A", "Stop the proxy");
        else Set(stopStart, "E768", "Start the proxy");
        ToolTip = $"{where}{(checkedAt is { } at ? $"\nChecked {at.LocalDateTime:HH:mm:ss}" : "")}";
        AutomationProperties.SetName(this, text.Text);
    }
}
