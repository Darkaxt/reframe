using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class MonitorWarningTests
{
    [Fact]
    public void ReadsTheHostsWarnings()
    {
        var result = JsonNode.Parse("""
            { "warnings": [
              { "id": "s1:7:99:scan", "sessionId": "s1", "kind": "scan", "pid": 7, "name": "grep.exe",
                "commandLine": "grep -r x /", "cores": 1.0, "startedAt": 5, "since": 5, "at": 125000,
                "message": "grep.exe (pid 7) has searched a whole disk for 2 minutes" },
              { "id": "s2:1:leftover", "sessionId": "s2", "kind": "leftover", "pid": 9, "name": "node.exe", "processes": 2,
                "since": 1, "at": 2, "message": "2 processes still running after the session ended: bash.exe, node.exe" },
              { "kind": "cpu" }
            ] }
            """);
        var warnings = MonitorWarning.ParseList(result);
        Assert.Equal(2, warnings.Count);
        Assert.Equal(("s1", "scan", 7, "grep -r x /"), (warnings[0].SessionId, warnings[0].Kind, warnings[0].Pid, warnings[0].CommandLine));
        Assert.Equal("Whole-disk search", warnings[0].Title);
        Assert.Equal(2, warnings[1].Processes);
        Assert.Equal("Left running", warnings[1].Title);
        Assert.Equal(
            "[Reframe] Warning: grep.exe (pid 7) has searched a whole disk for 2 minutes. Command: grep -r x /. Please check whether it is still needed and stop it if not.",
            warnings[0].Note());
        Assert.DoesNotContain("Command:", warnings[1].Note());
        Assert.Empty(MonitorWarning.ParseList(null));
    }

    [Fact]
    public void TellsHowLongAgo()
    {
        Assert.Equal("just now", MonitorWarning.Ago(0, 30_000));
        Assert.Equal("12 min ago", MonitorWarning.Ago(0, 12 * 60_000));
        Assert.Equal("1 h 5 min ago", MonitorWarning.Ago(0, 65 * 60_000));
    }
}
