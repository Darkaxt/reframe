using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Reframe.Shell;

/// <summary>A thread's properties (its context menu's Properties…): name/value rows whose values can be selected and
/// copied, a disk-usage section filled in once measured (it reads the thread's files off the UI thread), Copy all
/// and Close.</summary>
sealed class PropertiesDialog
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    readonly Grid rows = new();
    readonly List<(string Name, string Value)> all = [];

    public Border Frame { get; }
    public Button CloseButton { get; }
    public event Action? CloseRequested;

    public PropertiesDialog(string title, IEnumerable<(string Name, string Value)> properties, Task<List<(string Name, string Value)>> disk)
    {
        var column = new StackPanel();
        var heading = new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, ToolTip = title,
        };
        column.Children.Add(heading);
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.Margin = new Thickness(0, 12, 0, 0);
        column.Children.Add(rows);
        foreach (var (name, value) in properties) Row(name, value);
        Section("Disk usage");
        var measuring = Row("", "Measuring…");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var copy = Button("Copy all", primary: false);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, all.Select(p => p.Name.Length == 0 ? p.Value : $"{p.Name}: {p.Value}")));
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // The clipboard was busy; another click tries again.
            }
        };
        buttons.Children.Add(copy);
        CloseButton = Button("Close", primary: true);
        CloseButton.Margin = new Thickness(8, 0, 0, 0);
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        buttons.Children.Add(CloseButton);
        column.Children.Add(buttons);
        (Frame, _) = ModalHost.Frame(560, column);
        AutomationProperties.SetName(Frame, title);

        _ = ShowDiskAsync();
        async Task ShowDiskAsync()
        {
            List<(string Name, string Value)> measured;
            try
            {
                measured = await disk;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                measured = [("", $"Could not read: {e.Message}")];
            }
            rows.Children.Remove(measuring.Value);
            all.RemoveAll(p => p.Value == "Measuring…");
            foreach (var (name, value) in measured) Row(name, value);
        }
    }

    void Section(string name)
    {
        var text = new TextBlock { Text = name, Style = S("Text12"), FontWeight = FontWeights.SemiBold, Foreground = B("mutedBrush"), Margin = new Thickness(0, 12, 0, 4) };
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(text, rows.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(text, 2);
        rows.Children.Add(text);
        all.Add(("", ""));
        all.Add(("", name));
    }

    (TextBlock Name, TextBox Value) Row(string name, string value)
    {
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = rows.RowDefinitions.Count - 1;
        var label = new TextBlock { Text = name, Style = S("Text13"), Foreground = B("mutedBrush"), Margin = new Thickness(0, 3, 14, 3), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(label, row);
        rows.Children.Add(label);
        // Selectable, as Explorer's properties: a read-only text box without its frame.
        var text = new TextBox
        {
            Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = B("textBrush"),
            Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap, FontSize = 13,
            CaretBrush = B("textBrush"),
        };
        AutomationProperties.SetName(text, name.Length > 0 ? name : value);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        rows.Children.Add(text);
        all.Add((name, value));
        return (label, text);
    }

    static Button Button(string text, bool primary)
    {
        var button = new Button { Style = S(primary ? "PrimaryButton" : "ButtonBaseStyle"), Content = new TextBlock { Text = text, Style = S("Text13") } };
        AutomationProperties.SetName(button, text);
        return button;
    }
}
