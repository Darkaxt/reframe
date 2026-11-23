using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Naming what a "background" thread runs from the process table (BackgroundWork).</summary>
public class BackgroundWorkTests
{
    static readonly DateTime T0 = new(2026, 11, 23, 10, 0, 0);
    const int Claude = 100;

    static ProcessEntry P(int pid, int parent, string exe, string? line = null, int minute = 1) =>
        new(pid, parent, exe, line, T0.AddMinutes(minute));

    static List<ProcessEntry> With(params ProcessEntry[] more) => [new(Claude, 1, "claude.exe", null, T0), .. more];

    [Fact]
    public void ACoordinatorOwnerTaskNamesTheWork()
    {
        var processes = With(
            P(200, Claude, "bash.exe", "bash -c \"eval 'gate run'\""),
            P(201, 200, "coordinator-client.exe", "coordinator-client.exe gate run --owner-task \"Build the APK\" -- gradlew"),
            P(202, 201, "java.exe", "java -jar gradle.jar"));
        Assert.Equal(("Build the APK", T0.AddMinutes(1)), BackgroundWork.Describe(Claude, processes)!.Value);
    }

    [Fact]
    public void APythonScriptIsNamedWithItsFile()
    {
        var processes = With(
            P(200, Claude, "bash.exe"),
            P(201, 200, "python.exe", @"python -u -I D:\Work\gemini_image.py --out x.png"));
        Assert.Equal("python gemini_image.py", BackgroundWork.Describe(Claude, processes)?.Label);
    }

    [Fact]
    public void PowerShellIsNamedWithItsFileArgument()
    {
        var processes = With(
            P(200, Claude, "bash.exe"),
            P(201, 200, "powershell.exe", @"powershell -NoProfile -File ""C:\x y\Run-Job.ps1"" -Fast"));
        Assert.Equal("powershell Run-Job.ps1", BackgroundWork.Describe(Claude, processes)?.Label);
    }

    [Fact]
    public void OnlyShellsShowTheEvaluatedCommand()
    {
        var processes = With(
            P(200, Claude, "bash.exe", "bash -c \"source /tmp/snapshot.sh && eval 'sleep 600 && echo '\\''done'\\''' < /dev/null && pwd -P\""),
            P(201, 200, "conhost.exe"));
        Assert.Equal("sleep 600 && echo 'done'", BackgroundWork.Describe(Claude, processes)?.Label);
    }

    [Fact]
    public void AShellWithoutACommandIsAShellCommand()
    {
        Assert.Equal("a shell command", BackgroundWork.Describe(Claude, With(P(200, Claude, "cmd.exe")))?.Label);
    }

    [Fact]
    public void AChildOlderThanClaudeIsIgnored()
    {
        var processes = With(P(200, Claude, "bash.exe", minute: -5), P(201, 200, "python.exe", "python job.py", minute: -5));
        Assert.Null(BackgroundWork.Describe(Claude, processes));
    }

    [Fact]
    public void NonShellChildrenOfClaudeAreIgnored()
    {
        var processes = With(P(200, Claude, "node.exe", "node mcp-server.js"), P(201, 200, "python.exe", "python x.py"));
        Assert.Null(BackgroundWork.Describe(Claude, processes));
    }

    [Fact]
    public void SeveralWorksShowTheFirstAndACount()
    {
        var processes = With(
            P(200, Claude, "bash.exe", minute: 2),
            P(201, 200, "python.exe", "python second.py", minute: 2),
            P(300, Claude, "bash.exe", minute: 1),
            P(301, 300, "gh.exe", "gh run watch 123", minute: 1),
            P(400, Claude, "bash.exe", minute: 3),
            P(401, 400, "python.exe", "python third.py", minute: 3));
        Assert.Equal(("gh run (+2 more)", T0.AddMinutes(1)), BackgroundWork.Describe(Claude, processes)!.Value);
    }

    [Fact]
    public void ALongLabelIsCut()
    {
        var processes = With(P(200, Claude, "coordinator-client.exe"), P(201, Claude, "bash.exe"),
            P(202, 201, "coordinator-client.exe", $"coordinator-client --owner-task \"{new string('a', 200)}\""));
        var label = BackgroundWork.Describe(Claude, processes)!.Value.Label;
        Assert.Equal(80, label.Length);
        Assert.EndsWith("…", label);
    }

    [Theory]
    [InlineData(0.5, "Background: job · just now")]
    [InlineData(14.2, "Background: job · 14 min")]
    [InlineData(60, "Background: job · 1 h")]
    [InlineData(65, "Background: job · 1 h 5 min")]
    public void TextShowsHowLongItRuns(double minutes, string expected)
    {
        Assert.Equal(expected, BackgroundWork.Text("job", T0, T0.AddMinutes(minutes)));
    }
}
