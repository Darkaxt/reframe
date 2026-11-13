using System.Text.Json.Nodes;
using System.Windows;
using Reframe.Shell;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The Overview pane built and drawn again and again from the capture fixture, never shown (the CI capture
/// crashed on the pane's second drawing: its status line still belonged to the first header).</summary>
public class OverviewPaneTests
{
    [Fact]
    public void The_pane_draws_again_after_each_refresh()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (var part in new[] { "Tokens", "Controls", "Forms" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri($"pack://application:,,,/Reframe.Shell;component/Theme/{part}.xaml"),
                        });
                }
                var file = Path.Combine(TokenTestsRoot.Root, "tools", "visual", "fixtures", "overview.json");
                var source = new FixtureOverviewSource(JsonNode.Parse(File.ReadAllText(file))!["overview"]!.AsObject(), new DateOnly(2026, 11, 13));
                var pane = new OverviewPane();
                pane.SetSource(source);
                pane.RefreshAsync().GetAwaiter().GetResult();
                source.Indexing = true;
                pane.RefreshAsync().GetAwaiter().GetResult();
                pane.Measure(new Size(1280, 760));
                pane.Arrange(new Rect(0, 0, 1280, 760));
                Assert.True(pane.Page.ActualHeight > 300);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
