using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The top bar's messages to running threads: Pause, Continue, Clean up and a message of the user's own,
/// each to every running thread or (the scope toggle) only to the one this window shows or last focused.</summary>
sealed partial class MainWindow
{
    bool broadcastToAll = true;
    /// <summary>The tile that had the focus last (the toolbar takes it away when clicked).</summary>
    string? lastFocused;

    StackPanel BroadcastButtons()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(GlyphButton("E823", "Scheduled tasks", "Scheduled tasks of the threads", () => TasksDialog.Open(this, shell)));
        panel.Children.Add(new Border { Width = 1, Height = 16, Background = B("borderBrush"), Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(GlyphButton("E769", "Pause threads", "Ask running threads to reach a safe pause point", () => _ = SendBroadcastAsync(Broadcast.Pause)));
        panel.Children.Add(GlyphButton("E768", "Continue threads", "Tell the threads paused from here to continue", () => _ = SendBroadcastAsync(Broadcast.Continue)));
        panel.Children.Add(GlyphButton("E74D", "Clean up old files", "Ask running threads to delete their old and superseded files first", () => _ = SendBroadcastAsync(Broadcast.Cleanup)));
        panel.Children.Add(GlyphButton("E724", "Message threads", "Send a message of your own to running threads", () => _ = SendBroadcastAsync(Broadcast.Custom)));
        var scope = new Button { Style = S("ToolbarIcon"), Width = double.NaN, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 0, 0) };
        void ShowScope()
        {
            scope.Content = new TextBlock { Text = broadcastToAll ? "All" : "This", Style = S("Text12"), VerticalAlignment = VerticalAlignment.Center };
            scope.ToolTip = broadcastToAll ? "Messages go to all running threads (click: only this one)" : "Messages go to the thread shown or focused here (click: all)";
            AutomationProperties.SetName(scope, broadcastToAll ? "Message all threads" : "Message this thread");
        }
        ShowScope();
        scope.Click += (_, _) =>
        {
            broadcastToAll = !broadcastToAll;
            ShowScope();
        };
        WindowChrome.SetIsHitTestVisibleInChrome(scope, true);
        panel.Children.Add(scope);
        return panel;
    }

    static Button GlyphButton(string glyph, string name, string tooltip, Action click)
    {
        var button = new Button
        {
            Style = S("ToolbarIcon"), ToolTip = tooltip, Margin = new Thickness(0, 0, 2, 0),
            // The glyph's code point in Segoe Fluent Icons (Pause E769, Play E768, Delete E74D, Send E724).
            Content = new TextBlock { Text = ((char)Convert.ToInt32(glyph, 16)).ToString(), FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(button, name);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A scheduled task's thread placed in the window without moving the focus.</summary>
    public void PlaceSessionQuietly(string sessionId) => Dispatch(w => LayoutActions.AddTile(w, sessionId));

    /// <summary>The thread "This" means: the one a single window shows, else the tile focused last.</summary>
    string? BroadcastFocus() => LayoutActions.ViewModeOf(Win) == "single" ? selected : lastFocused ?? selected;

    async Task SendBroadcastAsync(string kind)
    {
        string[]? scope = null;
        if (!broadcastToAll)
        {
            if (BroadcastFocus() is not { } one)
            {
                MessageBox.Show(this, "No thread is shown or focused in this window.", "Reframe", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            scope = [one];
        }
        var targets = shell.BroadcastTargets(kind, scope);
        if (targets.Count == 0)
        {
            MessageBox.Show(this, Broadcast.NoTarget(kind), "Reframe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? text;
        if (kind == Broadcast.Custom)
        {
            text = MessageDialog.Ask(this, Broadcast.Question(kind, targets.Count));
            if (string.IsNullOrWhiteSpace(text)) return;
        }
        else
        {
            text = Broadcast.TextOf(kind)!;
            if (!CloseWindowDialog.Confirm(this, null, Broadcast.Question(kind, targets.Count), text, "Send")) return;
        }
        // The statuses may have moved on while the question was open.
        var now = shell.BroadcastTargets(kind, targets.Select(t => t.SessionId).ToArray());
        await shell.BroadcastAsync(kind, text.Trim(), now);
    }
}

/// <summary>A message of the user's own for the running threads (several lines allowed; Ctrl+Enter sends it).</summary>
sealed class MessageDialog : Window
{
    readonly TextBox input;

    static Style S(string key) => (Style)Application.Current.FindResource(key);

    MessageDialog(Window owner, string question)
    {
        Owner = owner;
        Title = "Message threads";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("surfaceBrush");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("textBrush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("FontFamily.Ui");
        FontSize = 13;

        var label = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        input = new TextBox
        {
            Style = S("SearchBox"), Tag = "Message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 120, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(input, "Message");
        var ok = new Button { Style = S("PrimaryButton"), Content = "Send", Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Style = S("ButtonBaseStyle"), Content = "Cancel", IsCancel = true };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DialogResult = true;
                e.Handled = true;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => Keyboard.Focus(input);
    }

    /// <summary>The message, or null when cancelled.</summary>
    public static string? Ask(Window owner, string question)
    {
        var dialog = new MessageDialog(owner, question);
        return dialog.ShowDialog() == true ? dialog.input.Text : null;
    }
}
