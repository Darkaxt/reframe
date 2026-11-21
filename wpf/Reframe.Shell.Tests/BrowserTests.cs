using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class BrowserTests
{
    [Fact]
    public void The_browser_choices_survive_a_write_and_read()
    {
        var state = AppState.CreateDefault();
        state.Preferences.BrowserAnthropic = true;
        state.Preferences.BrowserChatgpt = false;
        var back = StateParser.Parse(StateJson.ToText(state)).State!;
        Assert.True(back.Preferences.BrowserAnthropic);
        Assert.False(back.Preferences.BrowserChatgpt);
        Assert.Null(StateParser.Parse(StateJson.ToText(AppState.CreateDefault())).State!.Preferences.BrowserAnthropic);
    }

    [Fact]
    public void A_patch_sets_clears_and_ignores_unknown_choices()
    {
        var prefs = AppState.CreateDefault().Preferences;
        Assert.True(Settings.Apply(prefs, new PreferencesPatch { BrowserAnthropic = "on" }));
        Assert.True(prefs.BrowserAnthropic);
        Assert.True(Settings.Apply(prefs, new PreferencesPatch { BrowserAnthropic = "off" }));
        Assert.False(prefs.BrowserAnthropic);
        Assert.False(Settings.Apply(prefs, new PreferencesPatch { BrowserAnthropic = "maybe" }));
        Assert.True(Settings.Apply(prefs, new PreferencesPatch { BrowserAnthropic = "" }));
        Assert.Null(prefs.BrowserAnthropic);
        Assert.Equal("", Settings.BrowserChoice(prefs.BrowserChatgpt));
    }
}
