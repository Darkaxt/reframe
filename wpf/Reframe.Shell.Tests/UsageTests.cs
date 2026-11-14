using System.Text.Json.Nodes;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class UsageTests
{
    [Fact]
    public void ReadsAMonitorSample()
    {
        var usage = SessionUsage.ParseSample(JsonNode.Parse("""
            { "sessions": {
                "a": { "cpu": 12.5, "memory": 1610612736, "gpu": 3, "processes": [
                    { "pid": 7, "name": "claude.exe", "cpu": 12, "memory": 1073741824, "gpu": 3, "startedAt": 1700000000000 },
                    { "pid": 9, "name": "node.exe", "cpu": 0.5, "memory": 536870912, "gpu": 0, "startedAt": 1700000001000 } ] },
                "bad": 4 } }
            """));
        var a = Assert.Single(usage).Value;
        Assert.Equal(12.5, a.Cpu);
        Assert.Equal(2, a.Processes.Count);
        Assert.Equal("claude.exe", a.Processes[0].Name);
        Assert.Equal("1.5 GB", SessionUsage.Bytes(a.Memory));
        Assert.Equal("13%", SessionUsage.Percent(a.Cpu));
        Assert.StartsWith("CPU 13% · GPU 3% · RAM 1.5 GB\n2 processes\nclaude.exe (7): CPU 12%, GPU 3%, 1.0 GB\nnode.exe (9)", a.Describe());
        Assert.Empty(SessionUsage.ParseSample(null));
    }

    [Fact]
    public void FormatsLikeTheMeters()
    {
        Assert.Equal("0%", SessionUsage.Percent(0));
        Assert.Equal("<1%", SessionUsage.Percent(0.2));
        Assert.Equal("100%", SessionUsage.Percent(100));
        Assert.Equal("412 MB", SessionUsage.Bytes(412L * 1024 * 1024));
        Assert.Equal("0 MB", SessionUsage.Bytes(0));
    }

    [Fact]
    public void MeterIconsAreFoundButNotElectronIcons()
    {
        foreach (var kind in new[] { "cpu", "gpu", "ram" })
        {
            Assert.NotNull(Icons.Find(kind));
            Assert.DoesNotContain(Icons.All, i => i.Name == kind);
        }
    }
}
