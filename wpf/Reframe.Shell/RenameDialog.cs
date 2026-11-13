using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Reframe.Shell;

/// <summary>
/// Rename from a context menu. Electron edits the name in place (NameInput.tsx, in the row or the tile header); until
/// the shell has inline editing this small dialog asks for it (an empty name: the automatic one again).
/// </summary>
sealed class RenameDialog : Window
{
    readonly TextBox input;

    static Style S(string key) => (Style)Application.Current.FindResource(key);

    RenameDialog(Window owner, string current)
    {
        Owner = owner;
        Title = "Rename";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("surfaceBrush");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("textBrush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("FontFamily.Ui");
        FontSize = 13;

        input = new TextBox { Style = S("SearchBox"), Text = current, Tag = "Name", MaxLength = 100 };
        AutomationProperties.SetName(input, "Name");
        var ok = new Button { Style = S("PrimaryButton"), Content = "Rename", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Style = S("ButtonBaseStyle"), Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
            Keyboard.Focus(input);
        };
    }

    /// <summary>The new name, or null when cancelled.</summary>
    public static string? Ask(Window owner, string current)
    {
        var dialog = new RenameDialog(owner, current);
        return dialog.ShowDialog() == true ? dialog.input.Text : null;
    }
}
