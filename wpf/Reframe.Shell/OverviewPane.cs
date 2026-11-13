using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// The Overview pane (backlog #14), GitHub's profile overview made of thread activity: the contribution calendar
/// (metric and year switches, total line, Less→More legend, month and day labels, streaks), the most active threads,
/// the activity breakdown and the monthly timeline. Everything comes from the host's <c>overview</c> index as it is
/// (<see cref="IOverviewSource"/>); the pane reads it when shown and, only while the host is still indexing, again every
/// 30 s while it stays shown. No prompt text, only titles.
/// </summary>
public sealed class OverviewPane : Border
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>GitHub's 10 px squares 3 apart, a little larger for Reframe's 13 px text.</summary>
    const double Cell = 11, Gap = 3;
    static readonly TimeSpan WhileIndexing = TimeSpan.FromSeconds(30);

    /// <summary>The accent scale: the accent over the background at 25/45/70/100 %, level 0 the raised surface.</summary>
    public static readonly Color[] Levels = BuildLevels();

    static Color[] BuildLevels()
    {
        var bg = (Color)Application.Current.FindResource("bg");
        var accent = (Color)Application.Current.FindResource("accent");
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        Color At(double t) => Color.FromRgb(Mix(bg.R, accent.R, t), Mix(bg.G, accent.G, t), Mix(bg.B, accent.B, t));
        return [(Color)Application.Current.FindResource("surface-2"), At(0.25), At(0.45), At(0.7), At(1)];
    }

    readonly StackPanel body = new() { MaxWidth = 980, Margin = new Thickness(24, 20, 24, 24) };
    readonly DispatcherTimer timer = new() { Interval = WhileIndexing };
    IOverviewSource? source;
    OverviewMetric metric = OverviewMetric.Prompts;
    int? year;
    OverviewSummary? summary;
    OverviewBreakdown? breakdown;
    IReadOnlyList<OverviewThread> threads = [];
    IReadOnlyList<OverviewMonth> months = [];
    OverviewStatus status = OverviewStatus.Unconfigured;
    string? error;
    bool loading;

    /// <summary>Whether a thread has a session in this shell to bring forward (else its card is disabled).</summary>
    public Func<string, bool> CanOpen { get; set; } = _ => false;
    /// <summary>The creation times of the shell's open sessions (the timeline's "still open").</summary>
    public Func<IEnumerable<double>> OpenCreatedAt { get; set; } = () => [];
    public event Action<OverviewThread>? ThreadOpened;

    /// <summary>The page (inside the scroll viewer), for the capture.</summary>
    public FrameworkElement Page => body;

    public OverviewPane()
    {
        Background = B("bgBrush");
        Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        AutomationProperties.SetName(this, "Overview");
        timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) await RefreshAsync();
            else timer.Stop();
        };
        Render();
    }

    public void SetSource(IOverviewSource? overview) => source = overview;

    /// <summary>Reads status, summary, threads, breakdown and timeline (the year's where it applies) and draws them.</summary>
    public async Task RefreshAsync()
    {
        if (loading) return;
        loading = true;
        try
        {
            if (source is null)
            {
                error = "The Overview needs the Rust terminal host.";
                return;
            }
            var today = DateOnly.FromDateTime(DateTime.Now);
            var range = year is { } y ? new System.Text.Json.Nodes.JsonObject { ["year"] = y } : new System.Text.Json.Nodes.JsonObject();
            status = OverviewStatus.Parse(await source.CallAsync("status"));
            summary = OverviewSummary.Parse(await source.CallAsync("summary", (System.Text.Json.Nodes.JsonObject)range.DeepClone()), today);
            breakdown = OverviewBreakdown.Parse(await source.CallAsync("breakdown", (System.Text.Json.Nodes.JsonObject)range.DeepClone()));
            threads = ((await source.CallAsync("threads", new() { ["limit"] = 6 }))?["threads"]?.AsArray() ?? [])
                .Where(n => n is not null).Select(n => OverviewThread.Parse(n!)).ToList();
            months = ((await source.CallAsync("timeline"))?["months"]?.AsArray() ?? [])
                .Where(n => n is not null).Select(n => OverviewMonth.Parse(n!)).Take(6).ToList();
            error = null;
        }
        catch (HostServiceException e)
        {
            error = $"The activity index did not answer: {e.Message}";
        }
        finally
        {
            loading = false;
            Render();
            // Low frequency, and only while the host is still reading and the pane is shown.
            if (IsVisible && status.Working && error is null) timer.Start();
            else timer.Stop();
        }
    }

    void Render()
    {
        body.Children.Clear();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = true };
        var title = new TextBlock { Text = "Overview", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        // A new line each time: the old one still belongs to the previous header.
        var statusLine = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis };
        statusLine.Text = error ?? Overview.StatusLine(status) ?? (status.LastPassDay is { } day ? $"Up to date as of {day} · refreshed once a day" : "");
        statusLine.Foreground = error is not null ? B("dangerBrush") : B("mutedBrush");
        head.Children.Add(statusLine);
        body.Children.Add(head);
        if (summary is null) return;

        body.Children.Add(ActivityCard(summary));
        var columns = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var threadsCard = ThreadsSection();
        columns.Children.Add(threadsCard);
        var breakdownCard = BreakdownSection();
        Grid.SetColumn(breakdownCard, 2);
        columns.Children.Add(breakdownCard);
        body.Children.Add(columns);
        body.Children.Add(TimelineSection());
    }

    static Border Card(UIElement child) => new()
    {
        Background = B("surfaceBrush"), BorderBrush = B("borderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = (CornerRadius)Application.Current.FindResource("radiusCorner"), Padding = new Thickness(16), Child = child,
    };

    static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
    };

    static TextBlock Muted(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = B("mutedBrush") };

    /// <summary>A segmented switch of the toolbar's look (accent border on the chosen one).</summary>
    static StackPanel Segmented<T>(string name, IEnumerable<(T Value, string Text)> options, T chosen, Action<T> choose)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(panel, name);
        var list = options.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var (value, text) = list[i];
            var on = EqualityComparer<T>.Default.Equals(value, chosen);
            var button = new Button
            {
                Style = S("ToolbarButton"), Content = new TextBlock { Text = text, Style = S("Text12") }, Padding = new Thickness(10, 3, 10, 3),
                BorderBrush = on ? B("accentBrush") : B("border-strongBrush"), Margin = new Thickness(i == 0 ? 0 : -1, 0, 0, 0),
                Tag = new CornerRadius(i == 0 ? 6 : 0, i == list.Count - 1 ? 6 : 0, i == list.Count - 1 ? 6 : 0, i == 0 ? 6 : 0),
            };
            AutomationProperties.SetName(button, text);
            Panel.SetZIndex(button, on ? 1 : 0);
            button.Click += (_, _) => choose(value);
            panel.Children.Add(button);
        }
        return panel;
    }

    // ---------- (1) the contribution calendar and (5) streaks ----------

    Border ActivityCard(OverviewSummary data)
    {
        var heatmap = Overview.BuildHeatmap(data, metric, year);
        var stack = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var years = Segmented<int?>("Year", new (int?, string)[] { (null, "Last year") }.Concat(data.Years.Select(y => ((int?)y, y.ToString()))), year, async y =>
        {
            year = y;
            await RefreshAsync();
        });
        DockPanel.SetDock(years, Dock.Right);
        top.Children.Add(years);
        var metrics = Segmented<OverviewMetric>("Metric", [(OverviewMetric.Prompts, "Prompts"), (OverviewMetric.Sessions, "Sessions"), (OverviewMetric.ActiveTime, "Active time")], metric, m =>
        {
            metric = m;
            Render();
        });
        metrics.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(metrics, Dock.Right);
        top.Children.Add(metrics);
        top.Children.Add(new TextBlock { Text = heatmap.Total, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(top);
        stack.Children.Add(Calendar(heatmap));

        var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(Muted("Less", 11));
        foreach (var color in Levels)
            legend.Children.Add(new Rectangle { Width = Cell, Height = Cell, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(color), Margin = new Thickness(Gap, 0, 0, 0) });
        legend.Children.Add(new TextBlock { Text = "More", FontSize = 11, Foreground = B("mutedBrush"), Margin = new Thickness(Gap + 1, 0, 0, 0) });
        AutomationProperties.SetName(legend, "Less to more");
        DockPanel.SetDock(legend, Dock.Right);
        foot.Children.Add(legend);
        var streaks = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        streaks.Inlines.Add(new System.Windows.Documents.Run("Current streak ") { Foreground = B("mutedBrush") });
        streaks.Inlines.Add(new System.Windows.Documents.Run(Overview.Streak(data.CurrentStreak)) { FontWeight = FontWeights.SemiBold });
        streaks.Inlines.Add(new System.Windows.Documents.Run("    Longest streak ") { Foreground = B("mutedBrush") });
        streaks.Inlines.Add(new System.Windows.Documents.Run(Overview.Streak(data.LongestStreak)) { FontWeight = FontWeights.SemiBold });
        foot.Children.Add(streaks);
        stack.Children.Add(foot);
        return Card(stack);
    }

    static FrameworkElement Calendar(Heatmap heatmap)
    {
        const double Left = 30, Top = 16, Step = Cell + Gap;
        var canvas = new Canvas { Width = Left + heatmap.Weeks.Count * Step, Height = Top + 7 * Step, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var label in heatmap.Months)
        {
            var text = new TextBlock { Text = label.Text, FontSize = 11, Foreground = B("mutedBrush") };
            Canvas.SetLeft(text, Left + label.Week * Step);
            Canvas.SetTop(text, -1);
            canvas.Children.Add(text);
        }
        foreach (var (row, name) in new[] { (1, "Mon"), (3, "Wed"), (5, "Fri") })
        {
            var text = new TextBlock { Text = name, FontSize = 11, Foreground = B("mutedBrush") };
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, Top + row * Step - 3);
            canvas.Children.Add(text);
        }
        var fills = Levels.Select(c => (Brush)new SolidColorBrush(c)).ToArray();
        for (var w = 0; w < heatmap.Weeks.Count; w++)
            for (var d = 0; d < 7; d++)
            {
                if (heatmap.Weeks[w].Days[d] is not { } cell) continue;
                var square = new Rectangle { Width = Cell, Height = Cell, RadiusX = 2, RadiusY = 2, Fill = fills[cell.Level], ToolTip = cell.Tooltip };
                AutomationProperties.SetName(square, cell.Tooltip);
                Canvas.SetLeft(square, Left + w * Step);
                Canvas.SetTop(square, Top + d * Step);
                canvas.Children.Add(square);
            }
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = canvas, Focusable = false };
    }

    // ---------- (2) most active threads ----------

    FrameworkElement ThreadsSection()
    {
        var stack = new StackPanel();
        stack.Children.Add(Heading("Most active threads"));
        if (threads.Count == 0) stack.Children.Add(Muted("No threads yet."));
        var grid = new UniformGrid { Columns = 2 };
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        foreach (var thread in threads)
        {
            var canOpen = CanOpen(thread.SessionId);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = Overview.ThreadTitle(thread), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B("textBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var folder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            folder.Children.Add(new VectorIcon { Kind = "folder", Size = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = B("mutedBrush") });
            folder.Children.Add(new TextBlock { Text = Overview.FolderName(thread.Folder), FontSize = 12, Foreground = B("mutedBrush"), Margin = new Thickness(5, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(folder);
            var meta = new StackPanel { Orientation = Orientation.Horizontal };
            meta.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(thread.Provider == "chatgpt" ? "providerChatgptBrush" : "providerAnthropicBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            meta.Children.Add(Muted(Overview.ThreadMeta(thread, now), 11));
            content.Children.Add(meta);
            var card = new Button
            {
                Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 10, 12, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsEnabled = canOpen, Template = CardTemplate(), Cursor = canOpen ? Cursors.Hand : null,
                ToolTip = (thread.Folder ?? "") + (canOpen ? "\nClick to show its session" : "\nResuming a past thread is not available in this shell yet"),
            };
            ToolTipService.SetShowOnDisabled(card, true);
            AutomationProperties.SetName(card, Overview.ThreadTitle(thread));
            card.Click += (_, _) => ThreadOpened?.Invoke(thread);
            grid.Children.Add(card);
        }
        stack.Children.Add(grid);
        return stack;
    }

    /// <summary>A card button: surface, border, the accent border on hover; a disabled one is just not highlighted.</summary>
    static ControlTemplate CardTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Card");
        border.SetValue(Border.BackgroundProperty, B("surfaceBrush"));
        border.SetValue(Border.BorderBrushProperty, B("borderBrush"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, Application.Current.FindResource("radiusCorner"));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new MultiTrigger();
        hover.Conditions.Add(new System.Windows.Condition(UIElement.IsMouseOverProperty, true));
        hover.Conditions.Add(new System.Windows.Condition(UIElement.IsEnabledProperty, true));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, B("accentBrush"), "Card"));
        template.Triggers.Add(hover);
        return template;
    }

    // ---------- (3) activity breakdown ----------

    FrameworkElement BreakdownSection()
    {
        var stack = new StackPanel();
        stack.Children.Add(Heading("Activity breakdown"));
        var inner = new StackPanel();
        if (breakdown is { } data)
        {
            Group(inner, "Provider", Overview.Bars(data.Providers, Overview.ProviderLabel, 3), "turns");
            Group(inner, "Model", Overview.Bars(data.Models, m => m, 4), "turns");
            Group(inner, "Tools", Overview.ToolBars(data.Tools), "uses");
        }
        stack.Children.Add(Card(inner));
        return stack;
    }

    static void Group(StackPanel into, string name, IReadOnlyList<BreakdownBar> bars, string unit)
    {
        into.Children.Add(new TextBlock { Text = name.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B("mutedBrush"), Margin = new Thickness(0, into.Children.Count == 0 ? 0 : 10, 0, 4) });
        if (bars.Count == 0) into.Children.Add(Muted("Nothing yet", 11));
        foreach (var bar in bars)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, ToolTip = $"{bar.Label}: {bar.Count:N0} {unit}" };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.Children.Add(new TextBlock { Text = bar.Label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var fill = new Border { Background = B("accentBrush"), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = B("surface-2Brush"), Child = fill, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            var share = bar.Share;
            track.SizeChanged += (_, _) => fill.Width = Math.Round(track.ActualWidth * share);
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            var percent = new TextBlock { Text = $"{Math.Round(share * 100)}%", FontSize = 11, Foreground = B("mutedBrush"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(percent, 2);
            row.Children.Add(percent);
            AutomationProperties.SetName(row, $"{bar.Label} {Math.Round(share * 100)}%");
            into.Children.Add(row);
        }
    }

    // ---------- (4) monthly timeline ----------

    FrameworkElement TimelineSection()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(Heading("Activity timeline"));
        if (months.Count == 0) stack.Children.Add(Muted("No activity yet."));
        var open = OpenCreatedAt().ToList();
        foreach (var month in months)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            // The timeline's line and dot (GitHub's contribution activity).
            row.Children.Add(new Rectangle { Width = 2, Fill = B("borderBrush"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0) });
            row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = B("surface-2Brush"), Stroke = B("accentBrush"), StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) });
            var text = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock { Text = Overview.MonthTitle(month.Month), FontSize = 13, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = Overview.MonthLine(month, Overview.StillOpen(month.Month, open)), FontSize = 12, Margin = new Thickness(0, 2, 0, 2) });
            var folders = string.Join("  ·  ", month.Folders.Take(4).Select(f => $"{Overview.FolderName(f.Folder)} ({f.Sessions:N0})"));
            text.Children.Add(Muted($"{Overview.Amount(OverviewMetric.Prompts, month.Prompts)}, {Overview.Duration(month.ActiveMinutes)} active" + (folders.Length > 0 ? $"  —  {folders}" : ""), 11));
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        return stack;
    }
}
