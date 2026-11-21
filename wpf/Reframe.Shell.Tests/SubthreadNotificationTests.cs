using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class SubthreadNotificationTests
{
    static AppState State()
    {
        var state = AppState.CreateDefault();
        state.Windows.Clear();
        state.Windows.Add(LayoutActions.AddTile(LayoutActions.AddTile(WindowState.Empty("w1"), "a"), "b"));
        foreach (var id in new[] { "a", "b" })
            state.Sessions[id] = new SessionRecord { Id = id, ClaudeSessionId = "claude-" + id, Cwd = @"D:\p\" + id };
        state.Sessions["b"].OpenedBy = "a";
        return state;
    }

    [Fact]
    public void A_thread_another_thread_opened_is_silent_unless_asked_for()
    {
        var state = State();
        Assert.Equal(new Notifications.Decision(true, true, null), Notifications.Decide(state, "a", "waiting", attentive: false, disabled: false));
        Assert.Equal(new Notifications.Decision(false, false, "opened by another thread"), Notifications.Decide(state, "b", "waiting", attentive: false, disabled: false));
        state.Preferences.NotifySubthreads = true;
        Assert.Equal(new Notifications.Decision(true, true, null), Notifications.Decide(state, "b", "waiting", attentive: false, disabled: false));
    }

    [Fact]
    public void The_choice_survives_a_write_and_read_and_a_patch()
    {
        var state = AppState.CreateDefault();
        Assert.True(Settings.Apply(state.Preferences, new PreferencesPatch { NotifySubthreads = true }));
        Assert.True(StateParser.Parse(StateJson.ToText(state)).State!.Preferences.NotifySubthreads);
    }
}
