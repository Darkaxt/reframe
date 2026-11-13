using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Reframe.Shell.Core;
using static Reframe.Shell.Core.Strings;

namespace Reframe.Shell;

/// <summary>
/// The subscription quota at the foot of the session list (QuotaPanel.tsx, .sidebar-quota): per source, its heading and
/// one row per window: the window's length, a 6 px bar (green, orange from 70 %, red from 90 %), the percentage and
/// when it resets. Hidden while there is no quota. The rows' tooltips say how much is used, when it resets and as of when.
/// </summary>
sealed class QuotaPanel : Border
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    readonly StackPanel content = new();

    public QuotaPanel()
    {
        BorderBrush = B("borderBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(6, 2, 6, 8);
        Child = content;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, T("quota.label"));
    }

    public void Show(IReadOnlyList<ProviderQuota> shown, double now)
    {
        content.Children.Clear();
        Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var quota in shown)
        {
            // .sidebar-heading in .sidebar-quota: margin 6px 6px 4px.
            content.Children.Add(new SpacedText
            {
                Text = T(quota.Source == "claude" ? "quota.claude" : "quota.codex").ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold,
                LineHeight = 15.4, Foreground = B("mutedBrush"), Margin = new Thickness(6, 6, 6, 4), HorizontalAlignment = HorizontalAlignment.Left,
            });
            foreach (var window in quota.Windows) content.Children.Add(Row(quota, window, now));
        }
    }

    /// <summary>.quota-row: columns 2.2em, 1fr, 3em and auto, 6 apart; padding 2px 6px; 11 px.</summary>
    static Grid Row(ProviderQuota quota, QuotaWindow window, double now)
    {
        var used = Quota.Percent(window.UsedPercent);
        var label = Quota.WindowLabel(window.Minutes) is { Length: > 0 } l ? l : window.Id;
        var row = new Grid { Margin = new Thickness(6, 2, 6, 2), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11 * 2.2 + 6) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6 + 33) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock Cell(string text, int column, Brush brush, TextAlignment align = TextAlignment.Left)
        {
            var cell = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Text11"), Foreground = brush, TextAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(cell, column);
            row.Children.Add(cell);
            return cell;
        }
        Cell(label, 0, B("mutedBrush"));
        var fill = new Border
        {
            Background = B(Quota.Level(window.UsedPercent) switch { "danger" => "dangerBrush", "warn" => "orangeBrush", _ => "okBrush" }),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = B("surface-2Brush"), Child = fill, VerticalAlignment = VerticalAlignment.Center };
        bar.SizeChanged += (_, _) =>
        {
            fill.Width = Math.Round(bar.ActualWidth * used / 100.0);
            bar.Clip = new RectangleGeometry(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight), 3, 3);
        };
        AutomationProperties.SetName(bar, $"{label}: {T("quota.used", ("percent", used))}");
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        Cell($"{used}%", 2, B("textBrush"), TextAlignment.Right);
        var reset = Cell(window.ResetsAt is { } at ? T("quota.resets", ("when", Quota.UntilLabel(at, now))) : "", 3, B("mutedBrush"));
        reset.Margin = new Thickness(6, 0, 0, 0);
        row.ToolTip = string.Join("\n", new[]
        {
            T("quota.used", ("percent", used)),
            window.ResetsAt is { } resets ? T("quota.resetsAt", ("when", RelativeTime.Exact(resets))) : "",
            T("quota.observed", ("when", RelativeTime.Exact(quota.At))),
        }.Where(s => s.Length > 0));
        return row;
    }
}
