using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ProfileTests
{
    static LaunchProfile P(string name, string provider = "anthropic", string id = "") => new()
    {
        Id = id, Name = name, Provider = provider, Model = "opus", Effort = "high", PermissionMode = "bypassPermissions",
    };

    [Fact]
    public void Profiles_survive_a_write_and_read()
    {
        var state = AppState.CreateDefault();
        state.Preferences.Profiles = [P("Opus high", id: "a1"), P("Sol", "chatgpt", "b2")];
        var back = StateParser.Parse(StateJson.ToText(state)).State!;
        Assert.Equal(["Opus high", "Sol"], back.Preferences.Profiles!.Select(p => p.Name));
        Assert.Equal("bypassPermissions", back.Preferences.Profiles![0].PermissionMode);
        Assert.Equal("chatgpt", back.Preferences.Profiles[1].Provider);
    }

    [Fact]
    public void A_patch_cleans_the_list_and_gives_ids()
    {
        var prefs = AppState.CreateDefault().Preferences;
        var bad = P("x", "elsewhere");
        var blank = P("   ");
        var odd = P(new string('n', 80));
        odd.Effort = "extreme";
        Assert.True(Settings.Apply(prefs, new PreferencesPatch { Profiles = [bad, blank, odd, P("Same", id: "dup"), P("Same two", id: "dup")] }));
        Assert.Equal(3, prefs.Profiles!.Count);
        Assert.Equal(60, prefs.Profiles[0].Name.Length);
        Assert.Null(prefs.Profiles[0].Effort);
        Assert.All(prefs.Profiles, p => Assert.NotEqual("", p.Id));
        Assert.Equal(3, prefs.Profiles.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void An_empty_list_removes_the_profiles()
    {
        var prefs = AppState.CreateDefault().Preferences;
        Settings.Apply(prefs, new PreferencesPatch { Profiles = [P("A", id: "a")] });
        Assert.True(Settings.Apply(prefs, new PreferencesPatch { Profiles = [] }));
        Assert.Null(prefs.Profiles);
    }

    [Fact]
    public void Making_a_profile_the_default_sets_its_providers_defaults()
    {
        var prefs = AppState.CreateDefault().Preferences;
        Settings.Apply(prefs, new PreferencesPatch { Profiles = [P("A", id: "a"), P("B", "chatgpt", "b")] });
        Assert.True(Settings.Apply(prefs, new PreferencesPatch { MakeDefaultProfile = "a" }));
        var defaults = NewSession.DefaultsFor(prefs, "anthropic");
        Assert.Equal(("opus", "high", "bypassPermissions"), (defaults.Model, defaults.Effort, defaults.PermissionMode));
        Assert.Equal(["a"], NewSession.ProfilesFor(prefs, "anthropic").Select(p => p.Id));
    }
}
