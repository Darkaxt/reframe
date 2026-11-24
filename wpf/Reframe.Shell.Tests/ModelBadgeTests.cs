using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The tile header's model and effort (ModelBadge): from the status line, else the launch choice.</summary>
public class ModelBadgeTests
{
    [Fact]
    public void Reads_the_model_and_effort_of_the_status_line()
    {
        var payload = JsonNode.Parse("""{ "model": { "id": "claude-opus-5-5", "display_name": "Opus 5.5" }, "effort": { "level": "low" }, "thinking": { "enabled": true } }""");
        Assert.Equal(new ModelInUse("Opus 5.5", "low"), ModelBadge.FromStatusLine(payload));
        Assert.Equal(new ModelInUse("claude-x", null), ModelBadge.FromStatusLine(JsonNode.Parse("""{ "model": { "id": "claude-x" } }""")));
        Assert.Null(ModelBadge.FromStatusLine(JsonNode.Parse("""{ "rate_limits": {} }""")));
        Assert.Null(ModelBadge.FromStatusLine(null));
    }

    [Fact]
    public void The_report_wins_over_the_launch_choice_and_low_effort_stands_out()
    {
        var shown = ModelBadge.Describe(new ModelInUse("Opus 5.5", "low"), "sonnet", "high")!.Value;
        Assert.Equal("Opus 5.5 · low effort", shown.Text);
        Assert.True(shown.Low);
        Assert.Contains("reports", shown.Tooltip);
        // A report without an effort (a proxied model) keeps the launch effort, and says so.
        var proxied = ModelBadge.Describe(new ModelInUse("gpt-6.1-sol", null), "gpt-6.1-sol", "high")!.Value;
        Assert.Equal("gpt-6.1-sol · high effort", proxied.Text);
        Assert.Contains("effort as launched", proxied.Tooltip);
        Assert.Equal("Haiku", ModelBadge.Describe(new ModelInUse("Haiku", null), null, null)!.Value.Text);
    }

    [Fact]
    public void Before_a_report_the_launch_choice_shows_and_nothing_known_hides_it()
    {
        var launched = ModelBadge.Describe(null, "gpt-6.1-sol", "xhigh")!.Value;
        Assert.Equal("gpt-6.1-sol · xhigh effort", launched.Text);
        Assert.False(launched.Low);
        Assert.Contains("as launched", launched.Tooltip);
        Assert.Equal("default model · low effort", ModelBadge.Describe(null, null, "low")!.Value.Text);
        Assert.Null(ModelBadge.Describe(null, null, null));
    }
}
