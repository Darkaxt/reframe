using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// The Overview (backlog #14): a toolbar button beside Sessions that shows the Overview pane over the layout, and the
/// host's <c>overview</c> index configured once attached as the primary client (the cache in the profile, Claude Code's
/// home), done by ShellController once attached.
/// </summary>
public partial class MainWindow
{
    readonly OverviewPane overview = new() { Visibility = Visibility.Collapsed };
    Button? overviewButton;

    Button OverviewButton()
    {
        // A small calendar glyph of four squares (none of the Electron icons fits; icons.tsx has no chart).
        var glyph = new UniformGrid { Rows = 2, Columns = 2, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var level in new[] { 4, 2, 1, 3 })
            glyph.Children.Add(new Rectangle { Fill = new SolidColorBrush(OverviewPane.Levels[level]), Margin = new Thickness(0.75), RadiusX = 1, RadiusY = 1 });
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock { Text = "Overview", Style = S("Text13"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Style = S("ToolbarButton"), Content = content, ToolTip = "Overview: your thread activity", Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(button, "Overview");
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => ShowOverview(overview.Visibility != Visibility.Visible);
        overviewButton = button;
        return button;
    }

    void AddOverview(Grid workspace)
    {
        Grid.SetColumn(overview, 1);
        Panel.SetZIndex(overview, 3);
        workspace.Children.Add(overview);
        overview.CanOpen = claudeSessionId => SessionOfThread(claudeSessionId) is { } id && CanShow(id);
        overview.OpenCreatedAt = () => state.Sessions.Values.Where(s => s.Lifecycle == "open").Select(s => s.CreatedAt);
        overview.ThreadOpened += thread =>
        {
            if (SessionOfThread(thread.SessionId) is not { } id) return;
            ShowOverview(false);
            ShowSession(id);
        };
    }

    /// <summary>The reframe session (if any) whose Claude Code thread this is.</summary>
    string? SessionOfThread(string claudeSessionId) =>
        state.Sessions.Values.FirstOrDefault(s => s.ClaudeSessionId == claudeSessionId && s.Lifecycle == "open")?.Id;

    void ShowOverview(bool show)
    {
        // The host's index (a capture sets its fixture's instead).
        if (show && fixture is null) overview.SetSource(services?.Overview);
        overview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (overviewButton is not null) overviewButton.BorderBrush = show ? B("accentBrush") : B("border-strongBrush");
    }

    /// <summary>--diag-capture with tools/visual/fixtures/overview.json: the pane from the fixture's answers, complete
    /// ("overview") and while the first pass runs ("overview-indexing"). No host, no transcripts.</summary>
    async Task CaptureOverviewAsync(HwndSource popup, string dir, string fixtureFile)
    {
        var source = new FixtureOverviewSource(JsonNode.Parse(File.ReadAllText(fixtureFile))!["overview"]!.AsObject(), DateOnly.FromDateTime(DateTime.Now));
        overview.SetSource(source);
        ShowOverview(true);
        await overview.RefreshAsync();
        await DiagCapture.SettleAsync(Dispatcher, 500);
        DiagCapture.Save(popup, root, dir, "overview");
        source.Indexing = true;
        await overview.RefreshAsync();
        await DiagCapture.SettleAsync(Dispatcher, 500);
        DiagCapture.Save(popup, root, dir, "overview-indexing");
    }
}
