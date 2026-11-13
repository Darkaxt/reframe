using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Reframe.Shell.Core;
using static Reframe.Shell.Core.Strings;

namespace Reframe.Shell;

/// <summary>
/// TakeoverDialog.tsx: confirms taking over the session(s) of a terminal window dropped onto the window. The title, the
/// question (one session, or several to pick from with check boxes), each session as a bordered row (name, folder), the
/// hint, then Cancel and Take over (disabled while none is picked; it has the focus).
/// </summary>
sealed class TakeoverDialog
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    public Border Frame { get; }
    public Button ConfirmButton { get; }
    /// <summary>The picked sessions' ids, or null (Cancel).</summary>
    public event Action<List<string>?>? Answered;

    public TakeoverDialog(IReadOnlyList<ForeignCandidate> candidates)
    {
        var picked = candidates.Where(c => c.Preselected).Select(c => c.ClaudeSessionId).ToHashSet();
        var single = candidates.Count == 1;
        // .modal: a column, 12 apart.
        var column = new StackPanel();
        void Add(FrameworkElement e)
        {
            if (column.Children.Count > 0) e.Margin = new Thickness(0, 12, 0, 0);
            column.Children.Add(e);
        }
        var title = new TextBlock { Text = T("foreign.title"), FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        Add(title);
        Add(new TextBlock { Text = T(single ? "foreign.single" : "foreign.multiple"), Style = S("Text13"), TextWrapping = TextWrapping.Wrap });

        ConfirmButton = new Button { Style = S("PrimaryButton"), Content = new TextBlock { Text = T("takeover.confirm"), Style = S("Text13") }, IsEnabled = picked.Count > 0 };
        AutomationProperties.SetName(ConfirmButton, T("takeover.confirm"));

        // .takeover-list: rows 4 apart; .takeover-item: 6px 8px, border, radius 6, bg, the check box 10 before the text.
        var list = new StackPanel();
        foreach (var c in candidates)
        {
            var row = new DockPanel();
            if (!single)
            {
                var box = new CheckBox { Style = S("SettingCheckBox"), IsChecked = picked.Contains(c.ClaudeSessionId), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center }; // + the template's 8 before its (empty) content = .takeover-item gap 10
                AutomationProperties.SetName(box, c.Name ?? ForeignMatch.FolderName(c.Cwd));
                box.Checked += (_, _) => Pick(c.ClaudeSessionId, true);
                box.Unchecked += (_, _) => Pick(c.ClaudeSessionId, false);
                DockPanel.SetDock(box, Dock.Left);
                row.Children.Add(box);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = c.Name ?? ForeignMatch.FolderName(c.Cwd), Style = S("Text13"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = c.Cwd, Style = S("Text12"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = c.Cwd });
            row.Children.Add(text);
            var item = new Border
            {
                Padding = new Thickness(8, 6, 8, 6), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Background = B("bgBrush"), Child = row,
                Margin = new Thickness(0, list.Children.Count > 0 ? 4 : 0, 0, 0),
            };
            // A <label>: a click anywhere on the row toggles its check box.
            if (row.Children[0] is CheckBox toggle)
                item.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource is DependencyObject d && IsWithin(d, toggle)) return;
                    toggle.IsChecked = toggle.IsChecked != true;
                };
            list.Children.Add(item);
        }
        AutomationProperties.SetName(list, T("foreign.title"));
        Add(list);
        Add(new TextBlock { Text = T("foreign.detail"), Style = S("Text12"), Foreground = B("mutedBrush"), TextWrapping = TextWrapping.Wrap });

        // .button-row.end: Cancel, then Take over, 8 apart, at the right.
        var cancel = new Button { Style = S("ButtonBaseStyle"), Content = new TextBlock { Text = T("dialog.cancel"), Style = S("Text13") }, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(cancel, T("dialog.cancel"));
        cancel.Click += (_, _) => Answered?.Invoke(null);
        ConfirmButton.Click += (_, _) => Answered?.Invoke([.. candidates.Select(c => c.ClaudeSessionId).Where(picked.Contains)]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ConfirmButton);
        Add(buttons);

        (Frame, _) = ModalHost.Frame(520, column);
        AutomationProperties.SetName(Frame, T("foreign.title"));

        void Pick(string id, bool on)
        {
            if (on) picked.Add(id);
            else picked.Remove(id);
            ConfirmButton.IsEnabled = picked.Count > 0;
        }
    }

    static bool IsWithin(DependencyObject d, DependencyObject ancestor)
    {
        for (var current = d; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current == ancestor) return true;
        return false;
    }
}
