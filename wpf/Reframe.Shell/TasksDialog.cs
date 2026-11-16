using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// The top bar's Tasks: the scheduled tasks the threads saved (ScheduledTasks), with their thread, schedule, next
/// run and last result; each can be run now, paused or resumed, edited or deleted. Import from Codex brings over the
/// Codex automations whose thread has the name of one of Reframe's threads.
/// </summary>
sealed class TasksDialog : Window
{
    readonly ShellController shell;
    readonly StackPanel list = new();
    readonly Button import;

    static Style S(string key) => (Style)Application.Current.FindResource(key);
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    TasksDialog(Window owner, ShellController shell)
    {
        this.shell = shell;
        Owner = owner;
        Title = "Scheduled tasks";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = B("surfaceBrush");
        Foreground = B("textBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("FontFamily.Ui");
        FontSize = 13;

        var hint = new TextBlock
        {
            Style = S("Text12"), Foreground = B("mutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "Threads save tasks when you ask them for something later or on a schedule. At each run Reframe types the prompt into the thread (resuming it if it ended) once it is idle. Runs missed while Reframe was closed run once when it opens.",
        };
        import = new Button { Style = S("ButtonBaseStyle"), Content = "Import from Codex…" };
        import.Click += (_, _) => Import();
        var close = new Button { Style = S("PrimaryButton"), Content = "Close", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(import, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(import);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        shell.TasksChanged += Refresh;
        Closed += (_, _) => shell.TasksChanged -= Refresh;
        Refresh();
    }

    public static void Open(Window owner, ShellController shell) => new TasksDialog(owner, shell).ShowDialog();

    string ThreadName(string id) =>
        shell.State.Sessions.TryGetValue(id, out var r) ? StateValues.SessionLabel(r.Name, r.ClaudeName, r.Cwd) : "(thread not in Reframe)";

    void Refresh()
    {
        list.Children.Clear();
        var tasks = shell.ScheduledTasksList;
        if (tasks.Count == 0)
            list.Children.Add(new TextBlock { Style = S("Text13"), Foreground = B("mutedBrush"), Text = "No scheduled tasks yet. Ask a thread to do something on a schedule." });
        foreach (var task in tasks) list.Children.Add(Row(task));
        import.IsEnabled = shell.CodexTasks().Count > 0;
    }

    UIElement Row(ScheduledTask task)
    {
        var runs = ScheduledTasks.ReadRuns(ShellController.TasksDir, task.Id);
        var next = task.Active ? ScheduledTasks.NextDue(task, runs) : null;
        var when = shell.IsRunPending(task.Id) ? "run waiting for its thread"
            : !task.Active ? "paused"
            : next is { } n ? $"next {n.LocalDateTime:ddd d MMM HH:mm}" : "no more runs";
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Style = S("Text13"), FontWeight = FontWeights.SemiBold, Text = task.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock
        {
            Style = S("Text12"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            Text = $"{ThreadName(task.TargetThreadId)}{(task.NewChat ? " (new thread each run)" : "")} · {ScheduledTasks.Describe(task.RRule)} · {when}",
        });
        if (runs.LastResult is { } last)
            info.Children.Add(new TextBlock
            {
                Style = S("Text11"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"Last: {last}{(runs.LastResultAt is { } at ? $" ({DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime:d MMM HH:mm})" : "")}",
            });
        info.ToolTip = task.Prompt;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Action(string text, Action click)
        {
            var b = new Button { Style = S("ButtonBaseStyle"), Content = text, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(b, $"{text} {task.Name}");
            b.Click += (_, _) => click();
            actions.Children.Add(b);
            return b;
        }
        Action("Run now", () => shell.RunTaskNow(task)).IsEnabled = !shell.IsRunPending(task.Id);
        Action(task.Active ? "Pause" : "Resume", () => shell.SetTaskStatus(task, !task.Active));
        Action("Edit", () => Edit(task));
        Action("Delete", () =>
        {
            if (CloseWindowDialog.Confirm(this, null, $"Delete the scheduled task \"{task.Name}\"?", task.Prompt, "Delete")) shell.DeleteTask(task);
        });

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(info);
        return new Border { Child = row, BorderBrush = B("borderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8), Margin = new Thickness(0, 0, 0, 8) };
    }

    void Edit(ScheduledTask task)
    {
        if (TaskEditor.Ask(this, task) is { } changes) shell.ChangeTask(task, changes);
    }

    void Import()
    {
        var candidates = shell.CodexTasks();
        if (candidates.Count == 0) return;
        var boxes = candidates.Select(c => new CheckBox
        {
            Style = S("SettingCheckBox"), IsChecked = true, Margin = new Thickness(0, 0, 0, 6),
            Content = new TextBlock { Style = S("Text13"), TextWrapping = TextWrapping.Wrap, Text = $"{c.Task.Name} → \"{c.ThreadName}\" ({ScheduledTasks.Describe(c.Task.RRule)}{(c.Task.Active ? "" : ", paused")})" },
        }).ToList();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Style = S("Text12"), Foreground = B("mutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "These Codex tasks run in a Codex thread with the name of one of Reframe's threads. Imported copies run in that Reframe thread from now on; Codex's own files are not changed, so pause the Codex task to keep it from running twice.",
        });
        foreach (var b in boxes) panel.Children.Add(b);
        var dialog = new Window
        {
            Owner = this, Title = "Import from Codex", Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = 600, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Background, Foreground = Foreground,
            FontFamily = FontFamily, FontSize = FontSize,
        };
        var ok = new Button { Style = S("PrimaryButton"), Content = "Import", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        var cancel = new Button { Style = S("ButtonBaseStyle"), Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (dialog.ShowDialog() != true) return;
        shell.ImportCodexTasks(candidates.Where((_, i) => boxes[i].IsChecked == true));
    }
}

/// <summary>A scheduled task's name, prompt, rule and notifications, edited; the changed fields, or null.</summary>
sealed class TaskEditor : Window
{
    readonly TextBox name, prompt, rule;
    readonly CheckBox notifyAll;
    readonly TextBlock problem;

    static Style S(string key) => (Style)Application.Current.FindResource(key);

    TaskEditor(Window owner, ScheduledTask task)
    {
        Owner = owner;
        Title = "Edit task";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = owner.Background;
        Foreground = owner.Foreground;
        FontFamily = owner.FontFamily;
        FontSize = 13;

        name = new TextBox { Style = S("SearchBox"), Text = task.Name, Tag = "Name", Margin = new Thickness(0, 0, 0, 8) };
        prompt = new TextBox
        {
            Style = S("SearchBox"), Text = task.Prompt, Tag = "Prompt", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8),
        };
        rule = new TextBox { Style = S("SearchBox"), Text = task.RRule.Replace("\n", "\\n"), Tag = "Repeat rule (RRULE)", Margin = new Thickness(0, 0, 0, 4) };
        var described = new TextBlock { Style = S("Text12"), Opacity = 0.7, Margin = new Thickness(0, 0, 0, 8), Text = ScheduledTasks.Describe(task.RRule) };
        problem = new TextBlock { Style = S("Text12"), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        rule.TextChanged += (_, _) =>
        {
            var text = rule.Text.Replace("\\n", "\n");
            var ok = Recurrence.Parse(text) is not null;
            described.Text = ok ? ScheduledTasks.Describe(text) : "";
            problem.Text = ok ? "" : "Not a rule Reframe follows, e.g. FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=15";
        };
        notifyAll = new CheckBox
        {
            Style = S("SettingCheckBox"), IsChecked = task.NotificationPolicy == "all", Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { Style = S("Text13"), Text = "Notify after every run (otherwise only when a run could not be delivered)" },
        };
        foreach (var (box, label) in new[] { (name, "Name"), (prompt, "Prompt"), (rule, "Repeat rule") }) AutomationProperties.SetName(box, label);

        var save = new Button { Style = S("PrimaryButton"), Content = "Save", Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            if (Recurrence.Parse(rule.Text.Replace("\\n", "\n")) is null || name.Text.Trim().Length == 0 || prompt.Text.Trim().Length == 0) return;
            DialogResult = true;
        };
        var cancel = new Button { Style = S("ButtonBaseStyle"), Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var e in new UIElement[] { name, prompt, rule, described, notifyAll, problem, buttons }) panel.Children.Add(e);
        Content = panel;
        Loaded += (_, _) => Keyboard.Focus(prompt);
    }

    public static Dictionary<string, object>? Ask(Window owner, ScheduledTask task)
    {
        var editor = new TaskEditor(owner, task);
        if (editor.ShowDialog() != true) return null;
        return new Dictionary<string, object>
        {
            ["name"] = editor.name.Text.Trim(),
            ["prompt"] = editor.prompt.Text.Trim(),
            ["rrule"] = editor.rule.Text.Trim().Replace("\\n", "\n"),
            ["notification_policy"] = editor.notifyAll.IsChecked == true ? "all" : task.NotificationPolicy == "all" ? "important" : task.NotificationPolicy,
        };
    }
}
