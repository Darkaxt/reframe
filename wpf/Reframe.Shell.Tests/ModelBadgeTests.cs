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

    [Fact]
    public void The_last_assistant_turn_of_the_transcript_tells_the_effort()
    {
        var tail = string.Join("\n",
            """ffort":"high","type":"assistant"} (a cut first line)""",
            """{"type":"assistant","effort":"high","message":{"model":"claude-opus-5-5"}}""",
            """{"type":"user","effort":"max","message":{}}""",
            """{"type":"assistant","effort":"low","perTurnEffort":"low","message":{"model":"claude-opus-5-5"}}""",
            """{"type":"attachment","attachment":{"type":"hook_success"}}""",
            "");
        Assert.Equal(new ModelInUse("claude-opus-5-5", "low"), ModelBadge.FromTranscriptTail(tail));
        Assert.Null(ModelBadge.FromTranscriptTail("""{"type":"assistant","message":{"model":"m"}}"""));
        // The status line's display name with the transcript's effort.
        Assert.Equal(new ModelInUse("Opus 5.5", "low"),
            ModelBadge.Combine(new ModelInUse("Opus 5.5", null), new ModelInUse("claude-opus-5-5", "low")));
        Assert.Equal(new ModelInUse("Opus 5.5", "high"), ModelBadge.Combine(new ModelInUse("Opus 5.5", "high"), null));
    }

    [Fact]
    public void Reads_the_tail_of_a_file_being_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelbadge-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 1000) + "\nlast");
                writer.Write(bytes);
                writer.Flush();
                Assert.Equal("x\nlast", ModelBadge.ReadTail(path, 6));
            }
            Assert.Null(ModelBadge.ReadTail(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
