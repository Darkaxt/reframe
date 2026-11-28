using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ThreadDigestTests
{
    static string User(string text, string uuid) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    static string Assistant(string blocksJson, string uuid) =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"assistant\",\"content\":[" + blocksJson + "]}}";

    static string Text(string text) => "{\"type\":\"text\",\"text\":\"" + text + "\"}";

    static string ToolUse(string id, string command) =>
        "{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"Bash\",\"input\":{\"command\":\"" + command + "\"}}";

    static string Result(string id, string text, string uuid) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + id +
        "\",\"content\":\"" + text + "\"}]}}";

    static IReadOnlyList<ChatItem> Items(params string[] lines)
    {
        var model = new TranscriptModel();
        model.Append(lines);
        return model.Items;
    }

    [Fact]
    public void Shows_the_last_turns_with_their_tools()
    {
        var items = Items(
            User("first task", "u1"), Assistant(Text("done one"), "a1"),
            User("second task", "u2"), Assistant(ToolUse("t1", "cargo test") + "," + Text("running tests"), "a2"),
            Result("t1", "12 passed", "r1"), Assistant(Text("all green"), "a3"));
        var digest = ThreadDigest.Render(items, 1);
        Assert.DoesNotContain("first task", digest);
        Assert.Contains("### User\nsecond task", digest);
        Assert.Contains("- Bash: cargo test", digest);
        Assert.Contains("12 passed", digest);
        Assert.Contains("all green", digest);
        Assert.Contains("first task", ThreadDigest.Render(items, 2));
    }

    [Fact]
    public void Keeps_within_its_size_leaving_out_the_oldest()
    {
        var big = new string('x', 3_900);
        var lines = Enumerable.Range(0, 40).SelectMany(i => new[] { User("task " + i, "u" + i), Assistant(Text(big), "a" + i) }).ToArray();
        var digest = ThreadDigest.Render(Items(lines), ThreadDigest.MaxTurns);
        Assert.True(digest.Length <= ThreadDigest.MaxChars);
        Assert.Contains("task 39", digest);
        Assert.DoesNotContain("task 0\n", digest);
    }
}
