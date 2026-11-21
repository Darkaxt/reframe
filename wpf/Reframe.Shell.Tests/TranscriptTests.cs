using System.Text;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class TranscriptReaderTests
{
    static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void A_long_transcript_is_read_from_its_end_from_a_whole_line()
    {
        var line = "{\"type\":\"user\",\"n\":\"" + new string('x', 1000) + "\"}";
        var text = string.Concat(Enumerable.Repeat(line + "\n", TranscriptReader.MaxRead / line.Length + 50));
        using var s = Bytes(text);
        var r = TranscriptReader.Read(s, 0);
        Assert.Equal(s.Length, r.NewOffset);
        Assert.True(r.Lines.Count <= TranscriptReader.MaxRead / (line.Length + 1));
        Assert.All(r.Lines, l => Assert.Equal(line, l));
    }

    [Fact]
    public void ReadsCompleteLinesAndIgnoresPartialLast()
    {
        using var s = Bytes("{\"a\":1}\n{\"b\":2}\n{\"c\":");
        var r = TranscriptReader.Read(s, 0);
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, r.Lines);
        Assert.Equal("{\"a\":1}\n{\"b\":2}\n".Length, r.NewOffset);
        Assert.False(r.WasReset);
    }

    [Fact]
    public void NoNewlineYieldsNothingAndKeepsOffset()
    {
        using var s = Bytes("{\"a\":");
        var r = TranscriptReader.Read(s, 0);
        Assert.Empty(r.Lines);
        Assert.Equal(0, r.NewOffset);
    }

    [Fact]
    public void AppendedLinesAreReadFromStoredOffset()
    {
        var path = Path.Combine(Path.GetTempPath(), "reframe-transcript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var file = Path.Combine(path, "t.jsonl");
            File.WriteAllText(file, "one\ntw");
            var r1 = TranscriptReader.Read(file, 0);
            Assert.Equal(new[] { "one" }, r1.Lines);
            File.AppendAllText(file, "o\nthree\r\n");
            var r2 = TranscriptReader.Read(file, r1.NewOffset);
            Assert.Equal(new[] { "two", "three" }, r2.Lines);
            var r3 = TranscriptReader.Read(file, r2.NewOffset);
            Assert.Empty(r3.Lines);
            Assert.Equal(r2.NewOffset, r3.NewOffset);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public void ShorterFileResetsToStart()
    {
        using var s = Bytes("new\n");
        var r = TranscriptReader.Read(s, 500);
        Assert.True(r.WasReset);
        Assert.Equal(new[] { "new" }, r.Lines);
        Assert.Equal(4, r.NewOffset);
    }

    [Fact]
    public void MissingFileReadsAsEmpty()
    {
        var r = TranscriptReader.Read(Path.Combine(Path.GetTempPath(), "reframe-missing-" + Guid.NewGuid().ToString("N") + ".jsonl"), 7);
        Assert.Empty(r.Lines);
        Assert.Equal(7, r.NewOffset);
    }

    [Fact]
    public void MultiByteCharactersSurvive()
    {
        using var s = Bytes("h\u00e9llo \u4e16\u754c\n");
        var r = TranscriptReader.Read(s, 0);
        Assert.Equal("h\u00e9llo \u4e16\u754c", Assert.Single(r.Lines));
    }
}

public class TranscriptModelTests
{
    static string User(string text, string uuid = "u1") =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    static string AssistantBlocks(string blocksJson, string uuid = "a1", string extra = "") =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\"" + extra + ",\"message\":{\"role\":\"assistant\",\"content\":[" + blocksJson + "]}}";

    static string ToolUse(string id, string name, string inputJson) =>
        "{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":" + inputJson + "}";

    static string Result(string id, string contentJson, bool error = false, string uuid = "r1") =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + id +
        "\",\"content\":" + contentJson + (error ? ",\"is_error\":true" : "") + "}]}}";

    static TranscriptModel Model(params string[] lines)
    {
        var m = new TranscriptModel();
        m.Append(lines);
        return m;
    }

    [Fact]
    public void UserStringAndArrayContent()
    {
        var m = Model(
            User("hello"),
            "{\"type\":\"user\",\"uuid\":\"u2\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"from array\"}]}}");
        Assert.Collection(m.Items,
            i => Assert.Equal("hello", Assert.IsType<UserMessageItem>(i).Text),
            i => Assert.Equal("from array", Assert.IsType<UserMessageItem>(i).Text));
    }

    [Fact]
    public void AssistantTextAndThinking()
    {
        var m = Model(AssistantBlocks("{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"text\",\"text\":\"answer\"}"));
        Assert.Collection(m.Items,
            i => Assert.Equal("hmm", Assert.IsType<ThinkingItem>(i).Text),
            i => Assert.Equal("answer", Assert.IsType<AssistantTextItem>(i).Text));
    }

    [Fact]
    public void AssistantStringContent()
    {
        var m = Model("{\"type\":\"assistant\",\"message\":{\"content\":\"plain\"}}");
        Assert.Equal("plain", Assert.IsType<AssistantTextItem>(Assert.Single(m.Items)).Text);
    }

    [Fact]
    public void ToolResultPairsWithCallAndProducesNoUserMessage()
    {
        var m = Model(
            AssistantBlocks(ToolUse("t1", "Bash", "{\"command\":\"ls -la\"}")),
            Result("t1", "\"file1\\nfile2\""));
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(m.Items));
        var call = Assert.Single(group.Calls);
        Assert.Equal("Bash", call.Name);
        Assert.Equal("ls -la", call.Summary);
        Assert.Equal("file1\nfile2", call.Result);
        Assert.False(call.IsError);
        Assert.False(call.IsPending);
    }

    [Fact]
    public void ResultInLaterAppendUpdatesPendingCall()
    {
        var m = new TranscriptModel();
        m.Append(new[] { AssistantBlocks(ToolUse("t1", "Read", "{\"file_path\":\"C:\\\\x.txt\"}")) });
        var call = Assert.Single(Assert.IsType<ToolGroupItem>(Assert.Single(m.Items)).Calls);
        Assert.True(call.IsPending);
        Assert.Null(call.Result);

        m.Append(new[] { Result("t1", "[{\"type\":\"text\",\"text\":\"line1\"},{\"type\":\"text\",\"text\":\"line2\"}]") });
        var again = Assert.Single(Assert.IsType<ToolGroupItem>(Assert.Single(m.Items)).Calls);
        Assert.Equal("line1\nline2", again.Result);
        Assert.Equal("C:\\x.txt", again.Summary);
    }

    [Fact]
    public void ErrorResultIsFlagged()
    {
        var m = Model(
            AssistantBlocks(ToolUse("t1", "Bash", "{\"command\":\"false\"}")),
            Result("t1", "\"boom\"", error: true));
        var call = Assert.Single(Assert.IsType<ToolGroupItem>(Assert.Single(m.Items)).Calls);
        Assert.True(call.IsError);
        Assert.Equal("boom", call.Result);
    }

    [Fact]
    public void ResultBeforeCallStillPairs()
    {
        var m = Model(
            Result("t9", "\"early\""),
            AssistantBlocks(ToolUse("t9", "Bash", "{\"command\":\"x\"}")));
        var call = Assert.Single(Assert.IsType<ToolGroupItem>(Assert.Single(m.Items)).Calls);
        Assert.Equal("early", call.Result);
    }

    [Fact]
    public void GroupingLabels()
    {
        Assert.Equal("Ran 3 commands", LabelFor("Bash", "Bash", "Bash"));
        Assert.Equal("Ran 1 command", LabelFor("Bash"));
        Assert.Equal("Edited 2 files", LabelFor("Edit", "Write"));
        Assert.Equal("Edited 2 files", LabelFor("MultiEdit", "NotebookEdit"));
        Assert.Equal("Read 4 files", LabelFor("Read", "Read", "Read", "Read"));
        Assert.Equal("Searched 2 times", LabelFor("Grep", "Glob"));
        Assert.Equal("Used 3 tools", LabelFor("Bash", "Read", "Edit"));
        Assert.Equal("Used 2 tools", LabelFor("WebFetch", "TodoWrite"));
    }

    static string LabelFor(params string[] names)
    {
        var blocks = string.Join(",", names.Select((n, i) => ToolUse("t" + i, n, "{}")));
        var m = Model(AssistantBlocks(blocks));
        return Assert.IsType<ToolGroupItem>(Assert.Single(m.Items)).Label;
    }

    [Fact]
    public void TextBetweenToolCallsSplitsGroups()
    {
        var m = Model(
            AssistantBlocks(ToolUse("a", "Bash", "{}"), "a1"),
            AssistantBlocks("{\"type\":\"text\",\"text\":\"middle\"}", "a2"),
            AssistantBlocks(ToolUse("b", "Bash", "{}"), "a3"));
        Assert.Collection(m.Items,
            i => Assert.IsType<ToolGroupItem>(i),
            i => Assert.IsType<AssistantTextItem>(i),
            i => Assert.IsType<ToolGroupItem>(i));
    }

    [Fact]
    public void CompactionInAllThreeForms()
    {
        var m = Model(
            "{\"type\":\"summary\",\"summary\":\"earlier work\",\"leafUuid\":\"x\"}",
            "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"uuid\":\"s1\",\"content\":\"Conversation compacted\"}",
            "{\"type\":\"user\",\"uuid\":\"u5\",\"isCompactSummary\":true,\"message\":{\"role\":\"user\",\"content\":\"This session is continued\"}}");
        Assert.Equal(3, m.Items.Count);
        Assert.All(m.Items, i => Assert.IsType<CompactionItem>(i));
    }

    [Fact]
    public void SystemNoticeAndUnknownTypes()
    {
        var m = Model(
            "{\"type\":\"system\",\"subtype\":\"info\",\"content\":\"heads up\"}",
            "{\"type\":\"file-history-snapshot\",\"messageId\":\"m\"}",
            "{\"message\":1}");
        Assert.Collection(m.Items,
            i => Assert.Equal("heads up", Assert.IsType<SystemNoticeItem>(i).Text),
            i => Assert.Equal("file-history-snapshot", Assert.IsType<UnknownItem>(i).RawKind),
            i => Assert.Equal("(none)", Assert.IsType<UnknownItem>(i).RawKind));
    }

    [Fact]
    public void UnknownBlockAndOddContentNeverThrow()
    {
        var m = Model(
            AssistantBlocks("{\"type\":\"server_tool_use\",\"x\":1}"),
            "{\"type\":\"assistant\",\"message\":{\"content\":42}}",
            "{\"type\":\"user\",\"message\":null}",
            "{\"type\":\"assistant\"}");
        Assert.Equal("server_tool_use", Assert.IsType<UnknownItem>(m.Items[0]).RawKind);
        Assert.Equal("assistant", Assert.IsType<UnknownItem>(m.Items[1]).RawKind);
        Assert.Equal(0, m.MalformedLines);
    }

    [Fact]
    public void MalformedLinesAreSkippedAndCounted()
    {
        var m = Model("{not json", User("ok"), "[1,2]", "   ");
        Assert.Equal("ok", Assert.IsType<UserMessageItem>(Assert.Single(m.Items)).Text);
        Assert.Equal(2, m.MalformedLines);
    }

    [Fact]
    public void SidechainNestsUnderTaskCall()
    {
        var m = Model(
            AssistantBlocks(ToolUse("task1", "Task", "{\"description\":\"dig\",\"prompt\":\"go\"}"), "a1"),
            "{\"type\":\"user\",\"uuid\":\"s1\",\"parentUuid\":\"a1\",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"sub prompt\"}}",
            AssistantBlocks("{\"type\":\"text\",\"text\":\"sub answer\"}", "s2", ",\"parentUuid\":\"s1\",\"isSidechain\":true"),
            Result("task1", "\"done\""));
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(m.Items));
        var call = Assert.Single(group.Calls);
        Assert.Equal("dig", call.Summary);
        Assert.Equal("done", call.Result);
        Assert.Collection(call.Children,
            c => Assert.Equal("sub prompt", Assert.IsType<UserMessageItem>(c).Text),
            c => Assert.Equal("sub answer", Assert.IsType<AssistantTextItem>(c).Text));
        Assert.Empty(m.SidechainItems);
    }

    [Fact]
    public void OrphanSidechainGoesToFlatList()
    {
        var m = Model(
            User("top"),
            "{\"type\":\"user\",\"uuid\":\"s1\",\"parentUuid\":\"nowhere\",\"isSidechain\":true,\"message\":{\"content\":\"lost\"}}");
        Assert.Single(m.Items);
        Assert.Equal("lost", Assert.IsType<UserMessageItem>(Assert.Single(m.SidechainItems)).Text);
    }

    [Fact]
    public void ResetClearsState()
    {
        var m = Model(User("a"), "{bad");
        m.Reset();
        Assert.Empty(m.Items);
        Assert.Equal(0, m.MalformedLines);
    }
}
