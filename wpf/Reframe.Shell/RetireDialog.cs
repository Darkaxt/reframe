using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Archiving or deleting chats, followed in a popup: what is being done (checking, the question, archiving/deleting with
/// the time so far), then each chat's outcome. Closed while it works, the work goes on and the window's result strip
/// reports the outcome (<see cref="Visible"/> false).
/// </summary>
sealed class RetireDialog
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    readonly string operation;
    readonly TextBlock title;
    readonly TextBlock status;
    readonly ProgressBar bar;
    readonly TextBlock detail;
    readonly StackPanel lines;
    readonly StackPanel buttons;
    readonly DispatcherTimer clock;
    DateTime phaseStarted;
    string phaseText = "";
    TaskCompletionSource<bool>? answer;

    public Border Frame { get; }
    /// <summary>Whether the popup is still shown (false once closed by the user or by <see cref="Close"/>).</summary>
    public bool Visible { get; private set; } = true;
    /// <summary>The user asks to close it (a button); the owner closes the modal.</summary>
    public event Action? CloseRequested;
    /// <summary>A button that should take the keyboard focus now (the action of a question, Close).</summary>
    public event Action<Button>? FocusChanged;

    public RetireDialog(string operation, int count)
    {
        this.operation = operation;
        var column = new StackPanel();
        title = new TextBlock
        {
            Text = (operation == "archive" ? "Archive " : "Delete ") + (count == 1 ? "chat" : $"{count} chats"),
            FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        column.Children.Add(title);
        status = new TextBlock { Style = S("Text13"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        column.Children.Add(status);
        bar = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 10, 0, 0), Foreground = B("accentBrush"), Background = B("bgBrush"), BorderThickness = new Thickness(0) };
        column.Children.Add(bar);
        detail = new TextBlock { Style = S("Text12"), Foreground = B("mutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        column.Children.Add(new ScrollViewer { Content = detail, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        lines = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        column.Children.Add(new ScrollViewer { Content = lines, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        column.Children.Add(buttons);
        (Frame, _) = ModalHost.Frame(520, column);
        AutomationProperties.SetName(Frame, title.Text);

        clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => ShowPhase();
        Phase($"Checking {(count == 1 ? "the chat" : $"{count} chats")}…");
    }

    /// <summary>A step under way: its text with the seconds so far, the moving bar, and Run in background.</summary>
    public void Phase(string text)
    {
        phaseText = text;
        phaseStarted = DateTime.UtcNow;
        bar.Visibility = Visibility.Visible;
        detail.Visibility = Visibility.Collapsed;
        ShowPhase();
        clock.Start();
        SetButtons((Button("Run in background", primary: false), () => CloseRequested?.Invoke()));
    }

    void ShowPhase()
    {
        var seconds = (int)(DateTime.UtcNow - phaseStarted).TotalSeconds;
        status.Text = seconds >= 2 ? $"{phaseText} ({seconds} s)" : phaseText;
    }

    /// <summary>The question (Electron's confirmation box): its text and details, then Cancel and the action. False when
    /// cancelled or closed.</summary>
    public Task<bool> ConfirmAsync(string message, string details, string action)
    {
        clock.Stop();
        bar.Visibility = Visibility.Collapsed;
        status.Text = message;
        detail.Text = details;
        detail.Visibility = Visibility.Visible;
        answer = new TaskCompletionSource<bool>();
        var go = Button(action, primary: true);
        SetButtons((Button("Cancel", primary: false), () => answer.TrySetResult(false)), (go, () => answer.TrySetResult(true)));
        FocusChanged?.Invoke(go);
        return answer.Task;
    }

    /// <summary>The outcome: the counts, each chat's line, and Close.</summary>
    public void Done(IReadOnlyList<ChatBatchEntry> results)
    {
        clock.Stop();
        bar.Visibility = Visibility.Collapsed;
        detail.Visibility = Visibility.Collapsed;
        var verb = operation == "archive" ? "Archiving" : "Deleting";
        var failed = results.Count(r => r.Status == "failed");
        status.Text = results.Count == 0 ? $"{verb} finished; nothing changed."
            : $"{verb} finished. {results.Count(r => r.Status == "completed")} completed · {results.Count(r => r.Status == "skipped")} skipped · {failed} failed";
        lines.Children.Clear();
        foreach (var entry in results)
        {
            var line = new TextBlock { Style = S("Text13"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, lines.Children.Count > 0 ? 4 : 0, 0, 0) };
            var mark = entry.Status switch { "completed" => "✓ ", "failed" => "✗ ", _ => "– " };
            line.Inlines.Add(new System.Windows.Documents.Run(mark) { Foreground = entry.Status == "failed" ? B("dangerBrush") : B("mutedBrush") });
            line.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run(entry.Label)));
            line.Inlines.Add(new System.Windows.Documents.Run(ChatHistory.Line(entry)[entry.Label.Length..]));
            lines.Children.Add(line);
        }
        var close = Button("Close", primary: true);
        SetButtons((close, () => CloseRequested?.Invoke()));
        FocusChanged?.Invoke(close);
    }

    /// <summary>The popup went away (its modal closed): a pending question counts as cancelled.</summary>
    public void Closed()
    {
        Visible = false;
        clock.Stop();
        answer?.TrySetResult(false);
    }

    static Button Button(string text, bool primary)
    {
        var button = new Button { Style = S(primary ? "PrimaryButton" : "ButtonBaseStyle"), Content = new TextBlock { Text = text, Style = S("Text13") } };
        AutomationProperties.SetName(button, text);
        return button;
    }

    void SetButtons(params (Button Button, Action Click)[] items)
    {
        buttons.Children.Clear();
        foreach (var (button, click) in items)
        {
            button.Click += (_, _) => click();
            if (buttons.Children.Count > 0) button.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(button);
        }
    }
}
