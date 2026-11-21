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
    /// <summary>Removes takeover exclusions (null: all of them).</summary>
    public required Action<IReadOnlyCollection<string>?> Unexclude { get; init; }
    /// <summary>The shell's own login entry; null when it must not be touched (a capture).</summary>
    public Autostart? Autostart { get; init; }
    /// <summary>Whether GPU acceleration was on when the shell started (the hint says a change needs a restart).</summary>
    public bool GpuAccelerated { get; init; }
    /// <summary>Where the claude sessions run is (findClaude); null: not shown (a capture, like Electron's reference).</summary>
    public Func<string?, string?>? FindClaude { get; init; }
    public bool Capture { get; init; }
    /// <summary>The CLIs the launch config pinned for each provider (a provider, its binary, whether the adoption record verified it).</summary>
    public IReadOnlyList<(string Provider, string Path, bool Adopted)> ManagedClients { get; init; } = [];
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
    readonly StackPanel nav = new() { Width = 140, Margin = new Thickness(0, 0, 22, 0) };
    readonly ScrollViewer scroll;
    string tab = "general";
    string? editing;
    static readonly (string Key, string Label)[] TabList =
    [
        ("general", "inline.tab.general"), ("claude", "inline.tab.claude"), ("profiles", "inline.tab.profiles"),
        ("advanced", "inline.tab.advanced"), ("threads", "inline.tab.threads"),
    ];
    public Border Frame { get; }
    public Button CloseButton { get; private set; } = null!;
    public FrameworkElement HiddenSection { get; private set; } = null!;
    public event Action? CloseRequested;
    // The default model being typed, per provider (kept across a redraw of the dialog).
    readonly Dictionary<string, string> models = [];

    public SettingsDialog(SettingsContext context)
    {
        this.context = context;
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(content, 1);
        layout.Children.Add(nav);
        layout.Children.Add(content);
        (Frame, scroll) = ModalHost.Frame(740, layout);
        AutomationProperties.SetName(Frame, T("settings.title"));
        Build();
    }

    public ScrollViewer Scroll => scroll;
    public IReadOnlyList<string> Tabs => [.. TabList.Select(t => t.Key)];

    public void ShowTab(string key)
    {
        tab = key;
        editing = null;
        Build();
        scroll.ScrollToVerticalOffset(0);
    }

    Button NavItem(string key, string label)
    {
        var selected = tab == key;
        var button = new Button { Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 2) };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
        border.SetValue(Border.BackgroundProperty, selected ? B("selectedBrush") : Brushes.Transparent);
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.Content = new TextBlock { Text = label, Style = S("Text13"), Foreground = B(selected ? "textBrush" : "mutedBrush") };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => ShowTab(key);
        return button;
    }

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
        nav.Children.Clear();
        var state = context.State();
        var p = state.Preferences;

        nav.Children.Add(new TextBlock { Text = T("settings.title"), FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 12) });
        foreach (var (key, label) in TabList) nav.Children.Add(NavItem(key, T(label)));
        Add(new TextBlock { Text = T(TabList.First(t => t.Key == tab).Label), FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });

        switch (tab)
        {
            case "claude": BuildClaude(p); break;
            case "profiles": BuildProfiles(p); break;
            case "advanced": BuildAdvanced(p); break;
            case "threads": BuildThreads(state); break;
            default: BuildGeneral(p); break;
        }

        CloseButton = new Button { Style = S("PrimaryButton"), Content = new TextBlock { Text = T("settings.close"), Style = S("Text13") }, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(CloseButton, T("settings.close"));
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        Add(CloseButton, 20);
        if (!context.Capture) Application.Current.Dispatcher.BeginInvoke(() => Keyboard.Focus(CloseButton), System.Windows.Threading.DispatcherPriority.Input);
    }

    void BuildGeneral(Preferences p)
    {
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

        Add(Row(T("settings.closeWindow"), Select(T("settings.closeWindow"),
            [("ask", T("settings.closeWindowAsk")), ("end", T("settings.closeWindowEnd")), ("keep", T("settings.closeWindowKeep"))],
            p.CloseWindow, v => Set(new PreferencesPatch { CloseWindow = v }))));
        AddHint(T("settings.closeWindowHint"));

        Add(Row(T("settings.closeEnded"), Select(T("settings.closeEnded"),
            [.. PreferencesPatch.CloseEndedChoices.Select(h => (h.ToString(), h == 0 ? T("settings.closeEndedNever") : T("settings.closeEndedHours", ("hours", h.ToString()))))],
            p.CloseEndedAfterHours.ToString(), v => Set(new PreferencesPatch { CloseEndedAfterHours = int.Parse(v) }))));
        AddHint(T("settings.closeEndedHint"));

        var autostart = context.Autostart;
        Add(Check(T("settings.autostart"), autostart?.Enabled ?? false, v =>
        {
            autostart?.Set(v);
            Application.Current.Dispatcher.BeginInvoke(Refresh);
        }));
        AddHint(T("settings.autostartHint"));

        Add(Check(T("settings.autoUpdate"), p.CheckForUpdates, v => Set(new PreferencesPatch { CheckForUpdates = v })));
        AddHint(T("settings.autoUpdateHint"));

        Add(Check(T("settings.wakeForTasks"), p.WakeForTasks, v => Set(new PreferencesPatch { WakeForTasks = v })));
        AddHint(T("settings.wakeForTasksHint"));

        Add(Check(T("inline.notifySubthreads"), p.NotifySubthreads, v => Set(new PreferencesPatch { NotifySubthreads = v })));
        AddHint(T("inline.notifySubthreadsHint"));
    }

    void BuildClaude(Preferences p)
    {
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

        Add(new TextBlock { Text = T("inline.managed.title"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        if (context.ManagedClients.Count == 0) AddHint(T("inline.managed.none"));
        foreach (var (provider, path, adopted) in context.ManagedClients)
        {
            var version = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
            var how = !System.IO.File.Exists(path) ? T("inline.managed.missing") : adopted ? T("inline.managed.adopted") : T("inline.managed.launchConfig");
            var line = Hint($"{NewSession.ProviderName(provider)} · {version} · {how}\n{path}");
            line.ToolTip = path;
            Add(line, 6);
        }
        if (context.ManagedClients.Count > 0) AddHint(T("inline.managed.hint"));

        Add(new TextBlock { Text = T("inline.browser.title"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        var browserChoices = new (string, string)[] { ("", T("inline.browser.cli")), ("on", T("inline.browser.on")), ("off", T("inline.browser.off")) };
        Add(Row(T("inline.browser.anthropic"), Select(T("inline.browser.anthropic"), browserChoices,
            Settings.BrowserChoice(p.BrowserAnthropic), v => Set(new PreferencesPatch { BrowserAnthropic = v }))));
        Add(Row(T("inline.browser.chatgpt"), Select(T("inline.browser.chatgpt"), browserChoices,
            Settings.BrowserChoice(p.BrowserChatgpt), v => Set(new PreferencesPatch { BrowserChatgpt = v }))));
        AddHint(T("inline.browser.hint"));

        foreach (var provider in new[] { "anthropic", "chatgpt", "gemini" })
        {
            var name = NewSession.ProviderName(provider);
            var d = NewSession.DefaultsFor(p, provider);
            Add(new TextBlock { Text = T("settings.defaultsFor", ("provider", name)), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            Add(Row(T("settings.defaultModel"), ModelInput(provider, d.Model)));
            Add(Row(T("inline.defaultEffort"), Select($"{T("inline.defaultEffort")} – {name}",
                [("", T("inline.defaultEffortCli")), .. StateValues.EffortLevels.Select(l => (l, l))],
                d.Effort ?? "", v => Set(new PreferencesPatch { DefaultsProvider = provider, DefaultEffort = v }))));
            Add(Row(T("settings.defaultPermissionMode"), Select($"{T("settings.defaultPermissionMode")} – {name}",
                [("", T("dialog.default")), .. StateValues.PermissionModes.Select(m => (m, StateValues.PermissionLabel(m)))],
                d.PermissionMode ?? "", v => Set(new PreferencesPatch { DefaultsProvider = provider, DefaultPermissionMode = v }))));
        }
        AddHint(T("settings.launchHint"));

        Add(Check(T("settings.lookInWsl"), p.LookInWsl, v => Set(new PreferencesPatch { LookInWsl = v })));
        AddHint(T("settings.lookInWslHint"));
    }

    void BuildAdvanced(Preferences p)
    {
        Add(Check(T("settings.snap"), p.SnapToGrid, v => Set(new PreferencesPatch { SnapToGrid = v })));
        AddHint(T("settings.snapHint"));

        Add(Check(T("settings.gpu"), p.GpuAcceleration, v => Set(new PreferencesPatch { GpuAcceleration = v })));
        AddHint(p.GpuAcceleration == context.GpuAccelerated ? T("settings.gpuHint") : T("settings.gpuRestart"));

        Add(Row(T("settings.ptyHost"), Select(T("settings.ptyHost"),
            [("node", T("settings.ptyHostNode")), ("rust", T("settings.ptyHostRust"))],
            p.PtyHost, v => Set(new PreferencesPatch { PtyHost = v }))));
        AddHint(T("settings.ptyHostHint"));
    }

    void BuildThreads(AppState state)
    {
        HiddenSection = Hidden(state.HiddenThreads);
        Add(HiddenSection);
        Add(Excluded(state.TakeoverExclusions ?? []), 20);
    }

    // ---------- profiles ----------

    void BuildProfiles(Preferences p)
    {
        Add(Hint(T("inline.profiles.hint")), 6);
        foreach (var provider in new[] { "anthropic", "chatgpt", "gemini" })
        {
            var head = Row(NewSession.ProviderName(provider), Button(T("inline.profiles.new"), () => AddProfile(provider)));
            ((TextBlock)head.Children[0]).FontWeight = FontWeights.SemiBold;
            Add(head, 20);
            var profiles = NewSession.ProfilesFor(p, provider);
            if (profiles.Count == 0) Add(Hint(T("inline.profiles.none")), 6);
            foreach (var profile in profiles) Add(ProfileCard(p, profile), 8);
        }
    }

    List<LaunchProfile> CurrentProfiles() => (context.State().Preferences.Profiles ?? []).Select(x => x.Clone()).ToList();

    void Update(string id, Action<LaunchProfile> change)
    {
        var list = CurrentProfiles();
        if (list.FirstOrDefault(x => x.Id == id) is not { } target) return;
        change(target);
        Set(new PreferencesPatch { Profiles = list });
    }

    void AddProfile(string provider)
    {
        var defaults = NewSession.DefaultsFor(context.State().Preferences, provider);
        var profile = new LaunchProfile
        {
            Id = Guid.NewGuid().ToString("N")[..12], Name = T("inline.profiles.newName"), Provider = provider,
            Model = defaults.Model, Effort = defaults.Effort, PermissionMode = defaults.PermissionMode, AddDirs = defaults.AddDirs.Count > 0 ? [.. defaults.AddDirs] : null,
        };
        var list = CurrentProfiles();
        list.Add(profile);
        editing = profile.Id;
        Set(new PreferencesPatch { Profiles = list });
    }

    void Duplicate(LaunchProfile profile)
    {
        var copy = profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..12];
        copy.Name = T("inline.profiles.copy", ("name", profile.Name));
        var list = CurrentProfiles();
        list.Insert(list.FindIndex(x => x.Id == profile.Id) + 1, copy);
        Set(new PreferencesPatch { Profiles = list });
    }

    void Delete(LaunchProfile profile)
    {
        var list = CurrentProfiles();
        list.RemoveAll(x => x.Id == profile.Id);
        if (editing == profile.Id) editing = null;
        // An emptied list is sent as one the cleaner turns into "none".
        Set(new PreferencesPatch { Profiles = list });
        if (list.Count == 0) Application.Current.Dispatcher.BeginInvoke(Refresh);
    }

    static string Summary(LaunchProfile profile)
    {
        var parts = new List<string>
        {
            profile.Model is { Length: > 0 } m ? ModelNames.Label(m) : T("dialog.default"),
            profile.Effort ?? T("dialog.effortDefault"),
            profile.PermissionMode is { } mode ? StateValues.PermissionLabel(mode) : T("dialog.default"),
        };
        if (profile.AddDirs is { Count: > 0 } dirs) parts.Add($"+{dirs.Count} folder{(dirs.Count == 1 ? "" : "s")}");
        return string.Join(" · ", parts);
    }

    FrameworkElement ProfileCard(Preferences p, LaunchProfile profile)
    {
        var d = NewSession.DefaultsFor(p, profile.Provider);
        var isDefault = d.Model == profile.Model && d.Effort == profile.Effort && d.PermissionMode == profile.PermissionMode
            && string.Join(';', d.AddDirs) == string.Join(';', profile.AddDirs ?? []);
        var card = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = profile.Name, Style = S("Text13"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (isDefault)
            title.Children.Add(new Border
            {
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1), BorderBrush = B("accentBrush"),
                VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = T("inline.profiles.default"), Style = S("Text12"), Foreground = B("accentBrush") },
            });
        card.Children.Add(title);
        var summary = Hint(Summary(profile));
        summary.Margin = new Thickness(0, 2, 0, 8);
        card.Children.Add(summary);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        void AddButton(string text, Action click, bool enabled = true)
        {
            var button = Button(text, click, enabled, $"{text}: {profile.Name}");
            button.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(button);
        }
        var open = editing == profile.Id;
        AddButton(open ? T("inline.profiles.done") : T("inline.profiles.edit"), () =>
        {
            editing = open ? null : profile.Id;
            Refresh();
        });
        AddButton(T("inline.profiles.makeDefault"), () => Set(new PreferencesPatch { MakeDefaultProfile = profile.Id }), !isDefault);
        AddButton(T("inline.profiles.duplicate"), () => Duplicate(profile));
        AddButton(T("inline.profiles.delete"), () => Delete(profile));
        card.Children.Add(buttons);

        if (open) card.Children.Add(ProfileEditor(profile));
        return new Border
        {
            Background = B("bgBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = card,
        };
    }

    FrameworkElement ProfileEditor(LaunchProfile profile)
    {
        var editor = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        TextBox Field(string label, string text, Action<string> commit, bool area = false)
        {
            var caption = Hint(label);
            caption.Margin = new Thickness(0, editor.Children.Count > 0 ? 10 : 0, 0, 4);
            editor.Children.Add(caption);
            var box = new TextBox { Style = S("FormInput"), Text = text };
            if (area)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.Height = 2 * 17 + 14;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            SpellCheck.SetIsEnabled(box, false);
            AutomationProperties.SetName(box, label);
            var shown = text;
            box.LostKeyboardFocus += (_, _) =>
            {
                if (box.Text != shown) commit(box.Text);
            };
            if (!area)
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    e.Handled = true;
                    Keyboard.Focus(CloseButton);
                };
            editor.Children.Add(box);
            return box;
        }
        Field(T("inline.profiles.name"), profile.Name, v =>
        {
            if (v.Trim().Length > 0) Update(profile.Id, x => x.Name = v);
        });
        var model = Field(T("settings.defaultModel"), profile.Model ?? "", v => Update(profile.Id, x => x.Model = v.Trim().Length > 0 ? v.Trim() : null));
        Suggestions.Attach(model);
        var effort = Row(T("inline.defaultEffort"), Select(T("inline.defaultEffort"),
            [("", T("inline.defaultEffortCli")), .. StateValues.EffortLevels.Select(l => (l, l))],
            profile.Effort ?? "", v => Update(profile.Id, x => x.Effort = v.Length > 0 ? v : null)));
        effort.Margin = new Thickness(0, 10, 0, 0);
        editor.Children.Add(effort);
        var permission = Row(T("settings.defaultPermissionMode"), Select(T("settings.defaultPermissionMode"),
            [("", T("dialog.default")), .. StateValues.PermissionModes.Select(m => (m, StateValues.PermissionLabel(m)))],
            profile.PermissionMode ?? "", v => Update(profile.Id, x => x.PermissionMode = v.Length > 0 ? v : null)));
        permission.Margin = new Thickness(0, 10, 0, 0);
        editor.Children.Add(permission);
        Field(T("inline.profiles.addDirs"), string.Join("\n", profile.AddDirs ?? []),
            v => Update(profile.Id, x => x.AddDirs = NewSession.ParseAddDirs(v)), area: true);
        return editor;
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
    TextBox ModelInput(string provider, string? current)
    {
        var input = new TextBox { Style = S("SettingInput"), Text = models.GetValueOrDefault(provider) ?? current ?? "", Tag = T("dialog.modelPlaceholder") };
        AutomationProperties.SetName(input, $"{T("settings.defaultModel")} – {NewSession.ProviderName(provider)}");
        Suggestions.Attach(input);
        input.TextChanged += (_, _) => models[provider] = input.Text;
        input.LostKeyboardFocus += (_, _) =>
        {
            if (input.Text.Trim() != (current ?? "")) Set(new PreferencesPatch { DefaultsProvider = provider, DefaultModel = input.Text });
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
    FrameworkElement Hidden(IReadOnlyList<HiddenThread> threads) =>
        ThreadSection(T("settings.hidden"), T("settings.hiddenRestoreAll"), T("settings.hiddenHint"), T("settings.hiddenNone"), T("settings.hiddenRestore"),
            [.. threads.Select(t => (t.ClaudeSessionId, t.Label, t.Cwd, T("settings.hiddenAt", ("date", RelativeTime.Exact(t.HiddenAt)))))],
            context.Restore);

    FrameworkElement ThreadSection(string title, string allText, string hintText, string noneText, string oneText,
        IReadOnlyList<(string Id, string Label, string Cwd, string When)> threads, Action<IReadOnlyCollection<string>?> act)
    {
        var section = new StackPanel();
        AutomationProperties.SetName(section, title);
        section.Children.Add(Row(title, Button(allText, () => act(null), threads.Count > 0)));
        var hint = Hint(hintText);
        hint.Margin = new Thickness(0, 6, 0, 0);
        section.Children.Add(hint);
        if (threads.Count == 0)
        {
            var none = Hint(noneText);
            none.Margin = new Thickness(0, 6, 0, 0);
            section.Children.Add(none);
            return section;
        }
        var list = new StackPanel();
        foreach (var thread in threads)
        {
            var label = thread.Label is { Length: > 0 } l ? l : thread.Id;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, Style = S("Text13"), TextTrimming = TextTrimming.CharacterEllipsis });
            var meta = string.Join(" · ", new[] { thread.Cwd, thread.When }.Where(s => s.Length > 0));
            text.Children.Add(new TextBlock { Text = meta, Style = S("Text12"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = thread.Cwd });
            var restore = Button(oneText, () => act([thread.Id]), name: $"{oneText}: {label}");
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

    /// <summary>The takeover exclusions, laid out like the hidden threads: Remove all, the hint, then each with Remove.</summary>
    FrameworkElement Excluded(IReadOnlyList<TakeoverExclusion> excluded) =>
        ThreadSection(T("settings.excluded"), T("settings.excludedRemoveAll"), T("settings.excludedHint"), T("settings.excludedNone"), T("settings.excludedRemove"),
            [.. excluded.Select(x => (x.ClaudeSessionId, x.Label, x.Cwd, T("settings.excludedAt", ("date", RelativeTime.Exact(x.ExcludedAt)))))],
            context.Unexclude);

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
