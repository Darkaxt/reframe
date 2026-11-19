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

    [Fact]
    public void The_report_tells_the_opener_to_pass_a_model()
    {
        var text = OpenedThreadCheck.Report("worker", "gpt-6.1", "API Error: 400 Unknown model \"gpt-6.1\".");
        Assert.Contains("\"worker\"", text);
        Assert.Contains("`model`", text);
    }
}
