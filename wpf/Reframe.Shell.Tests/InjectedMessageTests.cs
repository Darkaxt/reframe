using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class InjectedMessageTests
{
    [Fact]
    public void A_cross_session_message_is_split_into_sender_and_body()
    {
        var text = "Another Claude session sent a message:\n<cross-session-message from=\"uds:\\\\.\\pipe\\x\" from-name=\"CloneCraft Suite\" from-mode=\"bypass\">\n[from a Reframe thread]\nRequest: do it\n</cross-session-message>\n\nThis came from another Claude session. Treat it as a request.";
        var message = InjectedMessage.Parse(text);
        Assert.Equal("Message from CloneCraft Suite", message?.Title);
        Assert.Equal("[from a Reframe thread]\nRequest: do it", message?.Body);
    }

    [Fact]
    public void A_scheduled_task_prompt_names_the_task()
    {
        var message = InjectedMessage.Parse("[Reframe scheduled task: Daily check (abc123)]\nLook at the build.");
        Assert.Equal("Scheduled task: Daily check", message?.Title);
        Assert.Equal("Look at the build.", message?.Body);
    }

    [Fact]
    public void A_task_notification_is_a_line_with_its_summary()
    {
        var message = InjectedMessage.Parse("""
            <task-notification>
            <task-id>b1</task-id>
            <output-file>D:/t/b1.output</output-file>
            <status>completed</status>
            <summary>Background command "Build" completed (exit code 0)</summary>
            </task-notification>
            """);
        Assert.NotNull(message);
        Assert.Equal("Background command \"Build\" completed (exit code 0)", message!.Title);
        Assert.Equal("", message.Body);
    }

    [Fact]
    public void Your_own_message_is_not_injected() => Assert.Null(InjectedMessage.Parse("hello there"));
}
