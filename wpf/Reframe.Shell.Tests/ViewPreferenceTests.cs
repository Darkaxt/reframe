using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ViewPreferenceTests
{
    [Fact]
    public void The_sidebar_width_and_the_chat_view_survive_a_write_and_read()
    {
        var state = AppState.CreateDefault();
        state.Preferences.SidebarWidth = 341.6;
        state.Preferences.ChatView = true;
        var back = StateParser.Parse(StateJson.ToText(state)).State!.Preferences;
        Assert.Equal(342.0, back.SidebarWidth);
        Assert.True(back.ChatView);
        var fresh = StateParser.Parse(StateJson.ToText(AppState.CreateDefault())).State!.Preferences;
        Assert.Null(fresh.SidebarWidth);
        Assert.False(fresh.ChatView);
    }

    [Fact]
    public void A_sidebar_width_out_of_range_is_dropped()
    {
        var state = AppState.CreateDefault();
        state.Preferences.SidebarWidth = 5000;
        Assert.Null(StateParser.Parse(StateJson.ToText(state)).State!.Preferences.SidebarWidth);
    }
}
