using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Where the dialog starts from (NewSessionPreset): a project's "+" gives its folder, provider and locations.</summary>
sealed record NewSessionPreset(string Cwd, string Provider, IReadOnlyList<FolderLocation>? Locations = null);

/// <summary>
/// NewSessionDialog.tsx: provider (with its note), effort, folder (Browse…, the recent folders as chips), name, first
/// message, worktree and, folded under Advanced, model, permission mode and additional folders. Each provider starts
/// with what was used for it last (defaultsFor); an effort picked here stays across providers. Start (or Ctrl+Enter in
/// the first message) asks <see cref="Create"/>; its error is shown above the buttons. Shown in a
/// <see cref="ModalHost"/> (.modal, 520 px).
/// </summary>
sealed class NewSessionDialog
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);
    static string T(string key) => Strings.T(key);

    public Border Frame { get; }
    public TextBox Folder { get; }
    public ComboBox ProviderBox => providerBox;
    public event Action? CloseRequested;
    /// <summary>Creates the session; the task's result is the error (and the WSL distribution without claude), or none.</summary>
    public Func<NewSessionRequest, Task<(string? Error, string? Distro)>>? Create { get; set; }

    readonly ComboBox providerBox, effortBox, permissionBox;
    readonly TextBox nameBox, promptBox, worktreeNameBox, modelBox, addDirsBox;
    readonly CheckBox worktreeBox;
    readonly FrameworkElement worktreeField, advancedBody;
    readonly TextBlock note, error;
    readonly WrapPanel recentPanel;
    readonly Button start, cancel;
    readonly Preferences preferences;
    readonly IReadOnlyDictionary<string, string> roots;
    readonly IReadOnlyList<string> recent;
    readonly NewSessionPreset? preset;
    string provider;
    bool effortPicked, busy, settingEffort;

    static TextBlock Text(string text, string style = "Text13") => new() { Text = text, Style = S(style), TextWrapping = TextWrapping.Wrap };

    /// <summary>.field: its label (12 px, muted) 4 px above the control.</summary>
    static StackPanel Field(string label, FrameworkElement control)
    {
        var field = new StackPanel();
        var caption = Text(label, "Text12");
        caption.Foreground = B("mutedBrush");
        caption.Margin = new Thickness(0, 0, 0, 4);
        field.Children.Add(caption);
        field.Children.Add(control);
        AutomationProperties.SetName(control, label);
        return field;
    }

    static TextBox Input(string placeholder = "")
    {
        var box = new TextBox { Style = S("FormInput"), Tag = placeholder };
        SpellCheck.SetIsEnabled(box, false);
        return box;
    }

    static TextBox Area(string placeholder, int rows)
    {
        var box = Input(placeholder);
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        // rows × the line (17 px at 13 px), the padding and the border.
        box.Height = rows * 17 + 14;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        return box;
    }

    static ComboBox Select(IEnumerable<(string Value, string Label)> options)
    {
        var box = new ComboBox { Style = S("SettingComboBox"), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30 };
        foreach (var (value, label) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedIndex = 0;
        return box;
    }

    static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    static void Pick(ComboBox box, string? value)
    {
        var index = box.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == (value ?? ""));
        box.SelectedIndex = Math.Max(0, index);
    }

    public NewSessionDialog(Preferences preferences, IReadOnlyDictionary<string, string> roots, IReadOnlyList<string> recent, NewSessionPreset? preset)
    {
        this.preferences = preferences;
        this.roots = roots;
        this.recent = recent;
        this.preset = preset;
        provider = preset?.Provider ?? "chatgpt";

        var content = new StackPanel();
        void Add(UIElement e, double gap = 12)
        {
            if (content.Children.Count > 0 && e is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
            content.Children.Add(e);
        }
        Add(new TextBlock { Text = T("dialog.title"), FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });

        providerBox = Select(StateValues.Providers.Select(p => (p, NewSession.ProviderName(p))));
        Pick(providerBox, provider);
        Add(Field(T("dialog.provider"), providerBox));
        // .provider-note: 13 px, muted, line-height 1.45; its margin (0 0 14px) adds to the gap below it.
        note = Text("");
        note.Foreground = B("mutedBrush");
        note.LineHeight = 13 * 1.45;
        note.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        note.Margin = new Thickness(0, 0, 0, 14);
        Add(note);

        effortBox = Select(new[] { ("", T("dialog.effortDefault")) }.Concat(NewSession.EffortLevels.Select(l => (l, l))));
        effortBox.SelectionChanged += (_, _) =>
        {
            if (!settingEffort) effortPicked = true;
        };
        Add(Field(T("dialog.effort"), effortBox));

        Folder = Input(T("dialog.folderPlaceholder"));
        Folder.Text = preset?.Cwd ?? "";
        Folder.TextChanged += (_, _) =>
        {
            ShowError(null, null);
            start!.IsEnabled = Folder.Text.Trim() != "" && !busy;
            ShowRecent();
        };
        var browse = new Button { Style = S("ButtonBaseStyle"), Content = Text(T("dialog.browse")), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(browse, T("dialog.browse"));
        browse.Click += (_, _) => Browse();
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(Folder);
        Add(Field(T("dialog.folder"), row));
        AutomationProperties.SetName(Folder, T("dialog.folder"));
        // .recent: chips 6 px apart, 4 px closer to the folder than the gap.
        recentPanel = new WrapPanel { Margin = new Thickness(0, -4, 0, 0) };
        AutomationProperties.SetName(recentPanel, T("dialog.recent"));
        Add(recentPanel);

        nameBox = Input(T("dialog.namePlaceholder"));
        Add(Field(T("dialog.name"), nameBox));

        promptBox = Area(T("dialog.promptPlaceholder"), 3);
        promptBox.MaxLength = NewSession.MaxPrompt;
        promptBox.PreviewKeyDown += async (_, e) =>
        {
            // Ctrl+Enter starts, as the Start button; Enter alone is a new line.
            if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            e.Handled = true;
            await SubmitAsync();
        };
        Add(Field(T("dialog.prompt"), promptBox));

        worktreeBox = new CheckBox { Style = S("SettingCheckBox"), Content = Text(T("dialog.worktree")), Margin = new Thickness(-4, 0, 0, 0) };
        AutomationProperties.SetName(worktreeBox, T("dialog.worktree"));
        Add(worktreeBox);
        worktreeNameBox = Input();
        worktreeField = Field(T("dialog.worktreeName"), worktreeNameBox);
        worktreeField.Margin = new Thickness(24, 0, 0, 0);
        worktreeField.Visibility = Visibility.Collapsed;
        Add(worktreeField);
        worktreeBox.Checked += (_, _) => worktreeField.Visibility = Visibility.Visible;
        worktreeBox.Unchecked += (_, _) => worktreeField.Visibility = Visibility.Collapsed;

        // details.advanced: the summary (12 px, muted, the disclosure triangle), folded.
        modelBox = Input(T("dialog.modelPlaceholder"));
        Suggestions.Attach(modelBox);
        permissionBox = Select(new[] { ("", T("dialog.default")) }.Concat(NewSession.PermissionModes.Select(m => (m, m))));
        addDirsBox = Area(T("dialog.addDirsPlaceholder"), 2);
        var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(Field(T("dialog.model"), modelBox));
        var permission = Field(T("dialog.permissionMode"), permissionBox);
        permission.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(permission);
        var dirs = Field(T("dialog.addDirs"), addDirsBox);
        dirs.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(dirs);
        advancedBody = body;
        var triangle = new Path { Data = Geometry.Parse("M0,0 L7,4 L0,8 Z"), Fill = B("mutedBrush"), Width = 7, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
        var summaryContent = new StackPanel { Orientation = Orientation.Horizontal };
        summaryContent.Children.Add(triangle);
        var summaryText = Text(T("dialog.advanced"), "Text12");
        summaryText.Foreground = B("mutedBrush");
        summaryContent.Children.Add(summaryText);
        var summary = new ToggleButton { Content = summaryContent, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, FocusVisualStyle = null };
        summary.Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        AutomationProperties.SetName(summary, T("dialog.advanced"));
        summary.Checked += (_, _) => { body.Visibility = Visibility.Visible; triangle.LayoutTransform = new RotateTransform(90); };
        summary.Unchecked += (_, _) => { body.Visibility = Visibility.Collapsed; triangle.LayoutTransform = null; };
        var advanced = new StackPanel();
        advanced.Children.Add(summary);
        advanced.Children.Add(body);
        Add(advanced);

        error = Text("");
        error.Foreground = B("dangerBrush");
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        Add(error);

        cancel = new Button { Style = S("ButtonBaseStyle"), Content = Text(T("dialog.cancel")), IsCancel = true };
        AutomationProperties.SetName(cancel, T("dialog.cancel"));
        cancel.Click += (_, _) => CloseRequested?.Invoke();
        start = new Button { Style = S("PrimaryButton"), Content = Text(T("dialog.start")), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(start, T("dialog.start"));
        start.Click += async (_, _) => await SubmitAsync();
        start.IsEnabled = Folder.Text.Trim() != "";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        Add(buttons);

        providerBox.SelectionChanged += (_, _) => PickProvider(Selected(providerBox));
        ApplyDefaults();
        ShowNote();
        ShowRecent();

        var (frame, _) = ModalHost.Frame(520, content);
        AutomationProperties.SetName(frame, T("dialog.title"));
        Frame = frame;
    }

    void ShowNote() => note.Text = T(provider == "anthropic" ? "dialog.providerNote.anthropic" : "dialog.providerNote.chatgpt");

    /// <summary>The remembered choices of the provider (an effort picked here stays).</summary>
    void ApplyDefaults()
    {
        var remembered = NewSession.DefaultsFor(preferences, provider);
        modelBox.Text = remembered.Model ?? "";
        if (!effortPicked)
        {
            settingEffort = true;
            Pick(effortBox, remembered.Effort);
            settingEffort = false;
        }
        Pick(permissionBox, remembered.PermissionMode);
        addDirsBox.Text = string.Join("\n", remembered.AddDirs);
    }

    void PickProvider(string next)
    {
        if (next == provider || next == "") return;
        var previous = provider;
        provider = next;
        // The provider's own location of this workspace; else the folder moved from one workspace root to the other.
        var location = preset?.Locations?.FirstOrDefault(l => l.Provider == next)?.Cwd;
        Folder.Text = location is { Length: > 0 } ? location
            : Workspaces.RebaseFolder(Folder.Text, roots.GetValueOrDefault(previous) ?? "", roots.GetValueOrDefault(next) ?? "");
        ApplyDefaults();
        ShowNote();
        ShowRecent();
    }

    /// <summary>The recent folders outside the other provider's workspace root, as chips (the current one selected).</summary>
    void ShowRecent()
    {
        recentPanel.Children.Clear();
        foreach (var folder in NewSession.ProviderRecent(recent, roots, provider))
        {
            var selected = folder == Folder.Text;
            var chip = new Button
            {
                Content = new TextBlock { Text = NewSession.FolderName(folder) is { Length: > 0 } n ? n : folder, Style = S("Text12"), Foreground = B(selected ? "textBrush" : "mutedBrush") },
                ToolTip = folder, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 6), FocusVisualStyle = null,
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 2, 10, 2));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
            border.SetValue(Border.BackgroundProperty, B("bgBrush"));
            border.SetValue(Border.BorderBrushProperty, B(selected ? "accentBrush" : "borderBrush"));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            chip.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            AutomationProperties.SetName(chip, folder);
            chip.Click += (_, _) => Folder.Text = folder;
            recentPanel.Children.Add(chip);
        }
        recentPanel.Visibility = recentPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (System.IO.Directory.Exists(Folder.Text.Trim())) dialog.InitialDirectory = Folder.Text.Trim();
        if (dialog.ShowDialog(Window.GetWindow(Frame)) == true) Folder.Text = dialog.FolderName;
    }

    /// <summary>The request as createSession takes it (the fields trimmed there).</summary>
    public NewSessionRequest Request() => new(
        Folder.Text.Trim(), provider, nameBox.Text.Trim() is { Length: > 0 } name ? name : null, worktreeBox.IsChecked == true,
        worktreeBox.IsChecked == true && worktreeNameBox.Text.Trim() is { Length: > 0 } w ? w : null,
        modelBox.Text.Trim() is { Length: > 0 } model ? model : null,
        // null: nothing chosen yet (Settings' default); "": the CLI's own.
        Selected(effortBox), Selected(permissionBox) is { Length: > 0 } mode ? mode : null,
        NewSession.ParseAddDirs(addDirsBox.Text), promptBox.Text.Trim() is { Length: > 0 } prompt ? prompt : null);

    public async Task SubmitAsync()
    {
        if (busy || Create is null || Folder.Text.Trim() == "") return;
        busy = true;
        start.IsEnabled = false;
        (string? Error, string? Distro) result;
        try
        {
            result = await Create(Request());
        }
        finally
        {
            busy = false;
            start.IsEnabled = Folder.Text.Trim() != "";
        }
        if (result.Error is null) CloseRequested?.Invoke();
        else ShowError(result.Error, result.Distro);
    }

    /// <summary>.form-error: the WSL distribution without claude, else dialog.error.*.</summary>
    void ShowError(string? code, string? distro)
    {
        if (code is null)
        {
            error.Visibility = Visibility.Collapsed;
            return;
        }
        var text = distro is not null ? Strings.T("error.claudeNotFoundInWsl", ("distro", distro)) : T($"dialog.error.{code}");
        if (code == "claudeNotFound" && distro is null) text += " " + T("error.claudeOnlyInWsl");
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
