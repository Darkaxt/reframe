using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// ChatModelDialog.tsx: the selected chats' provider and exact model (empty: the provider's default), applied to the
/// eligible ones by <see cref="Apply"/> (changeChatModels); its error is shown under the note. Shown in a
/// <see cref="ModalHost"/> (.modal, 520 px).
/// </summary>
sealed class ChatModelDialog
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    public Border Frame { get; }
    public TextBox Model { get; }
    public event Action? CloseRequested;
    /// <summary>Applies provider and model; the task's result is the error to show, or null (then the dialog closes).</summary>
    public Func<string, string, Task<string?>>? Apply { get; set; }

    readonly ComboBox provider;
    readonly TextBlock note, error;
    readonly Button cancel, submit;
    bool busy;

    static string ProviderName(string p) => p == "anthropic" ? "Anthropic" : "ChatGPT";

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

    public ChatModelDialog(int count, string initialProvider, string? initialModel)
    {
        var content = new StackPanel();
        void Add(UIElement e, double gap = 12)
        {
            if (content.Children.Count > 0 && e is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
            content.Children.Add(e);
        }
        var title = new TextBlock
        {
            Text = "Change selected chats’ model", FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Add(title);
        Add(Text($"{count} selected chats. Only inactive native chats can change."));

        provider = new ComboBox { Style = S("SettingComboBox"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in StateValues.Providers) provider.Items.Add(new ComboBoxItem { Content = ProviderName(p), Tag = p });
        provider.SelectedIndex = Math.Max(0, StateValues.Providers.ToList().IndexOf(initialProvider));
        Add(Field("Model provider", provider));

        Model = new TextBox { Style = S("SearchBox"), Tag = "Provider default", Text = initialModel ?? "", MaxLength = 300 };
        SpellCheck.SetIsEnabled(Model, false);
        Add(Field("Model", Model));

        // .provider-note: 13 px, muted, line-height 1.45; its margin (0 0 14px) adds to the gap below it.
        note = Text("");
        note.Foreground = B("mutedBrush");
        note.LineHeight = 13 * 1.45;
        note.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        note.Margin = new Thickness(0, 0, 0, 14);
        Add(note);
        provider.SelectionChanged += (_, _) =>
        {
            Model.Text = "";
            ShowNote();
        };
        ShowNote();

        error = Text("");
        error.Foreground = B("dangerBrush");
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        Add(error);

        cancel = new Button { Style = S("ButtonBaseStyle"), Content = Text("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        submit = new Button { Style = S("PrimaryButton"), Content = Text("Apply to selected chats"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(submit, "Apply to selected chats");
        submit.Click += async (_, _) => await SubmitAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(submit);
        Add(buttons);

        var (frame, _) = ModalHost.Frame(520, content);
        frame.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !busy && Keyboard.FocusedElement is not ComboBox)
            {
                e.Handled = true;
                await SubmitAsync();
            }
        };
        AutomationProperties.SetName(frame, "Change selected chats’ model");
        Frame = frame;
    }

    string Provider => (provider.SelectedItem as ComboBoxItem)?.Tag as string ?? "chatgpt";

    void ShowNote() => note.Text =
        $"Applies {ProviderName(Provider)} and this exact model ID to the selected eligible chats, including chats currently using another provider. Leave Model empty to use that provider’s default. Existing conversations are preserved.";

    void Close()
    {
        if (!busy) CloseRequested?.Invoke();
    }

    async Task SubmitAsync()
    {
        if (busy || Apply is null) return;
        var model = Model.Text.Trim();
        if (!ChatHistory.ValidModel(model))
        {
            ShowError("Invalid selected chats/provider/model.");
            return;
        }
        busy = true;
        ShowError("");
        foreach (var control in new Control[] { provider, Model, cancel, submit }) control.IsEnabled = false;
        ((TextBlock)submit.Content).Text = "Applying…";
        string? failure;
        try
        {
            failure = await Apply(Provider, model);
        }
        finally
        {
            busy = false;
            foreach (var control in new Control[] { provider, Model, cancel, submit }) control.IsEnabled = true;
            ((TextBlock)submit.Content).Text = "Apply to selected chats";
        }
        if (failure is null) CloseRequested?.Invoke();
        else ShowError(failure);
    }

    void ShowError(string text)
    {
        error.Text = text;
        error.Visibility = text == "" ? Visibility.Collapsed : Visibility.Visible;
    }
}
