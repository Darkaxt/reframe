using System.Text.RegularExpressions;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Notifications (S9): src/main/toast.test.ts's app id, CLSID and activation tests, ported, and app.ts
/// notify()'s and updateAttention's rules.</summary>
public class NotificationsTests
{
    const string Exe = @"D:\Tools\Reframe\releases\0.10.1-claudegpt.14\reframe.exe";

    [Fact]
    public void AppUserModelId_uses_the_fixed_managed_id_else_the_executable()
    {
        Assert.Equal("Darkaxt.Reframe.ClaudeGPT", Notifications.AppUserModelId("Darkaxt.Reframe.ClaudeGPT", Exe));
        Assert.Equal(Exe, Notifications.AppUserModelId(null, Exe));
        Assert.Equal(Exe, Notifications.AppUserModelId(@"bad id\x", Exe));
    }

    [Fact]
    public void ToastActivatorClsid_is_a_stable_braced_guid_per_app_id()
    {
        var id = Notifications.ToastActivatorClsid("Darkaxt.Reframe.ClaudeGPT");
        Assert.Matches(new Regex("^\\{[0-9A-F]{8}-[0-9A-F]{4}-5[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\\}$"), id);
        Assert.Equal(id, Notifications.ToastActivatorClsid("Darkaxt.Reframe.ClaudeGPT"));
        Assert.NotEqual(id, Notifications.ToastActivatorClsid("dev.reframe.app"));
        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void ToastActivatorClsid_matches_reframes()
    {
        // node -e "toastActivatorClsid('dev.reframe.app')" with src/main/toast.ts's code: sha1('reframe-toast:dev.reframe.app').
        var h = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData("reframe-toast:dev.reframe.app"u8.ToArray()));
        var id = Notifications.ToastActivatorClsid("dev.reframe.app");
        Assert.StartsWith("{" + h[..8] + "-" + h[8..12] + "-5" + h[13..16] + "-", id);
        Assert.EndsWith(h[17..20] + "-" + h[20..32] + "}", id);
    }

    [Fact]
    public void Activation_round_trips_the_session_id_through_the_launch_argument()
    {
        const string id = "a1b2&c<d>\"e";
        var toast = Notifications.SessionToastXml(id, "Done: <x> & \"y\"", "body");
        Assert.Contains("<text>Done: &lt;x&gt; &amp; &quot;y&quot;</text>", toast);
        var launch = Regex.Match(toast, "launch=\"([^\"]*)\"").Groups[1].Value.Replace("&amp;", "&");
        Assert.Equal(id, Notifications.SessionFromActivation(launch));
        // The toast is well-formed XML.
        Assert.Equal("toast", System.Xml.Linq.XElement.Parse(toast).Name.LocalName);
    }

    [Fact]
    public void Activation_ignores_arguments_that_name_no_session()
    {
        Assert.Null(Notifications.SessionFromActivation(""));
        Assert.Null(Notifications.SessionFromActivation(null));
        Assert.Null(Notifications.SessionFromActivation("other=1"));
        Assert.Null(Notifications.SessionFromActivation("reframe-session=%E0%A4%A"));
    }

    [Fact]
    public void Toast_text_is_reframes()
    {
        Assert.Equal(("db-migration is done", "Claude finished responding."), Notifications.ToastText("done", "db-migration"));
        Assert.Equal(("db-migration needs you", "Claude is waiting for your input."), Notifications.ToastText("waiting", "db-migration"));
        Assert.Equal("Reframe – a session needs you", Notifications.WaitingText(1));
        Assert.Equal("Reframe – 3 sessions need you", Notifications.WaitingText(3));
    }

    static AppState State()
    {
        var state = AppState.CreateDefault();
        state.Windows.Clear();
        var w = LayoutActions.AddTile(LayoutActions.AddTile(WindowState.Empty("w1"), "a"), "b");
        state.Windows.Add(w);
        state.Windows.Add(LayoutActions.AddTile(WindowState.Empty("w2"), "c"));
        foreach (var id in new[] { "a", "b", "c", "lost" })
            state.Sessions[id] = new SessionRecord { Id = id, ClaudeSessionId = "claude-" + id, Cwd = @"D:\p\" + id };
        return state;
    }

    [Fact]
    public void Decide_toasts_and_flashes_where_nobody_looks()
    {
        var state = State();
        Assert.Equal(new Notifications.Decision(true, true, null), Notifications.Decide(state, "a", "waiting", attentive: false, disabled: false));
        Assert.Equal(new Notifications.Decision(false, true, null), Notifications.Decide(state, "a", "done", attentive: false, disabled: false));
    }

    [Fact]
    public void Decide_is_quiet_for_the_tile_the_user_looks_at()
    {
        var state = State();
        Assert.Equal(new Notifications.Decision(false, false, "window in focus, tile visible"),
            Notifications.Decide(state, "a", "waiting", attentive: true, disabled: false));
        // Collapsed into the dock, the tile is not seen even in the focused window.
        state.Windows[0] = LayoutActions.Apply(state.Windows[0], new LayoutAction.TileCollapse("a"));
        Assert.Equal(new Notifications.Decision(true, true, null), Notifications.Decide(state, "a", "waiting", attentive: true, disabled: false));
    }

    [Fact]
    public void Decide_skips_hidden_threads_sessions_in_no_window_and_disabled_notifications()
    {
        var state = State();
        Assert.False(Notifications.Decide(state, "lost", "waiting", false, false).Toast);
        Assert.False(Notifications.Decide(state, "unknown", "waiting", false, false).Flash);
        state.HiddenThreads.Add(new HiddenThread { ClaudeSessionId = "claude-c", Label = "c" });
        Assert.Equal(new Notifications.Decision(false, false, "not shown"), Notifications.Decide(state, "c", "waiting", false, false));
        // REFRAME_DISABLE_NOTIFICATIONS: neither a toast nor a flash.
        Assert.Equal(new Notifications.Decision(false, false, "notifications off"), Notifications.Decide(state, "a", "waiting", false, true));
    }

    [Fact]
    public void AttentionCounts_counts_waiting_sessions_per_window()
    {
        var state = State();
        var status = new Dictionary<string, string> { ["a"] = "waiting", ["b"] = "waiting", ["c"] = "busy" };
        var counts = Notifications.AttentionCounts(state, id => status.GetValueOrDefault(id, "ended"));
        Assert.Equal(2, counts["w1"]);
        Assert.Equal(0, counts["w2"]);
    }
}
