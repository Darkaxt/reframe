using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reframe.Shell.Core;
using static Reframe.Shell.Core.Strings;

namespace Reframe.Shell;

/// <summary>What Settings shows besides the preferences (SettingsDialog.tsx's other props).</summary>
sealed class SettingsContext
{
    public required Func<AppState> State { get; init; }
    /// <summary>Applies a change (Settings.Apply, then saved); whether something changed.</summary>
    public required Func<PreferencesPatch, bool> Set { get; init; }
    /// <summary>Restores hidden threads (null: all of them).</summary>
    public required Action<IReadOnlyCollection<string>?> Restore { get; init; }
    /// <summary>The shell's own login entry; null when it must not be touched (a capture).</summary>
    public Autostart? Autostart { get; init; }
    /// <summary>Whether GPU acceleration was on when the shell started (the hint says a change needs a restart).</summary>
    public bool GpuAccelerated { get; init; }
    /// <summary>Where the claude sessions run is (findClaude); null: not shown (a capture, like Electron's reference).</summary>
    public Func<string?, string?>? FindClaude { get; init; }
    public bool Capture { get; init; }
}

/// <summary>
/// Settings (SettingsDialog.tsx): the preferences of src/shared/state.ts, each changed at once (and saved) as Electron
/// does, the shell's start with the system, and the hidden threads with their Restore buttons. The window is rebuilt
/// after each change, at its scroll position, as React renders it again. The shell has no updater: Check for updates is
/// left out as Electron does without one.
/// </summary>
sealed class SettingsDialog
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    readonly SettingsContext context;
    readonly StackPanel content = new();
    readonly ScrollViewer scroll;
    public Border Frame { get; }
    public Button CloseButton { get; private set; } = null!;
    public FrameworkElement HiddenSection { get; private set; } = null!;
    public event Action? CloseRequested;
    string model;

    public SettingsDialog(SettingsContext context)
    {
        this.context = context;
        model = context.State().Preferences.DefaultModel ?? "";
        (Frame, scroll) = ModalHost.Frame(460, content);
        AutomationProperties.SetName(Frame, T("settings.title"));
        Build();
    }

    public ScrollViewer Scroll => scroll;

    /// <summary>Renders again (after a change, or when the state changed elsewhere), keeping the scroll position.</summary>
    public void Refresh()
    {
        var offset = scroll.VerticalOffset;
        Build();
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(offset);
    }

    void Set(PreferencesPatch patch)
    {
        // After the control's own event: the dialog is rebuilt only then.
        if (context.Set(patch)) Application.Current.Dispatcher.BeginInvoke(Refresh);
    }

    // ---------- the pieces of .modal.settings ----------

    /// <summary>The flex column's gap (12) before <paramref name="element"/>; a hint sits 6 below (margin-top -6).</summary>
    void Add(FrameworkElement element, double gap = 12)
    {
        if (content.Children.Count > 0) element.Margin = new Thickness(element.Margin.Left, gap + element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
        content.Children.Add(element);
    }

    static TextBlock Text(string text, string style = "Text13") =>
        new() { Text = text, Style = S(style), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    static TextBlock Hint(string text)
    {
        var hint = Text(text, "Text12");
        hint.Foreground = B("mutedBrush");
        return hint;
    }

    void AddHint(string text) => Add(Hint(text), 6);

    /// <summary>.setting: the label at the left (wrapping), the control at the right, 12 apart, centred.</summary>
    static Grid Row(string label, UIElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Text(label);
        text.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(text);
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(control);
        return row;
    }

    static Button Button(string text, Action click, bool enabled = true, string? name = null)
    {
        var button = new Button { Style = S("ButtonBaseStyle"), Content = new TextBlock { Text = text, Style = S("Text13") }, IsEnabled = enabled };
        AutomationProperties.SetName(button, name ?? text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A Chromium select: as wide as its widest option (13 px Segoe UI) and its padding, border and arrow (33 px).</summary>
    ComboBox Select(string label, IReadOnlyList<(string Value, string Text)> options, string value, Action<string> changed)
    {
        var box = new ComboBox { Style = S("SettingComboBox") };
        AutomationProperties.SetName(box, label);
        var widest = 0.0;
        var dpi = VisualTreeHelper.GetDpi(Frame).PixelsPerDip;
        var face = new Typeface((FontFamily)Application.Current.FindResource("FontFamily.Ui"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (var (optionValue, text) in options)
        {
            box.Items.Add(new ComboBoxItem { Content = text, Tag = optionValue });
            var measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 13, Brushes.White, dpi);
            widest = Math.Max(widest, measured.WidthIncludingTrailingWhitespace);
        }
        box.Width = Math.Ceiling(widest) + 33;
        box.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => o.Value == value));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string chosen } && chosen != value) changed(chosen);
        };
        return box;
    }

    static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Style = S("SettingCheckBox"), Content = new TextBlock { Text = text, Style = S("Text13") }, IsChecked = value };
        AutomationProperties.SetName(box, text);
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    void Build()
    {
        content.Children.Clear();
        var state = context.State();
        var p = state.Preferences;

        Add(new TextBlock { Text = T("settings.title"), FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });

        Add(Row(T("inline.archives"), Button(T("inline.archivesChoose"), ChooseArchiveRoot)));
        var archives = Hint($"{(string.IsNullOrEmpty(p.ArchiveRoot) ? T("inline.archivesDefault") : p.ArchiveRoot)} · {T("inline.archivesHint")}");
        archives.ToolTip = p.ArchiveRoot;
        Add(archives, 6);

        // What "System" stands for, as Intl.DisplayNames names the language ("English").
        var system = CultureInfo.InstalledUICulture is { IsNeutralCulture: false, Parent: { Name.Length: > 0 } parent } ? parent : CultureInfo.InstalledUICulture;
        Add(Row(T("settings.language"), Select(T("settings.language"),
            [("system", T("settings.languageSystem", ("language", system.EnglishName))), .. Settings.LanguageNames],
            p.Language, v => Set(new PreferencesPatch { Language = v }))));

        Add(Row(T("settings.textSize"), Stepper(p.FontSize)));
        AddHint(T("settings.textSizeHint", ("bigger", "Ctrl+="), ("smaller", "Ctrl+-"), ("normal", "Ctrl+0"), ("ctrl", T("keys.ctrl"))));

        Add(Check(T("settings.snap"), p.SnapToGrid, v => Set(new PreferencesPatch { SnapToGrid = v })));
        AddHint(T("settings.snapHint"));

        Add(Row(T("settings.closeWindow"), Select(T("settings.closeWindow"),
            [("ask", T("settings.closeWindowAsk")), ("end", T("settings.closeWindowEnd")), ("keep", T("settings.closeWindowKeep"))],
            p.CloseWindow, v => Set(new PreferencesPatch { CloseWindow = v }))));
        AddHint(T("settings.closeWindowHint"));

        Add(Check(T("settings.gpu"), p.GpuAcceleration, v => Set(new PreferencesPatch { GpuAcceleration = v })));
        AddHint(p.GpuAcceleration == context.GpuAccelerated ? T("settings.gpuHint") : T("settings.gpuRestart"));

        Add(Row(T("settings.ptyHost"), Select(T("settings.ptyHost"),
            [("node", T("settings.ptyHostNode")), ("rust", T("settings.ptyHostRust"))],
            p.PtyHost, v => Set(new PreferencesPatch { PtyHost = v }))));
        AddHint(T("settings.ptyHostHint"));

        var autostart = context.Autostart;
        Add(Check(T("settings.autostart"), autostart?.Enabled ?? false, v =>
        {
            autostart?.Set(v);
            Application.Current.Dispatcher.BeginInvoke(Refresh);
        }));
        AddHint(T("settings.autostartHint"));

        var claudeButtons = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrEmpty(p.ClaudeBin))
        {
            var usePath = Button(T("settings.claudeUsePath"), () => Set(new PreferencesPatch { ClaudeBin = "" }));
            usePath.Margin = new Thickness(0, 0, 6, 0);
            claudeButtons.Children.Add(usePath);
        }
        claudeButtons.Children.Add(Button(T("settings.claudeChoose"), ChooseClaude));
        Add(Row(T("settings.claude"), claudeButtons));
        if (context.FindClaude is { } find) Add(ClaudeLine(p.ClaudeBin, find(p.ClaudeBin)), 6);
        AddHint(T("settings.claudeHint"));

        Add(Row(T("settings.defaultModel"), ModelInput(p.DefaultModel)));
        Add(Row(T("inline.defaultEffort"), Select(T("inline.defaultEffort"),
            [("", T("inline.defaultEffortCli")), .. StateValues.EffortLevels.Select(l => (l, l))],
            p.DefaultEffort ?? "", v => Set(new PreferencesPatch { DefaultEffort = v }))));
        Add(Row(T("settings.defaultPermissionMode"), Select(T("settings.defaultPermissionMode"),
            [("", T("dialog.default")), .. StateValues.PermissionModes.Select(m => (m, m))],
            p.DefaultPermissionMode ?? "", v => Set(new PreferencesPatch { DefaultPermissionMode = v }))));
        AddHint(T("settings.launchHint"));

        Add(Check(T("settings.lookInWsl"), p.LookInWsl, v => Set(new PreferencesPatch { LookInWsl = v })));
        AddHint(T("settings.lookInWslHint"));

        HiddenSection = Hidden(state.HiddenThreads);
        Add(HiddenSection);

        CloseButton = new Button { Style = S("PrimaryButton"), Content = new TextBlock { Text = T("settings.close"), Style = S("Text13") }, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(CloseButton, T("settings.close"));
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        Add(CloseButton);
        if (!context.Capture) Application.Current.Dispatcher.BeginInvoke(() => Keyboard.Focus(CloseButton), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>.setting-stepper: −, the size, +, Normal (6 apart; the buttons at least 30 wide).</summary>
    StackPanel Stepper(int size)
    {
        var stepper = new StackPanel { Orientation = Orientation.Horizontal };
        Button Step(string text, string name, bool enabled, int to)
        {
            var b = Button(text, () => Set(new PreferencesPatch { FontSize = to }), enabled, name);
            b.MinWidth = 30;
            return b;
        }
        stepper.Children.Add(Step("−", T("settings.smaller"), size > StateValues.FontSizeMin, Settings.FontSizeAfter(size, "smaller")));
        stepper.Children.Add(new TextBlock
        {
            Text = size.ToString(CultureInfo.InvariantCulture), Style = S("Text13"), MinWidth = 16, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
        });
        var bigger = Step("+", T("settings.bigger"), size < StateValues.FontSizeMax, Settings.FontSizeAfter(size, "bigger"));
        bigger.Margin = new Thickness(0, 0, 6, 0);
        stepper.Children.Add(bigger);
        stepper.Children.Add(Step(T("settings.normal"), T("settings.normal"), size != StateValues.FontSizeNormal, StateValues.FontSizeNormal));
        return stepper;
    }

    /// <summary>.setting-claude: where the claude that runs is (the chosen one, else the PATH's).</summary>
    static FrameworkElement ClaudeLine(string? chosen, string? found)
    {
        var line = new DockPanel();
        var label = string.IsNullOrEmpty(chosen)
            ? found is not null ? T("settings.claudeFound") : T("settings.claudeNotFound")
            : found is not null ? T("settings.claudeChosen") : T("settings.claudeMissing");
        var head = Hint(label);
        head.TextWrapping = TextWrapping.NoWrap;
        line.Children.Add(head);
        if ((string.IsNullOrEmpty(chosen) ? found : chosen) is { } path)
        {
            var shown = Hint(path);
            shown.TextWrapping = TextWrapping.NoWrap;
            shown.TextTrimming = TextTrimming.CharacterEllipsis;
            shown.ToolTip = path;
            shown.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(shown);
        }
        return line;
    }

    /// <summary>.setting-input: kept while typing, taken when left (Enter leaves it).</summary>
    TextBox ModelInput(string? current)
    {
        var input = new TextBox { Style = S("SettingInput"), Text = model, Tag = T("dialog.modelPlaceholder") };
        AutomationProperties.SetName(input, T("settings.defaultModel"));
        Suggestions.Attach(input);
        input.TextChanged += (_, _) => model = input.Text;
        input.LostKeyboardFocus += (_, _) =>
        {
            if (model.Trim() != (current ?? "")) Set(new PreferencesPatch { DefaultModel = model });
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Keyboard.Focus(CloseButton);
        };
        return input;
    }

    /// <summary>section.hidden-threads: the row with Restore all, the hint, then "none" or the list (6 apart, at most
    /// 180 high), each thread's title and folder · when it was hidden, and its Restore.</summary>
    FrameworkElement Hidden(IReadOnlyList<HiddenThread> threads)
    {
        var section = new StackPanel();
        AutomationProperties.SetName(section, T("settings.hidden"));
        section.Children.Add(Row(T("settings.hidden"), Button(T("settings.hiddenRestoreAll"), () => context.Restore(null), threads.Count > 0)));
        var hint = Hint(T("settings.hiddenHint"));
        hint.Margin = new Thickness(0, 6, 0, 0);
        section.Children.Add(hint);
        if (threads.Count == 0)
        {
            var none = Hint(T("settings.hiddenNone"));
            none.Margin = new Thickness(0, 6, 0, 0);
            section.Children.Add(none);
            return section;
        }
        var list = new StackPanel();
        foreach (var thread in threads)
        {
            var label = thread.Label is { Length: > 0 } l ? l : thread.ClaudeSessionId;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, Style = S("Text13"), TextTrimming = TextTrimming.CharacterEllipsis });
            var meta = string.Join(" · ", new[] { thread.Cwd, T("settings.hiddenAt", ("date", RelativeTime.Exact(thread.HiddenAt))) }.Where(s => s.Length > 0));
            text.Children.Add(new TextBlock { Text = meta, Style = S("Text12"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = thread.Cwd });
            var restore = Button(T("settings.hiddenRestore"), () => context.Restore([thread.ClaudeSessionId]), name: $"{T("settings.hiddenRestore")}: {label}");
            var row = Row("", restore);
            row.Children.RemoveAt(0);
            text.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Insert(0, text);
            if (list.Children.Count > 0) row.Margin = new Thickness(0, 6, 0, 0);
            list.Children.Add(row);
        }
        section.Children.Add(new ScrollViewer
        {
            MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Content = list, Margin = new Thickness(0, 12, 0, 0),
        });
        return section;
    }

    void ChooseArchiveRoot()
    {
        if (context.Capture) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(Window.GetWindow(Frame)) == true) Set(new PreferencesPatch { ArchiveRoot = dialog.FolderName });
    }

    void ChooseClaude()
    {
        if (context.Capture) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"{T("settings.programs")}|*.exe;*.cmd;*.bat;*.com|{T("settings.allFiles")}|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(Frame)) == true) Set(new PreferencesPatch { ClaudeBin = dialog.FileName });
    }
}
