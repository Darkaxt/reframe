using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class UpdateWatchTests
{
    const string Json = """{"version":"0.11.0-claudegpt.85","executable":"D:\\Tools\\Reframe\\releases\\0.11.0-claudegpt.85\\reframe.exe","status":"installed_pending_launch"}""";

    [Fact]
    public void A_different_installed_version_is_pending()
    {
        var update = UpdateWatch.Pending(Json, "0.11.0-claudegpt.84+abc", _ => true);
        Assert.Equal("0.11.0-claudegpt.85", update?.Version);
    }

    [Fact]
    public void The_running_version_is_not_pending_even_with_a_commit_suffix()
        => Assert.Null(UpdateWatch.Pending(Json, "0.11.0-claudegpt.85+1234", _ => true));

    [Fact]
    public void A_missing_executable_or_half_written_file_is_not_pending()
    {
        Assert.Null(UpdateWatch.Pending(Json, "0.11.0-claudegpt.84", _ => false));
        Assert.Null(UpdateWatch.Pending("{\"version\":", "0.11.0-claudegpt.84", _ => true));
    }

    [Fact]
    public void The_active_file_sits_beside_the_releases_folder()
    {
        Assert.Equal(Path.Combine(@"D:\Tools\Reframe", "active.json"), UpdateWatch.ActiveFile(@"D:\Tools\Reframe\releases\0.11.0-claudegpt.84\"));
        Assert.Null(UpdateWatch.ActiveFile(@"D:\src\bin\Debug\"));
    }
}
