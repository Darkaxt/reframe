using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The top bar's warnings button (right, before the gear; shown once there was a warning, with the count of
/// those open) and its dropdown: each open warning with its thread, what it is about and when, and Notify thread,
/// Kill process and Dismiss; then the earlier ones (resolved, killed or dismissed) and Clear.</summary>
public partial class MainWindow
{
    Button? warningsButton;
    TextBlock? warningsCount;
    VectorIcon? warningsIcon;
    Popup? warningsPopup;
    readonly StackPanel warningsList = new();
    DateTime warningsClosedAt;

    Button WarningsButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        warningsIcon = new VectorIcon { Kind = "warning", Size = 14, Foreground = B("warnBrush"), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(warningsIcon);
        warningsCount = new TextBlock { Style = S("Text12"), Foreground = B("warnBrush"), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(warningsCount);
        var button = warningsButton = new Button
        {
            Style = S("ToolbarIcon"), Content = content, Width = double.NaN, Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(button, "Warnings");
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        warningsPopup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Custom, AllowsTransparency = true, StaysOpen = false,
            CustomPopupPlacementCallback = (size, target, _) => [new CustomPopupPlacement(new Point(target.Width - size.Width, target.Height + 4), PopupPrimaryAxis.Horizontal)],
            Child = new Border
            {
                Background = B("surfaceBrush"), BorderBrush = B("border-strongBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 4, 0, 4), Width = 440,
                Child = new ScrollViewer { MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = warningsList },
            },
        };
        AutomationProperties.SetName(warningsPopup.Child, "Warnings");
        warningsPopup.Closed += (_, _) => warningsClosedAt = DateTime.UtcNow;
        // A click on the button while the dropdown is open closes it (the popup closes on the press), not reopens it.
        button.Click += (_, _) =>
        {
            if (DateTime.UtcNow - warningsClosedAt < TimeSpan.FromMilliseconds(300)) return;
            ShowWarningsList();
            warningsPopup.IsOpen = true;
        };
        shell.WarningsChanged += ShowWarnings;
        Closed += (_, _) => shell.WarningsChanged -= ShowWarnings;
        ShowWarnings();
        return button;
    }

    void ShowWarnings()
    {
        if (warningsButton is null || warningsCount is null || warningsIcon is null || warningsPopup is null) return;
        var count = shell.Warnings.Count;
        var any = count > 0 || shell.ResolvedWarnings.Count > 0;
        warningsCount.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        warningsCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        warningsIcon.Foreground = B(count > 0 ? "warnBrush" : "mutedBrush");
        warningsButton.ToolTip = count == 0 ? "No open warnings" : count == 1 ? "1 warning" : $"{count} warnings";
        warningsButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) warningsPopup.IsOpen = false;
        else if (warningsPopup.IsOpen) ShowWarningsList();
    }

    void ShowWarningsList()
    {
        warningsList.Children.Clear();
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        foreach (var warning in shell.Warnings.OrderByDescending(w => w.At))
        {
            if (warningsList.Children.Count > 0)
                warningsList.Children.Add(new Border { Height = 1, Background = B("borderBrush"), Margin = new Thickness(0, 2, 0, 2) });
            warningsList.Children.Add(WarningRow(warning, now));
        }
        if (shell.Warnings.Count == 0)
            warningsList.Children.Add(new TextBlock { Text = "No open warnings.", Style = S("Text13"), Foreground = B("mutedBrush"), Margin = new Thickness(12, 6, 12, 6) });
        if (shell.ResolvedWarnings.Count == 0) return;
        var header = new DockPanel { Margin = new Thickness(12, 8, 8, 2) };
        var clear = RowButton("Clear", "Forgets the earlier warnings");
        clear.Margin = new Thickness(0);
        clear.Click += (_, _) => shell.ClearWarningHistory();
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(new TextBlock { Text = "Earlier", Style = S("Text12"), FontWeight = FontWeights.SemiBold, Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        warningsList.Children.Add(new Border { Height = 1, Background = B("borderBrush"), Margin = new Thickness(0, 4, 0, 0) });
        warningsList.Children.Add(header);
        foreach (var (warning, how, at) in shell.ResolvedWarnings)
        {
            var row = new StackPanel { Margin = new Thickness(12, 4, 12, 4) };
            row.Children.Add(new TextBlock
            {
                Text = $"{warning.Title} · {LabelOf(warning.SessionId)} · {how} {MonitorWarning.Ago(at, now)}", Style = S("Text12"),
                Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock { Text = warning.Message, Style = S("Text12"), Foreground = B("mutedBrush"), TextWrapping = TextWrapping.Wrap });
            AutomationProperties.SetName(row, $"{warning.Title}, {how}: {warning.Message}");
            warningsList.Children.Add(row);
        }
    }

    FrameworkElement WarningRow(MonitorWarning warning, long now)
    {
        var column = new StackPanel { Margin = new Thickness(12, 6, 12, 8) };
        var heading = new DockPanel();
        var ago = new TextBlock { Text = MonitorWarning.Ago(warning.Since, now), Style = S("Text12"), Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(ago, Dock.Right);
        heading.Children.Add(ago);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new VectorIcon { Kind = "warning", Size = 13, Foreground = B("warnBrush"), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock
        {
            Text = $"{warning.Title} · {LabelOf(warning.SessionId)}", Style = S("Text13"), FontWeight = FontWeights.SemiBold,
            Foreground = B("textBrush"), Margin = new Thickness(6, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        heading.Children.Add(title);
        column.Children.Add(heading);
        column.Children.Add(new TextBlock { Text = warning.Message, Style = S("Text12"), Foreground = B("textBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (warning.CommandLine.Length > 0)
            column.Children.Add(new TextBlock
            {
                Text = warning.CommandLine, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = B("mutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = warning.CommandLine, Margin = new Thickness(0, 3, 0, 0),
            });
        var error = new TextBlock { Style = S("Text12"), Foreground = B("dangerBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var notify = RowButton("Notify thread", "Types a note about this into the thread, for its agent to handle");
        notify.IsEnabled = shell.CanNotify(warning);
        notify.Click += async (_, _) =>
        {
            notify.IsEnabled = false;
            await shell.NotifyAsync(warning);
            ((TextBlock)notify.Content).Text = "Notified";
        };
        buttons.Children.Add(notify);
        var kill = RowButton(warning.Kind == "leftover" ? "Kill processes" : "Kill process",
            warning.Kind == "leftover" ? "Ends the processes the ended thread left running" : $"Ends {warning.Name} (pid {warning.Pid}) and what it started");
        kill.Click += async (_, _) =>
        {
            kill.IsEnabled = false;
            if (await shell.KillAsync(warning) is { } failed)
            {
                error.Text = failed;
                error.Visibility = Visibility.Visible;
                kill.IsEnabled = true;
            }
        };
        buttons.Children.Add(kill);
        var dismiss = RowButton("Dismiss", "Hides this warning");
        dismiss.Click += (_, _) => shell.DismissWarning(warning);
        buttons.Children.Add(dismiss);
        column.Children.Add(buttons);
        column.Children.Add(error);
        AutomationProperties.SetName(column, $"{warning.Title}: {warning.Message}");
        return column;
    }

    static Button RowButton(string text, string tooltip)
    {
        var button = new Button { Style = S("ButtonBaseStyle"), Content = new TextBlock { Text = text, Style = S("Text12") }, ToolTip = tooltip, Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(button, text);
        return button;
    }
}
