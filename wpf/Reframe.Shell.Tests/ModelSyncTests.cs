using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ModelSyncTests
{
    [Theory]
    [InlineData("opus", "claude-opus-5-5", null)]
    [InlineData("opus[1m]", "claude-opus-5-5", null)]
    [InlineData("sonnet", "claude-opus-5-5", "claude-opus-5-5")]
    [InlineData(null, "claude-sonnet-5-5", "claude-sonnet-5-5")]
    [InlineData("default", "gpt-6.1-sol", "gpt-6.1-sol")]
    [InlineData("gpt-6.1-sol", "gpt-6.1-sol", null)]
    [InlineData("gpt-6.1-sol", "gpt-6.1-sol-fast", "gpt-6.1-sol-fast")]
    [InlineData("opus", "<synthetic>", null)]
    [InlineData("opus", "", null)]
    public void A_model_is_kept_unless_the_session_runs_another(string? stored, string seen, string? expected) =>
        Assert.Equal(expected, ModelSync.Changed(stored, seen));

    [Fact]
    public void The_selected_thread_of_a_window_survives_a_write_and_read()
    {
        var state = AppState.CreateDefault();
        state.Windows[0].Selected = "abc";
        Assert.Equal("abc", StateParser.Parse(StateJson.ToText(state)).State!.Windows[0].Selected);
    }
}
