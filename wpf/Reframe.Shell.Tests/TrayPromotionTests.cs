using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>A new release's tray icon keeps the taskbar choice the user made for the previous one.</summary>
public class TrayPromotionTests
{
    const string Releases = @"D:\Tools\Reframe\releases";
    static string Exe(string version) => $@"{Releases}\{version}\reframe.exe";
    static TrayIconEntry E(string key, string version, int? promoted) => new(key, Exe(version), promoted);

    [Fact]
    public void TakesTheNewestDecidedReleasesChoice()
    {
        var entries = new[]
        {
            E("a", "0.10.1-claudegpt.29", 1), E("b", "0.11.0-claudegpt.32", 1), E("c", "0.11.0-claudegpt.35", null),
            E("own", "0.11.0-claudegpt.42", null),
        };
        var plan = TrayPromotion.Decide(entries, Exe("0.11.0-claudegpt.42"), _ => true);
        Assert.Equal("own", plan.Promote);
        Assert.True(plan.OwnFound);
    }

    [Fact]
    public void ANewerHiddenChoiceWins()
    {
        var entries = new[] { E("a", "0.11.0-claudegpt.4", 1), E("b", "0.11.0-claudegpt.35", 0), E("own", "0.11.0-claudegpt.42", null) };
        Assert.Null(TrayPromotion.Decide(entries, Exe("0.11.0-claudegpt.42"), _ => true).Promote);
    }

    [Fact]
    public void TheUsersChoiceForThisReleaseStays()
    {
        var entries = new[] { E("a", "0.11.0-claudegpt.35", 1), E("own", "0.11.0-claudegpt.42", 0) };
        Assert.Null(TrayPromotion.Decide(entries, Exe("0.11.0-claudegpt.42"), _ => true).Promote);
    }

    [Fact]
    public void WaitsForWindowsToWriteTheRecord()
    {
        var plan = TrayPromotion.Decide([E("a", "0.11.0-claudegpt.35", 1)], Exe("0.11.0-claudegpt.42"), _ => true);
        Assert.False(plan.OwnFound);
        Assert.Null(plan.Promote);
    }

    [Fact]
    public void RemovesOnlyRecordsOfPrunedSiblingReleases()
    {
        var entries = new[]
        {
            E("gone", "0.11.0-claudegpt.33", null), E("kept", "0.11.0-claudegpt.35", 1),
            new TrayIconEntry("other", @"D:\Temp\build\win-unpacked\reframe.exe", null),
            new TrayIconEntry("app", @"D:\Tools\Reframe\releases\x\other.exe", null),
            E("own", "0.11.0-claudegpt.42", null),
        };
        var live = new HashSet<string>([Exe("0.11.0-claudegpt.35"), Exe("0.11.0-claudegpt.42")], StringComparer.OrdinalIgnoreCase);
        var plan = TrayPromotion.Decide(entries, Exe("0.11.0-claudegpt.42"), live.Contains);
        Assert.Equal(["gone"], plan.Remove);
        Assert.Equal("own", plan.Promote);
    }

    sealed class FakeSettings(List<TrayIconEntry> entries) : ITrayIconSettings
    {
        public readonly List<TrayIconEntry> Items = entries;
        public IReadOnlyList<TrayIconEntry> Entries() => Items;
        public void SetPromoted(string key, int value)
        {
            var i = Items.FindIndex(e => e.Key == key);
            Items[i] = Items[i] with { IsPromoted = value };
        }
        public void Delete(string key) => Items.RemoveAll(e => e.Key == key);
    }

    [Fact]
    public void ApplyWritesThePlan()
    {
        var settings = new FakeSettings([E("gone", "0.11.0-claudegpt.33", null), E("b", "0.11.0-claudegpt.35", 1), E("own", "0.11.0-claudegpt.42", null)]);
        var found = TrayPromotion.Apply(settings, Exe("0.11.0-claudegpt.42"), p => !p.Contains(".33"), _ => { });
        Assert.True(found);
        Assert.DoesNotContain(settings.Items, e => e.Key == "gone");
        Assert.Equal(1, settings.Items.Single(e => e.Key == "own").IsPromoted);
    }
}
