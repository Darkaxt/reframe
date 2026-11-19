using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class OpenedThreadCheckTests
{
    [Fact]
    public void An_api_error_as_the_first_answer_is_a_failure()
    {
        var (state, error) = OpenedThreadCheck.Check(["""{"type":"user","message":{"content":"hi"}}""",
            """{"type":"assistant","isApiErrorMessage":true,"message":{"content":[{"type":"text","text":"API Error: 400 Unknown model \"gpt-6.1\"."}]}}"""]);
        Assert.Equal(OpenedThreadState.Failed, state);
        Assert.StartsWith("API Error: 400", error);
    }

    [Fact]
    public void Work_or_nothing_yet_is_not_a_failure()
    {
        Assert.Equal(OpenedThreadState.Waiting, OpenedThreadCheck.Check(["""{"type":"user","message":{"content":"hi"}}"""]).State);
        Assert.Equal(OpenedThreadState.Working, OpenedThreadCheck.Check(["""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Read","input":{}}]}}"""]).State);
    }

    const string Unknown = "API Error: 400 Unknown model \"gpt-6.1\". Supported: codex: gpt-5, gpt-6.1-sol-fast, gpt-6.1-sol, gpt-6.1-terra, gpt-6.2-astra; grok: grok-4";

    [Fact]
    public void The_closest_listed_model_replaces_an_unknown_one()
    {
        Assert.Equal("gpt-6.1-sol", OpenedThreadCheck.Substitute(Unknown, "gpt-6.1"));
        Assert.Null(OpenedThreadCheck.Substitute(Unknown, "gpt-9"));
        Assert.Null(OpenedThreadCheck.Substitute("API Error: 529 overloaded", "gpt-6.1"));
        Assert.Null(OpenedThreadCheck.Substitute(Unknown, null));
    }

    [Fact]
    public void The_first_prompt_is_the_first_user_message()
    {
        Assert.Equal("do it", OpenedThreadCheck.FirstPrompt(["""{"type":"mode"}""", """{"type":"user","message":{"content":"do it"}}""", """{"type":"user","message":{"content":"later"}}"""]));
        Assert.Equal("a", OpenedThreadCheck.FirstPrompt(["""{"type":"user","message":{"content":[{"type":"text","text":"a"}]}}"""]));
        Assert.Null(OpenedThreadCheck.FirstPrompt(["""{"type":"mode"}"""]));
    }

    [Fact]
    public void The_report_tells_the_opener_to_pass_a_model()
    {
        var text = OpenedThreadCheck.Report("worker", "gpt-6.1", "API Error: 400 Unknown model \"gpt-6.1\".");
        Assert.Contains("\"worker\"", text);
        Assert.Contains("`model`", text);
    }
}
