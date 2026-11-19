using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The chat view of a thread, laid out as Codex lays out a thread: your messages as bubbles on the right, each turn's
/// work folded under "Worked for …" with the final answer open, steps as muted one-line rows that open to the command and its
/// output, markdown in the answers, a card of the files edited, and a rounded message box with a + button and the permission
/// mode. It reads the transcript file the CLI writes; the message box types into the running session. Rebuilt when the
/// transcript grew, at most once a second while shown.</summary>
sealed class ChatView : Border
{
    const int MaxItems = 400;
    const int MaxResult = 6000;
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    readonly string claudeSessionId;
    readonly TranscriptModel model = new();
    readonly StackPanel rows = new() { Margin = new Thickness(0, 8, 0, 12), MaxWidth = 860 };
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly DispatcherTimer timer;
    readonly Dictionary<string, bool> open = [];
    readonly Func<IReadOnlyList<(string Name, string Detail)>> scheduled;
    readonly Button stopButton;
    readonly Action<string, bool> fork;
    readonly List<string> history = [];
    int historyAt = -1;
    static readonly string[] Models = ["default", "opus", "sonnet", "haiku"];
    readonly Border side = new() { Width = 300, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 6, 18, 14), VerticalAlignment = VerticalAlignment.Top };
    readonly StackPanel sideRows = new() { Margin = new Thickness(16, 8, 16, 14) };
    readonly HashSet<string> sideAll = [];
    static bool sideOpen;
    string sideSignature = "";
    string? path;
    long offset;

    public ChatView(string claudeSessionId, Func<string, Task<bool>> send, Func<string> permission, Action<FrameworkElement> permissionMenu,
        Func<IReadOnlyList<(string Name, string Detail)>> scheduled, Func<string> model, Action interrupt, Action<string, bool> fork, Func<string> title)
    {
        this.claudeSessionId = claudeSessionId;
        this.fork = fork;
        this.scheduled = scheduled;
        Background = B("terminalBrush");
        var input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, MinHeight = 44, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, Foreground = B("textBrush"), BorderThickness = new Thickness(0), CaretBrush = B("textBrush"),
            Padding = new Thickness(4, 6, 4, 4), FontSize = 14,
        };
        var placeholder = new TextBlock { Text = "Message this session", Foreground = B("mutedBrush"), FontSize = 14, IsHitTestVisible = false, Margin = new Thickness(8, 8, 0, 0) };
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var status = new TextBlock { Style = S("Text12"), Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        async Task Send()
        {
            var text = input.Text.Trim();
            if (text.Length == 0) return;
            input.IsEnabled = false;
            var sent = await send(text);
            input.IsEnabled = true;
            if (sent)
            {
                historyAt = -1;
                input.Clear();
                status.Text = "";
                Refresh(true);
            }
            else status.Text = "Not sent: the session is not running";
            input.Focus();
        }
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage() && PasteImage() is { } pasted)
            {
                e.Handled = true;
                input.SelectedText = (pasted.Contains(' ') ? "\"" + pasted + "\"" : pasted) + " ";
                input.CaretIndex = input.SelectionStart + input.SelectionLength;
                return;
            }
            // Up and Down walk the messages sent before (from the first line / the last line of the box).
            if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None && input.GetLineIndexFromCharacterIndex(input.CaretIndex) == 0 && history.Count > 0)
            {
                historyAt = Math.Min(historyAt + 1, history.Count - 1);
                input.Text = history[history.Count - 1 - historyAt];
                input.CaretIndex = input.Text.Length;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None && historyAt >= 0
                && input.GetLineIndexFromCharacterIndex(input.CaretIndex) == input.LineCount - 1)
            {
                historyAt--;
                input.Text = historyAt < 0 ? "" : history[history.Count - 1 - historyAt];
                input.CaretIndex = input.Text.Length;
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            e.Handled = true;
            _ = Send();
        };
        var plus = Chip("+", "Add files: their paths are put in the message");
        plus.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Add files to the message" };
            if (dialog.ShowDialog() != true) return;
            input.SelectedText = string.Join(" ", dialog.FileNames.Select(f => f.Contains(' ') ? "\"" + f + "\"" : f)) + " ";
            input.CaretIndex = input.SelectionStart + input.SelectionLength;
            input.Focus();
        };
        var mode = Chip(StateValues.PermissionLabel(permission()), "Permission mode (applies when the session starts next)");
        mode.Click += (_, _) => permissionMenu(mode);
        var modelChip = Chip(model(), "Model of this session: types /model into it");
        modelChip.Click += (_, _) =>
        {
            var menu = ShellMenus.Build(Models.Select(m => new MenuSpec("normal", m, Action: "model:" + m)), _ => true, action =>
            {
                var chosen = action["model:".Length..];
                ((TextBlock)modelChip.Content).Text = chosen;
                _ = send("/model " + chosen);
            });
            menu.PlacementTarget = modelChip;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        };
        stopButton = Chip("■ Stop", "Interrupt the model (Esc in the terminal)");
        stopButton.Visibility = Visibility.Collapsed;
        stopButton.Click += (_, _) => interrupt();
        var bar = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(plus, Dock.Left);
        DockPanel.SetDock(mode, Dock.Left);
        DockPanel.SetDock(modelChip, Dock.Right);
        DockPanel.SetDock(stopButton, Dock.Right);
        bar.Children.Add(plus);
        bar.Children.Add(mode);
        bar.Children.Add(modelChip);
        bar.Children.Add(stopButton);
        bar.Children.Add(status);
        var stack = new StackPanel();
        stack.Children.Add(new Grid { Children = { input, placeholder } });
        stack.Children.Add(bar);
        var composer = new Border
        {
            Background = B("surfaceBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(16, 4, 16, 14), MaxWidth = 860, Child = stack,
        };
        var layout = new DockPanel();
        DockPanel.SetDock(composer, Dock.Bottom);
        layout.Children.Add(composer);
        scroll.Content = rows;
        // A selection in a message gets an "Add to chat" button: the selected text goes into the message box as a quote.
        var addButton = new Button { Style = S("IconButton"), Width = double.NaN, Content = new TextBlock { Text = "Add to chat", Style = S("Text12"), Margin = new Thickness(10, 0, 10, 0) } };
        var addPopup = new System.Windows.Controls.Primitives.Popup
        {
            StaysOpen = false, AllowsTransparency = true, PlacementTarget = rows, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            Child = new Border { Background = B("surfaceBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = addButton },
        };
        var picked = "";
        addButton.Click += (_, _) =>
        {
            addPopup.IsOpen = false;
            var quote = string.Join("\n", picked.Trim().Split('\n').Select(l => "> " + l.TrimEnd('\r'))) + "\n";
            input.Text = (input.Text.Length > 0 && !input.Text.EndsWith('\n') ? input.Text + "\n" : input.Text) + quote;
            input.Focus();
            input.CaretIndex = input.Text.Length;
        };
        rows.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            for (DependencyObject? at = source; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is RichTextBox box)
                {
                    picked = box.Selection.Text;
                    if (picked.Trim().Length == 0) return;
                    var where = e.GetPosition(rows);
                    addPopup.HorizontalOffset = Math.Max(0, where.X - 40);
                    addPopup.VerticalOffset = Math.Max(0, where.Y - 38);
                    addPopup.IsOpen = true;
                    return;
                }
        }), true);
        side.BorderBrush = B("borderBrush");
        side.Background = B("surfaceBrush");
        side.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sideRows };
        side.HorizontalAlignment = HorizontalAlignment.Right;
        side.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };
        var more = new Button
        {
            Style = S("IconButton"), Width = 28, Height = 28, ToolTip = "More",
            Content = new TextBlock { Text = "\uE712", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = B("mutedBrush") },
        };
        more.Click += (_, _) =>
        {
            var menu = ShellMenus.Build([new MenuSpec("normal", "Copy conversation", Action: "copy"), new MenuSpec("normal", "Copy session id", Action: "id")], _ => true, action =>
            {
                try
                {
                    Clipboard.SetText(action == "id" ? claudeSessionId : string.Join("\n\n", this.model.Items.Select(i => i switch
                    {
                        UserMessageItem u => "You: " + u.Text,
                        AssistantTextItem a => a.Text,
                        _ => "",
                    }).Where(t => t.Length > 0)));
                }
                catch (System.Runtime.InteropServices.COMException) { }
            });
            menu.PlacementTarget = more;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        var titleText = new TextBlock { Text = title(), Style = S("Text13"), Foreground = B("textBrush"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var toggle = new Button
        {
            Style = S("IconButton"), Width = 28, Height = 28, ToolTip = "Scheduled tasks, changes, subagents and sources",
            Content = new TextBlock { Text = "\uE8A0", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = B("mutedBrush") },
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Side column");
        toggle.Click += (_, _) =>
        {
            sideOpen = !sideOpen;
            side.Visibility = sideOpen ? Visibility.Visible : Visibility.Collapsed;
            sideSignature = "";
            RefreshSide();
        };
        var header = new DockPanel { Margin = new Thickness(16, 6, 10, 2) };
        DockPanel.SetDock(toggle, Dock.Right);
        DockPanel.SetDock(more, Dock.Right);
        header.Children.Add(toggle);
        header.Children.Add(more);
        header.Children.Add(titleText);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        // The card floats over the conversation (the conversation keeps its width and its scroll bar).
        var stage = new Grid();
        stage.Children.Add(scroll);
        stage.Children.Add(side);
        stage.SizeChanged += (_, e) => side.MaxHeight = Math.Max(120, e.NewSize.Height - 20);
        layout.Children.Add(stage);
        Child = layout;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            Refresh(false);
            RefreshSide();
            UpdateWorking();
        }, Dispatcher);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                ((TextBlock)mode.Content).Text = StateValues.PermissionLabel(permission());
                titleText.Text = title();
                side.Visibility = sideOpen ? Visibility.Visible : Visibility.Collapsed;
                Refresh(false);
                RefreshSide();
                timer.Start();
            }
            else timer.Stop();
        };
    }

    static Button Chip(string text, string tip) => new()
    {
        Style = S("IconButton"), Width = double.NaN, MinWidth = 24, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0),
        Content = new TextBlock { Text = text, Style = S("Text12"), Margin = new Thickness(8, 0, 8, 0) },
    };

    string? Find()
    {
        if (path is not null && File.Exists(path)) return path;
        try
        {
            var projects = Path.Combine(ClaudeRegistry.Home(), "projects");
            if (!Directory.Exists(projects)) return null;
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(dir, claudeSessionId + ".jsonl");
                if (File.Exists(candidate)) return path = candidate;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    void Refresh(bool toEnd)
    {
        if (string.IsNullOrEmpty(claudeSessionId)) return;
        if (Find() is not { } file)
        {
            if (rows.Children.Count == 0) rows.Children.Add(Note("No transcript yet."));
            return;
        }
        TranscriptRead read;
        try { read = TranscriptReader.Read(file, offset); }
        catch (IOException) { return; }
        if (read.WasReset) model.Reset();
        offset = read.NewOffset;
        if (read.Lines.Count == 0 && rows.Children.Count > 0 && !read.WasReset) return;
        model.Append(read.Lines);
        var atEnd = toEnd || scroll.VerticalOffset >= scroll.ScrollableHeight - 24;
        rows.Children.Clear();
        // The transcript also holds bookkeeping lines (file-history-snapshot, api-request, turn_duration ...): not rows.
        List<ChatItem> items = [.. model.Items.Where(i => i is not UnknownItem && i is not SystemNoticeItem { Bare: true }
            && !(i is UserMessageItem { Text: var t } && t.StartsWith("[Image: source:", StringComparison.Ordinal)))];
        if (items.Count > MaxItems) items = items.GetRange(items.Count - MaxItems, MaxItems);
        if (items.Count == 0) rows.Children.Add(Note("The transcript is empty."));
        var turns = new List<List<ChatItem>>();
        foreach (var item in items)
        {
            if (item is UserMessageItem || turns.Count == 0) turns.Add([]);
            turns[^1].Add(item);
        }
        for (var n = 0; n < turns.Count; n++) AddTurn(turns[n], n == turns.Count - 1);
        if (atEnd) scroll.ScrollToEnd();
    }

    /// <summary>The clipboard's image saved as a file (the way a pasted image reaches the terminal), or null.</summary>
    static string? PasteImage()
    {
        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Clipboard.GetImage()));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return Drops.Save("clipboard.png", buffer.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentNullException)
        {
            return null;
        }
    }

    /// <summary>The model is working while the last thing in the transcript is your message, a thought or a call still waiting
    /// for its result, and the file changed in the last ten minutes. Then the Stop button shows; it also keeps the sent
    /// messages for the Up key.</summary>
    void UpdateWorking()
    {
        var items = model.Items;
        history.Clear();
        foreach (var item in items)
            if (item is UserMessageItem u && InjectedMessage.Parse(u.Text) is null && !u.Text.StartsWith("[Request interrupted", StringComparison.Ordinal) && u.Text != "[image]") history.Add(u.Text);
        var working = items.Count > 0 && items[^1] switch
        {
            UserMessageItem u => InjectedMessage.Parse(u.Text) is not null || !u.Text.StartsWith("[Request interrupted", StringComparison.Ordinal),
            ThinkingItem => true,
            ToolGroupItem g => g.Calls.Any(c => c.IsPending),
            _ => false,
        };
        if (working && path is not null)
            try { working = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10); }
            catch (IOException) { working = false; }
        stopButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- the side column ----------

    static readonly string[] EditTools = ["Edit", "MultiEdit", "NotebookEdit"];

    /// <summary>The column at the right (as Codex's): this thread's scheduled tasks, the files it changed and wrote, its
    /// subagents and the links and files it looked at. Built from the transcript's tool calls; rebuilt only when they changed.</summary>
    void RefreshSide()
    {
        if (!sideOpen) return;
        var calls = CallsOf(model.Items).ToList();
        var tasks = scheduled();
        var changed = calls.Where(c => EditTools.Contains(c.Name)).Select(c => c.Summary).Where(s => s.Length > 0).Distinct().ToList();
        var written = calls.Where(c => c.Name == "Write").Select(c => c.Summary).Where(s => s.Length > 0).Distinct().ToList();
        var agents = calls.Where(c => c.Name is "Task" or "Agent").ToList();
        var sources = calls.Where(c => c.Name is "WebFetch" or "WebSearch" or "Read").Select(c => (c.Name == "WebSearch" ? "search: " : "") + c.Summary)
            .Where(s => s.Length > 0).Distinct().ToList();
        var signature = string.Join('\u0001', tasks.Select(t => t.Name + t.Detail)) + '\u0002' + string.Join('\u0001', changed) + '\u0002' + string.Join('\u0001', written)
            + '\u0002' + string.Join('\u0001', agents.Select(a => a.Summary + (a.IsPending ? "…" : a.IsError ? "!" : "✓"))) + '\u0002' + string.Join('\u0001', sources) + '\u0002' + string.Join(',', sideAll);
        if (signature == sideSignature) return;
        sideSignature = signature;
        sideRows.Children.Clear();
        SideSection("scheduled", "Scheduled", tasks.Select(t => (t.Name + " · " + t.Detail, t.Detail)).ToList(), "");
        SideSection("changes", "Changes", changed.Select(f => (System.IO.Path.GetFileName(f), f)).ToList(), "");
        SideSection("outputs", "Outputs", written.Select(f => (System.IO.Path.GetFileName(f), f)).ToList(), "");
        var done = agents.Count(a => !a.IsPending);
        SideSection("agents", "Subagents", agents.Select(a => ((a.IsPending ? "… " : a.IsError ? "✕ " : "✓ ") + a.Summary, a.Summary)).ToList(), done > 0 ? $"{done} done" : "");
        SideSection("sources", "Sources", sources.Select(s => (System.IO.Path.GetFileName(s).Length > 0 && !s.StartsWith("search: ") && !s.Contains("://") ? System.IO.Path.GetFileName(s) : s, s)).ToList(), "");
        if (sideRows.Children.Count == 0) sideRows.Children.Add(Note("Nothing yet."));
    }

    static string GlyphOf(string key) => key switch
    {
        "scheduled" => "\uE121",
        "changes" => "\uE70F",
        "outputs" => "\uE8A5",
        "agents" => "\uE77B",
        _ => "\uE774",
    };

    void SideSection(string key, string title, List<(string Name, string Detail)> items, string count)
    {
        if (items.Count == 0) return;
        var head = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
        if (count.Length > 0)
        {
            var right = new TextBlock { Text = count, Style = S("Text12"), Foreground = B("mutedBrush") };
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
        }
        head.Children.Add(new TextBlock { Text = title, Style = S("Text12"), Foreground = B("mutedBrush") });
        sideRows.Children.Add(head);
        var all = sideAll.Contains(key);
        foreach (var (name, detail) in items.Take(all ? 200 : 5))
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), ToolTip = detail };
            var glyph = new TextBlock { Text = GlyphOf(key), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = B("mutedBrush"), Width = 22, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(new TextBlock { Text = name, Style = S("Text13"), Foreground = B("textBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            sideRows.Children.Add(row);
        }
        if (items.Count > 5)
        {
            var more = new TextBlock
            {
                Text = all ? "Show less" : "View all", Style = S("Text12"), Foreground = B("mutedBrush"), Cursor = Cursors.Hand,
                Margin = new Thickness(0, 4, 0, 2), Background = Brushes.Transparent,
            };
            more.MouseLeftButtonUp += (_, _) =>
            {
                if (!sideAll.Remove(key)) sideAll.Add(key);
                sideSignature = "";
                RefreshSide();
            };
            sideRows.Children.Add(more);
        }
    }

    void AddTurn(List<ChatItem> turn, bool last)
    {
        List<ChatItem> rest = turn;
        if (turn[0] is UserMessageItem user)
        {
            rows.Children.Add(Bubble(user));
            rest = turn.GetRange(1, turn.Count - 1);
        }
        var finalText = rest.OfType<AssistantTextItem>().LastOrDefault();
        List<ChatItem> work = [.. rest.Where(i => !ReferenceEquals(i, finalText))];
        var turnKey = turn[0].Uuid ?? turn[0].Timestamp ?? "";
        if (work.Count > 0)
        {
            List<ChatItem> timed = [.. work];
            if (finalText is not null) timed.Add(finalText);
            rows.Children.Add(Fold("turn:" + turnKey, "Worked" + Duration(timed), () =>
            {
                var inner = new StackPanel();
                foreach (var item in work) inner.Children.Add(Row(item));
                return inner;
            }, defaultOpen: last && finalText is null));
        }
        if (finalText is not null) rows.Children.Add(new Border { Margin = new Thickness(0, 8, 16, 8), Child = new StackPanel { Children = { Markdown(finalText.Text), MetaRow(null, finalText.Text, HorizontalAlignment.Left) } }, ContextMenu = ForkMenu(finalText.Uuid, false) });
        var edited = new List<string>();
        foreach (var call in CallsOf(rest))
            if (call.Name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit" && !edited.Contains(call.Summary)) edited.Add(call.Summary);
        if (edited.Count > 0 && finalText is not null) rows.Children.Add(FilesCard(edited, "turnfiles:" + turnKey));
    }

    static IEnumerable<ToolCallItem> CallsOf(IEnumerable<ChatItem> items)
    {
        foreach (var item in items)
        {
            if (item is ToolGroupItem group) foreach (var call in group.Calls) yield return call;
            else if (item is ToolCallItem single) yield return single;
        }
    }

    static string Duration(IEnumerable<ChatItem> items)
    {
        var times = new List<DateTimeOffset>();
        foreach (var item in items)
            if (DateTimeOffset.TryParse(item.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) times.Add(t);
        if (times.Count < 2) return "";
        var span = times.Max() - times.Min();
        return " for " + (span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{Math.Max(1, (int)span.TotalSeconds)}s");
    }

    UIElement Row(ChatItem item) => item switch
    {
        UserMessageItem u => Bubble(u),
        AssistantTextItem a => new Border { Margin = new Thickness(0, 6, 16, 6), Child = Markdown(a.Text) },
        ThinkingItem t => new Border { Margin = new Thickness(0, 6, 16, 6), BorderBrush = AgentTint, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Child = Markdown(t.Text, "textBrush") },
        ToolGroupItem { Calls.Count: 1 } g => Call(g.Calls[0]),
        ToolGroupItem g => Fold("group:" + g.Uuid + g.Label, g.Label, () =>
        {
            var inner = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            foreach (var call in g.Calls) inner.Children.Add(Call(call));
            return inner;
        }, step: true, glyph: KindOf(g.Calls[0].Name).Glyph, tint: B("mutedBrush")),
        ToolCallItem c => Call(c),
        CompactionItem => IconLine("\uE895", "Context automatically compacted"),
        SystemNoticeItem s => Note(s.Text),
        _ => new Border(),
    };

    /// <summary>What another session or a scheduled task put into the thread: a card on the left with its sender, folded to its title.</summary>
    UIElement Injected(UserMessageItem user, InjectedMessage message) => new Border
    {
        BorderBrush = B("borderBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 10, 60, 10), Padding = new Thickness(12, 2, 0, 2),
        Child = message.Body.Length == 0
            ? new TextBlock { Text = "↩ " + message.Title, Style = S("Text12"), Foreground = B("mutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) }
            : Fold("injected:" + user.Uuid, "↩ " + message.Title, () => new Border { Margin = new Thickness(0, 2, 0, 6), Child = Markdown(message.Body, "mutedBrush") }),
    };

    /// <summary>Right-click menu to continue the conversation as a new thread from this message (before it) or this answer (after it).</summary>
    ContextMenu? ForkMenu(string? uuid, bool before)
    {
        if (uuid is null) return null;
        var item = new MenuItem { Header = before ? "Fork a new thread from before this message" : "Fork a new thread from here" };
        item.Click += (_, _) => fork(uuid, before);
        var menu = new ContextMenu();
        menu.Items.Add(item);
        return menu;
    }

    UIElement Bubble(UserMessageItem user)
    {
        if (InjectedMessage.Parse(user.Text) is { } injected) return Injected(user, injected);
        var inner = new StackPanel { ContextMenu = ForkMenu(user.Uuid, true) };
        foreach (var data in user.Images.Take(6))
        {
            var image = Picture(data);
            if (image is not null) inner.Children.Add(image);
        }
        var text = string.Join("\n", user.Text.Split('\n').Where(l => l != "[image]" && !System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^\[Image #\d+\]$"))).Trim();
        if (text.Length > 0) inner.Children.Add(Plain(text, "textBrush"));
        var bubble = new Border
        {
            Background = B("surfaceBrush"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 9, 14, 9),
            HorizontalAlignment = HorizontalAlignment.Right, Child = inner,
        };
        var column = new StackPanel { Margin = new Thickness(60, 12, 16, 8) };
        column.Children.Add(bubble);
        column.Children.Add(MetaRow(user.Timestamp, text, HorizontalAlignment.Right));
        return column;
    }

    /// <summary>The small row under a message: its time and a button that copies its text.</summary>
    static UIElement MetaRow(string? timestamp, string copyText, HorizontalAlignment side)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = side, Margin = new Thickness(0, 4, 4, 0) };
        if (side == HorizontalAlignment.Right && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            row.Children.Add(new TextBlock { Text = at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), Style = S("Text12"), Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var copy = new Button
        {
            Style = S("IconButton"), Width = 24, Height = 22, ToolTip = "Copy",
            Content = new TextBlock { Text = "\uE8C8", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = B("mutedBrush") },
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(copyText); }
            catch (System.Runtime.InteropServices.COMException) { }
        };
        row.Children.Add(copy);
        return row;
    }

    /// <summary>A pasted image from its base64 data, at most 260 pixels high; null when it cannot be decoded.</summary>
    static Image? Picture(string base64)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            bitmap.EndInit();
            bitmap.Freeze();
            var image = new Image
            {
                Source = bitmap, MaxHeight = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand, ToolTip = "Click to enlarge",
            };
            image.MouseLeftButtonUp += (_, _) => ImageViewer.Open(image, bitmap);
            return image;
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    // One tint and one icon per kind of event: commands green, reading and searching blue, edits amber, agents purple, errors red.
    static UIElement IconLine(string glyph, string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = B("mutedBrush"), Width = 24, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, Style = S("Text12"), Foreground = B("mutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    static readonly Brush CommandTint = new SolidColorBrush(Color.FromRgb(0x7E, 0xC6, 0x99));
    static readonly Brush ReadTint = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF));
    static readonly Brush EditTint = new SolidColorBrush(Color.FromRgb(0xE3, 0xB3, 0x41));
    static readonly Brush AgentTint = new SolidColorBrush(Color.FromRgb(0xB3, 0x92, 0xF0));
    static readonly Brush ErrorTint = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

    static (string Glyph, Brush Tint) KindOf(string name) => name switch
    {
        "Bash" => ("\uE756", CommandTint),
        "Edit" or "MultiEdit" or "NotebookEdit" => ("\uE70F", EditTint),
        "Write" => ("\uE74E", EditTint),
        "Read" => ("\uE8A5", ReadTint),
        "Grep" or "Glob" or "WebSearch" or "WebFetch" => ("\uE721", ReadTint),
        "Task" or "Agent" => ("\uE77B", AgentTint),
        _ => ("\uE713", CommandTint),
    };

    UIElement Call(ToolCallItem call)
    {
        var (glyph, tint) = KindOf(call.Name);
        var verb = call.Name switch
        {
            "Bash" => "Ran",
            "Edit" or "MultiEdit" or "NotebookEdit" => "Edited",
            "Write" => "Wrote",
            "Read" => "Read",
            "Grep" or "Glob" => "Searched",
            "Task" or "Agent" => "Agent:",
            _ => call.Name,
        };
        var head = $"{verb} {call.Summary}".TrimEnd() + (call.IsPending ? "  …" : call.IsError ? "  (error)" : "");
        return Fold("call:" + call.Id, head, () =>
        {
            var inner = new StackPanel { Margin = new Thickness(14, 2, 0, 6) };
            var result = call.Result ?? "(no result yet)";
            var shown = result.Length > MaxResult ? result[..MaxResult] + "\n… " + (result.Length - MaxResult) + " more characters" : result;
            if (call.Diff.Count > 0)
            {
                inner.Children.Add(DiffCard(call));
                if (call.IsError) inner.Children.Add(Text(shown, "textBrush", mono: true));
            }
            else
                inner.Children.Add(new Border
                {
                    Background = B("surfaceBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8), Child = Text(call.Summary + "\n\n" + shown, call.IsError ? "textBrush" : "mutedBrush", mono: true),
                });
            if (call.Children.Count > 0)
            {
                inner.Children.Add(Note($"{call.Children.Count} subagent items"));
                foreach (var child in call.Children.Take(60)) inner.Children.Add(Row(child));
            }
            return inner;
        }, step: true, glyph: glyph, tint: call.IsError ? ErrorTint : tint);
    }

    /// <summary>An edit's lines: removed ones on red, added ones on green, the file's name above.</summary>
    UIElement DiffCard(ToolCallItem call)
    {
        var removed = new SolidColorBrush(Color.FromArgb(0x30, 0xF8, 0x51, 0x49));
        var added = new SolidColorBrush(Color.FromArgb(0x30, 0x3F, 0xB9, 0x50));
        var lines = new StackPanel();
        lines.Children.Add(new TextBlock { Text = call.Summary, Style = S("Text12"), Foreground = B("mutedBrush"), Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = call.Summary });
        foreach (var line in call.Diff)
            lines.Children.Add(new TextBlock
            {
                Text = line, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = B("textBrush"), TextWrapping = TextWrapping.Wrap,
                Background = line[0] == '-' ? removed : added, Padding = new Thickness(6, 0, 6, 0),
            });
        return new Border
        {
            Background = B("surfaceBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8), Child = lines,
        };
    }

    /// <summary>"Edited 3 files" and the files, as Codex's card of a turn's changes (without counts: the transcript does not hold them).</summary>
    UIElement FilesCard(List<string> files, string key)
    {
        var card = new StackPanel();
        card.Children.Add(new TextBlock
        {
            Text = files.Count == 1 ? "Edited 1 file" : $"Edited {files.Count} files", Style = S("Text13"), Foreground = B("textBrush"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var file in files.Take(3)) card.Children.Add(FileLine(file));
        if (files.Count > 3)
            card.Children.Add(Fold(key, $"Show {files.Count - 3} more files", () =>
            {
                var more = new StackPanel();
                foreach (var file in files.Skip(3)) more.Children.Add(FileLine(file));
                return more;
            }));
        return new Border
        {
            Background = B("surfaceBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 16, 8), Child = card,
        };
    }

    static TextBlock FileLine(string file) => new()
    {
        Text = file, Style = S("Text12"), Foreground = B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file, Margin = new Thickness(0, 2, 0, 2),
    };

    /// <summary>A row folded until its header is clicked. The body is made when opened; the choice is kept across rebuilds.</summary>
    UIElement Fold(string key, string header, Func<UIElement> body, bool step = false, bool defaultOpen = false, string? glyph = null, Brush? tint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var label = new TextBlock
        {
            Style = S("Text12"), Foreground = glyph is null ? B("mutedBrush") : tint ?? B("mutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = header,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headRow = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = step ? new Thickness(0, 3, 0, 3) : new Thickness(0, 6, 0, 6) };
        if (glyph is not null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = tint ?? B("mutedBrush"), Width = 24, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            headRow.Children.Add(icon);
        }
        headRow.Children.Add(label);
        var content = new ContentPresenter();
        void Show(bool isOpen)
        {
            label.Text = header + (isOpen ? " ⌄" : " ›");
            content.Content = isOpen ? body() : null;
        }
        headRow.MouseLeftButtonUp += (_, _) =>
        {
            var now = !(open.TryGetValue(key, out var was) ? was : defaultOpen);
            open[key] = now;
            Show(now);
        };
        Show(open.TryGetValue(key, out var current) ? current : defaultOpen);
        panel.Children.Add(headRow);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>The Markdown the answers use (MarkdownLite): headings, lists, fenced code, bold and inline code. One read-only
    /// document, so a selection can run across paragraphs (and be added to the message box).</summary>
    static UIElement Markdown(string text, string brush = "textBrush")
    {
        var doc = NewDocument(brush, 14);
        foreach (var block in MarkdownLite.Parse(text))
        {
            Paragraph paragraph;
            switch (block.Kind)
            {
                case MdBlockKind.Code:
                    paragraph = new Paragraph(new Run(block.Raw))
                    {
                        FontFamily = new FontFamily("Consolas"), FontSize = 12, Background = B("surfaceBrush"), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 4),
                    };
                    break;
                case MdBlockKind.Bullet:
                    paragraph = new Paragraph { Margin = new Thickness(30, 2, 0, 2), TextIndent = -22 };
                    paragraph.Inlines.Add(new Run(block.Marker + " ") { Foreground = B("mutedBrush") });
                    AddSpans(paragraph, block.Spans);
                    break;
                case MdBlockKind.Heading:
                    paragraph = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = block.Level == 1 ? 17 : block.Level == 2 ? 15 : 14, Margin = new Thickness(0, 8, 0, 4) };
                    AddSpans(paragraph, block.Spans);
                    break;
                default:
                    paragraph = new Paragraph { Margin = new Thickness(0, 5, 0, 5) };
                    AddSpans(paragraph, block.Spans);
                    break;
            }
            doc.Blocks.Add(paragraph);
        }
        return Selectable(doc);
    }

    static void AddSpans(Paragraph paragraph, IReadOnlyList<MdSpan> spans)
    {
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeights.Bold;
            if (span.Code)
            {
                run.FontFamily = new FontFamily("Consolas");
                run.Background = B("surfaceBrush");
            }
            paragraph.Inlines.Add(run);
        }
    }

    static FlowDocument NewDocument(string brush, double size) => new()
    {
        PagePadding = new Thickness(0), Foreground = B(brush), FontSize = size, FontFamily = new TextBlock { Style = S("Text13") }.FontFamily, Background = Brushes.Transparent,
    };

    /// <summary>A read-only box around the document: text can be selected and copied. Its mouse wheel goes to the chat's scroll.</summary>
    static RichTextBox Selectable(FlowDocument doc)
    {
        var box = new RichTextBox
        {
            Document = doc, IsReadOnly = true, IsReadOnlyCaretVisible = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsInactiveSelectionHighlightEnabled = true,
            FocusVisualStyle = null, Cursor = Cursors.IBeam,
        };
        box.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled || box.Parent is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = box });
        };
        return box;
    }

    /// <summary>A message of plain text (no Markdown), selectable.</summary>
    static UIElement Plain(string text, string brush)
    {
        var doc = NewDocument(brush, 14);
        doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
        return Selectable(doc);
    }

    static TextBlock Text(string text, string brush, bool mono = false)
    {
        var block = new TextBlock { Text = text, Style = S("Text13"), Foreground = B(brush), TextWrapping = TextWrapping.Wrap };
        if (mono)
        {
            block.FontFamily = new FontFamily("Consolas");
            block.FontSize = 12;
        }
        return block;
    }

    static TextBlock Note(string text) => new() { Text = text, Style = S("Text12"), Foreground = B("mutedBrush"), Margin = new Thickness(0, 4, 0, 4), FontStyle = FontStyles.Italic };
}
