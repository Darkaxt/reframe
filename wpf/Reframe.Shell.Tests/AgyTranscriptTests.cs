using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>A Gemini thread's transcript (Antigravity CLI's transcript_full.jsonl) in the Chat view.</summary>
public class AgyTranscriptTests
{
    static readonly string[] Lines =
    [
        """{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-11-21T15:32:55Z","content":"<USER_REQUEST>\nRead a.txt, then write b.txt.\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: x.\n</ADDITIONAL_METADATA>"}""",
        """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","model":"gemini-3.8-flash-low","tool_calls":[{"name":"read_file","args":{"Path":"D:\\p\\a.txt"}},{"name":"write_file","args":{"Content":"HELLO\n","Path":"D:\\p\\b.txt"}}]}""",
        """{"step_index":2,"source":"MODEL","type":"GENERIC","status":"DONE","content":"Created At: 2026-11-21T16:32:57+01:00\nCompleted At: 2026-11-21T16:32:57+01:00\nShowing lines 1-1 of 1\n1: hello"}""",
        """{"step_index":3,"source":"MODEL","type":"GENERIC","status":"ERROR","error":"access denied","content":"Created At: x"}""",
        """{"step_index":4,"source":"SYSTEM","type":"SYSTEM_MESSAGE","status":"DONE","content":"<SYSTEM_MESSAGE>\nReminder\n</SYSTEM_MESSAGE>"}""",
        """{"step_index":5,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","model":"gemini-3.8-flash-low","content":"done."}""",
    ];

    [Fact]
    public void Its_steps_become_messages_and_tool_calls()
    {
        var model = new TranscriptModel();
        model.Append(Lines);
        var top = model.Items.SelectMany(i => i is ToolGroupItem g ? g.Calls.Cast<ChatItem>() : new[] { i }).ToList();
        Assert.Equal("Read a.txt, then write b.txt.", Assert.IsType<UserMessageItem>(top[0]).Text);
        var read = Assert.IsType<ToolCallItem>(top[1]);
        Assert.Equal(("Read", @"D:\p\a.txt", "Showing lines 1-1 of 1\n1: hello", false), (read.Name, read.Summary, read.Result, read.IsError));
        var write = Assert.IsType<ToolCallItem>(top[2]);
        Assert.Equal(("Write", true), (write.Name, write.IsError));
        Assert.Equal(new[] { "+HELLO" }, write.Diff);
        Assert.StartsWith("access denied", write.Result);
        Assert.Equal("done.", Assert.IsType<AssistantTextItem>(top[3]).Text);
        Assert.Equal(4, top.Count);
        Assert.Equal(0, model.MalformedLines);
    }

    [Fact]
    public void Its_model_is_read_from_the_newest_step()
    {
        var file = Path.Combine(Path.GetTempPath(), "reframe-agy-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(file, Lines);
            Assert.Equal("gemini-3.8-flash-low", ClaudeRegistry.LastModelIn(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Its_transcript_is_found_under_agys_home()
    {
        var home = Path.Combine(Path.GetTempPath(), "reframe-agy-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logs = Path.Combine(home, "brain", "c1", ".system_generated", "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "transcript_full.jsonl"), "");
            Assert.Equal(Path.Combine(logs, "transcript_full.jsonl"), Antigravity.TranscriptFile("c1", home));
            Assert.Null(Antigravity.TranscriptFile("c2", home));
            Assert.Null(Antigravity.TranscriptFile("", home));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }
}
