using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>The read-only chat view of a thread (chat-view-spec stage 2): its transcript as messages, with tool calls,
/// thinking and long results folded until clicked. It only reads the file the CLI writes; the terminal stays the place
/// for input. Rows are rebuilt when the transcript grew, at most once a second while the view is shown.</summary>
sealed class ChatView : Border
{
    const int MaxItems = 250;
    const int MaxResult = 6000;
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    readonly string claudeSessionId;
    readonly TranscriptModel model = new();
    readonly StackPanel rows = new() { Margin = new Thickness(10, 8, 10, 12) };
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly DispatcherTimer timer;
    readonly HashSet<string> opened = [];
    string? path;
    long offset;

    public ChatView(string claudeSessionId)
    {
        this.claudeSessionId = claudeSessionId;
        Background = B("terminalBrush");
        Child = scroll;
        scroll.Content = rows;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
                timer.Start();
            }
            else timer.Stop();
        };
    }

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

    void Refresh()
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
        var atEnd = scroll.VerticalOffset >= scroll.ScrollableHeight - 24;
        rows.Children.Clear();
        var items = model.Items;
        if (items.Count > MaxItems) rows.Children.Add(Note($"{items.Count - MaxItems} earlier items not shown"));
        foreach (var item in items.Skip(Math.Max(0, items.Count - MaxItems))) rows.Children.Add(Row(item));
        if (items.Count == 0) rows.Children.Add(Note("The transcript is empty."));
        if (atEnd) scroll.ScrollToEnd();
    }

    UIElement Row(ChatItem item) => item switch
    {
        UserMessageItem u => new Border
        {
            Background = B("surfaceBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 6),
            Child = Text(u.Text, "textBrush"),
        },
        AssistantTextItem a => new Border { Margin = new Thickness(0, 4, 0, 4), Child = Text(a.Text, "textBrush") },
        ThinkingItem t => Fold("thinking:" + t.Uuid, "Thinking", () => Text(t.Text, "mutedBrush")),
        ToolGroupItem g => Fold("group:" + g.Uuid + g.Label, g.Label, () =>
        {
            var inner = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            foreach (var call in g.Calls) inner.Children.Add(Call(call));
            return inner;
        }),
        ToolCallItem c => Call(c),
        CompactionItem => Note("Conversation compacted"),
        SystemNoticeItem s => Note(s.Text),
        UnknownItem x => Note($"({x.RawKind})"),
        _ => Note(""),
    };

    UIElement Call(ToolCallItem call)
    {
        var head = $"{call.Name}  {call.Summary}".TrimEnd() + (call.IsPending ? "  …" : call.IsError ? "  (error)" : "");
        return Fold("call:" + call.Id, head, () =>
        {
            var inner = new StackPanel { Margin = new Thickness(14, 0, 0, 4) };
            var result = call.Result ?? "(no result yet)";
            inner.Children.Add(Text(result.Length > MaxResult ? result[..MaxResult] + "\n… " + (result.Length - MaxResult) + " more characters" : result, call.IsError ? "textBrush" : "mutedBrush", mono: true));
            if (call.Children.Count > 0)
            {
                inner.Children.Add(Note($"{call.Children.Count} subagent items"));
                foreach (var child in call.Children.Take(60)) inner.Children.Add(Row(child));
            }
            return inner;
        });
    }

    /// <summary>A row folded until its header is clicked; the body is made on demand and remembered as open across rebuilds.</summary>
    UIElement Fold(string key, string header, Func<UIElement> body)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        var label = new TextBlock { Style = S("Text12"), Foreground = B("mutedBrush"), Cursor = Cursors.Hand, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = header };
        var content = new ContentPresenter();
        void Show(bool open)
        {
            label.Text = (open ? "▾ " : "▸ ") + header;
            content.Content = open ? body() : null;
        }
        label.MouseLeftButtonUp += (_, _) =>
        {
            var open = !opened.Remove(key);
            if (open) opened.Add(key);
            Show(open);
        };
        Show(opened.Contains(key));
        panel.Children.Add(label);
        panel.Children.Add(content);
        return panel;
    }

    static TextBlock Text(string text, string brush, bool mono = false)
    {
        var block = new TextBlock { Text = text, Style = S("Text13"), Foreground = B(brush), TextWrapping = TextWrapping.Wrap };
        if (mono) block.FontFamily = new FontFamily("Consolas");
        return block;
    }

    static TextBlock Note(string text) => new() { Text = text, Style = S("Text12"), Foreground = B("mutedBrush"), Margin = new Thickness(0, 4, 0, 4), FontStyle = FontStyles.Italic };
}
