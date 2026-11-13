using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// SessionSidebar.tsx: the search field and the PINNED, PROJECTS and RECENTS sections, drawn as styles.css draws them
/// (.sidebar, .sidebar-row, .folder-workspace, …). Each section's rows are a ListBox, so keyboard and UI automation see
/// list items named after the chats. A click selects a row and asks to open it (<see cref="Open"/>), a right click asks for its menu (<see cref="RowMenu"/>); a
/// project opens or closes locally (<see cref="ProjectToggled"/>); the per-project + is inert.
/// </summary>
sealed class SessionSidebar : Border
{
    public TextBox Search { get; }
    public ScrollViewer Scroll { get; }
    readonly StackPanel content = new();
    readonly Dictionary<string, Border> rows = [];
    string? selected;

    public event Action<ChatRow>? Open;
    /// <summary>The row's context menu is asked for (right click, the menu key or Shift+F10): the chat and its item.</summary>
    public event Action<ChatRow, FrameworkElement>? RowMenu;
    public event Action<string>? ProjectToggled;
    public event Action<string>? QueryChanged;

    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    public SessionSidebar()
    {
        Width = 270;
        Background = B("surfaceBrush");
        BorderBrush = B("borderBrush");
        BorderThickness = new Thickness(0, 0, 1, 0);
        AutomationProperties.SetName(this, "Session list");

        Search = new TextBox { Style = S("SearchBox"), Tag = "Search sessions", Margin = new Thickness(8) };
        AutomationProperties.SetName(Search, "Search sessions");
        Search.TextChanged += (_, _) => QueryChanged?.Invoke(Search.Text);
        CommandBindings.Add(new CommandBinding(SearchCommands.Clear, (_, _) => Search.Clear()));

        Scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = content,
        };
        content.Margin = new Thickness(6, 0, 6, 8);
        var panel = new DockPanel();
        DockPanel.SetDock(Search, Dock.Top);
        panel.Children.Add(Search);
        panel.Children.Add(Scroll);
        Child = panel;
    }

    /// <summary>The chat row (its tag) of a visible row by label.</summary>
    public ChatRow? ChatOf(string label) => RowOf(label)?.Tag as ChatRow;

    /// <summary>The row of a chat in a section ("pinned", "recents", "project:&lt;id&gt;"), or its first one.</summary>
    public FrameworkElement? RowOf(string label) =>
        rows.Values.FirstOrDefault(r => r.Tag is ChatRow row && row.Label == label);

    public void Show(SidebarSections sections)
    {
        rows.Clear();
        content.Children.Clear();
        Heading("Pinned");
        if (sections.Pinned.Count > 0) content.Children.Add(List("Pinned", sections.Pinned));
        else Empty("Pin chats from their context menu.");
        Heading("Projects");
        if (sections.Projects.Count == 0) Empty("No projects yet.");
        var projects = new StackPanel();
        AutomationProperties.SetName(projects, "Projects");
        foreach (var project in sections.Projects) projects.Children.Add(Project(project));
        content.Children.Add(projects);
        Heading("Recents");
        if (sections.Recents.Count > 0) content.Children.Add(List("Recents", sections.Recents));
        else Empty("No matching chats.");
        if (sections.HasMore)
        {
            var more = new Button { Style = S("ButtonBaseStyle"), Content = "Show more", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6) };
            content.Children.Add(more);
        }
        UpdateSelection();
    }

    void Heading(string text)
    {
        // .sidebar-heading: margin 10px 6px 4px; 11px, 600, letter-spacing .06em, upper case, muted. An empty text's
        // margins collapse with the heading's in CSS (4 below it, 10 above the next), hence no vertical margin on
        // .sidebar-empty here.
        content.Children.Add(new SpacedText
        {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, LineHeight = 15.4,
            Foreground = B("mutedBrush"), Margin = new Thickness(6, 10, 6, 4), HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    void Empty(string text) => content.Children.Add(new TextBlock
    {
        Text = text, Style = S("Text12"), Foreground = B("mutedBrush"), Margin = new Thickness(6, 0, 6, 0), TextWrapping = TextWrapping.Wrap,
    });

    ListBox List(string name, IEnumerable<ChatRow> chats)
    {
        var list = new ListBox { Style = S("PlainList") };
        AutomationProperties.SetName(list, name);
        foreach (var chat in chats) list.Items.Add(Item(chat));
        return list;
    }

    ListBoxItem Item(ChatRow chat)
    {
        var row = ChatVisual(chat);
        var item = new ListBoxItem { Content = row, ToolTip = chat.Tooltip };
        AutomationProperties.SetName(item, chat.Label);
        AutomationProperties.SetHelpText(item, chat.Tooltip);
        item.PreviewMouseLeftButtonUp += (_, e) =>
        {
            Choose(chat);
            e.Handled = true;
        };
        item.MouseRightButtonUp += (_, e) =>
        {
            selected = chat.Key;
            UpdateSelection();
            RowMenu?.Invoke(chat, item);
            e.Handled = true;
        };
        item.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || (Keyboard.Modifiers == ModifierKeys.Shift && (e.Key == Key.F10 || e.SystemKey == Key.F10)))
            {
                RowMenu?.Invoke(chat, item);
                e.Handled = true;
                return;
            }
            if (e.Key is not (Key.Enter or Key.Space)) return;
            Choose(chat);
            e.Handled = true;
        };
        return item;
    }

    void Choose(ChatRow chat)
    {
        selected = chat.Key;
        UpdateSelection();
        Open?.Invoke(chat);
    }

    public void Select(string? key)
    {
        selected = key;
        UpdateSelection();
    }

    void UpdateSelection()
    {
        foreach (var row in rows.Values) Paint(row);
    }

    void Paint(Border row)
    {
        var chosen = row.Tag is ChatRow chat && chat.Key == selected;
        var current = row.Tag is ProjectRow { Current: true };
        row.Background = chosen ? B("selectedBrush") : row.IsMouseOver || current ? B("surface-2Brush") : Brushes.Transparent;
        row.BorderBrush = chosen ? B("selectedBorderBrush") : null;
    }

    /// <summary>.sidebar-row of a chat: status dot (or the running mark), provider icon, title (wrapping) with the
    /// time at its right, and the meta line.</summary>
    Border ChatVisual(ChatRow chat)
    {
        var grid = new Grid();
        foreach (var w in new[] { 8.0, 8, 16, 8 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement mark = chat.RunningMark
            ? new TextBlock { Text = "▶", FontSize = 9, Foreground = B("okBrush"), Width = 8 }
            : new StatusDot { Status = chat.Status };
        mark.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(mark, chat.RunningMark ? "Running" : SidebarView.StatusLabels.GetValueOrDefault(chat.Status, chat.Status));
        grid.Children.Add(mark);
        var icon = Provider(chat.Provider);
        Grid.SetColumn(icon, 2);
        grid.Children.Add(icon);

        var titleLine = new Grid();
        titleLine.ColumnDefinitions.Add(new ColumnDefinition());
        titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleLine.Children.Add(new TextBlock { Text = chat.Label, Style = S("Text13"), Foreground = B("textBrush"), TextWrapping = TextWrapping.Wrap });
        if (chat.Stamp != "")
        {
            // .sidebar-used: 11px, muted, on the title's baseline (gap 8).
            var stamp = new TextBlock { Text = chat.Stamp, Style = S("Text11"), Foreground = B("mutedBrush"), Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(stamp, 1);
            titleLine.Children.Add(stamp);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleLine);
        text.Children.Add(new TextBlock { Text = chat.Meta, Style = S("Text12"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 4);
        grid.Children.Add(text);
        return Row(grid, chat, chat.Key + "|" + chat.Section);
    }

    Border Row(UIElement child, object tag, string id)
    {
        var row = new Border
        {
            Child = child, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
            BorderThickness = new Thickness(1), Tag = tag, Cursor = Cursors.Hand, SnapsToDevicePixels = true,
        };
        // padding 6px; the selection's inset 1px box-shadow is the border, so the padding is 5 inside it.
        row.Padding = new Thickness(5);
        row.MouseEnter += (_, _) => Paint(row);
        row.MouseLeave += (_, _) => Paint(row);
        rows[id] = row;
        return row;
    }

    /// <summary>ProviderIcon.tsx: the provider's mark at 16 px in its colour; "?" when unknown.</summary>
    static FrameworkElement Provider(string? provider)
    {
        FrameworkElement icon = provider switch
        {
            "chatgpt" => new VectorIcon { Kind = "chatgpt", Size = 16, Foreground = B("providerChatgptBrush") },
            "anthropic" => new VectorIcon { Kind = "anthropic", Size = 16, Foreground = B("providerAnthropicBrush") },
            _ => new TextBlock { Text = "?", FontSize = 12, Foreground = B("providerUnknownBrush"), HorizontalAlignment = HorizontalAlignment.Center },
        };
        icon.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(icon, provider switch { "chatgpt" => "ChatGPT", "anthropic" => "Claude", _ => "Unknown provider" });
        return icon;
    }

    /// <summary>A project (.sidebar-project): the folder-workspace row and its + button, then its chats when expanded.</summary>
    FrameworkElement Project(ProjectRow project)
    {
        var busy = project.Activity;
        var grid = new Grid();
        foreach (var w in new[] { 14.0, 8, 14, 8 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // .project-chevron: the collapse arrow turned to point right (closed) or down (open).
        var chevron = new VectorIcon
        {
            Kind = "collapse", Size = 14, Foreground = B("textBrush"), VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(project.Expanded ? -90 : 180),
        };
        grid.Children.Add(chevron);
        var folder = new VectorIcon { Kind = "folder", Size = 14, Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(folder, 2);
        grid.Children.Add(folder);

        var title = new TextBlock
        {
            Style = S("Text13"), Foreground = B("textBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = busy is null ? FontWeights.Normal : FontWeights.SemiBold,
        };
        title.Inlines.Add(new Run(project.Name));
        if (busy is not null)
            title.Inlines.Add(new Run($" ({busy.Count})") { Foreground = CountBrush(busy.Status) });
        Grid.SetColumn(title, 4);
        grid.Children.Add(title);
        if (busy is not null)
        {
            var state = StateMark(busy.Status);
            state.Margin = new Thickness(8, 0, 0, 0);
            state.ToolTip = project.StateLabel;
            AutomationProperties.SetName(state, project.StateLabel);
            Grid.SetColumn(state, 5);
            grid.Children.Add(state);
        }
        var count = new TextBlock { Text = project.Count.ToString(), Style = S("Text13"), Foreground = B("mutedBrush"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 6);
        grid.Children.Add(count);

        var row = Row(grid, project, "project|" + project.Id);
        row.MinHeight = 44;
        row.ToolTip = project.Tooltip;
        Paint(row);
        var button = new Button { Content = row, Template = BareTemplate(), Focusable = true, FocusVisualStyle = null };
        AutomationProperties.SetName(button, project.Name);
        AutomationProperties.SetHelpText(button, project.Tooltip);
        AutomationProperties.SetItemStatus(button, project.Expanded ? "expanded" : "collapsed");
        button.Click += (_, _) => ProjectToggled?.Invoke(project.Id);

        var plus = new Button { Style = S("IconButton"), Content = new VectorIcon { Kind = "plus", Size = 14 }, ToolTip = $"New session in {project.Name}", Margin = new Thickness(2, 0, 0, 0) };
        AutomationProperties.SetName(plus, $"New session in {project.Name}");
        var header = new DockPanel();
        DockPanel.SetDock(plus, Dock.Right);
        header.Children.Add(plus);
        header.Children.Add(button);

        var panel = new StackPanel();
        panel.Children.Add(header);
        if (project.Expanded)
        {
            var chats = new Border
            {
                Margin = new Thickness(16, 0, 0, 0), Padding = new Thickness(6, 0, 0, 0),
                BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1, 0, 0, 0),
            };
            if (project.Chats.Count > 0) chats.Child = List($"{project.Name} chats", project.Chats);
            else chats.Child = new TextBlock { Text = "No matching chats.", Style = S("Text12"), Foreground = B("mutedBrush"), Margin = new Thickness(6, 4, 6, 4) };
            panel.Children.Add(chats);
        }
        return panel;
    }

    static ControlTemplate BareTemplate()
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        template.Seal();
        return template;
    }

    static Brush CountBrush(string status) => status switch
    {
        "idle" => B("okBrush"),
        "busy" => B("orangeBrush"),
        "background" => new SolidColorBrush(((SolidColorBrush)B("orangeBrush")).Color) { Opacity = 0.6 },
        _ => B("dangerBrush"),
    };

    /// <summary>.project-state: idle a dot, busy a diamond (dimmed: background work), waiting a square, error a "!" badge.</summary>
    static FrameworkElement StateMark(string status)
    {
        FrameworkElement mark = status switch
        {
            "idle" => new Ellipse { Width = 8, Height = 8, Fill = B("okBrush") },
            "busy" or "background" => new Rectangle
            {
                Width = 7, Height = 7, Fill = B("orangeBrush"), Opacity = status == "background" ? 0.45 : 1,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
            },
            "waiting" => new Rectangle { Width = 8, Height = 8, Fill = B("dangerBrush") },
            _ => new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Background = B("dangerBrush"),
                Child = new TextBlock { Text = "!", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("bgBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        mark.VerticalAlignment = VerticalAlignment.Center;
        return mark;
    }
}
