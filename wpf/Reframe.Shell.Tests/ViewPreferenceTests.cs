using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ViewPreferenceTests
{
    [Fact]
    public void The_sidebar_width_and_the_chat_view_survive_a_write_and_read()
    {
        var state = AppState.CreateDefault();
        state.Preferences.SidebarWidth = 342;
        state.Preferences.ChatView = true;
        var back = StateParser.Parse(StateJson.ToText(state)).State!.Preferences;
        Assert.Equal(342, StateValues.ClampSidebarWidth(back.SidebarWidth));
        Assert.True(back.ChatView);
        var fresh = StateParser.Parse(StateJson.ToText(AppState.CreateDefault())).State!.Preferences;
        Assert.Equal(StateValues.SidebarWidthNormal, StateValues.ClampSidebarWidth(fresh.SidebarWidth));
        Assert.False(fresh.ChatView);
    }
}
