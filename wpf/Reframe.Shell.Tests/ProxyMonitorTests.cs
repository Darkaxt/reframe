using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

public class ProxyMonitorTests
{
    [Theory]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":\"http://127.0.0.1:8317\"}}", "http://127.0.0.1:8317/healthz")]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":\"http://localhost:9000/v1\"}}", "http://localhost:9000/healthz")]
    public void Reads_the_loopback_address_from_the_settings(string json, string expected) =>
        Assert.Equal(expected, ProxyWatch.HealthUrl(json)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"env\":{}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":5}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":\"https://api.anthropic.com\"}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":\"ftp://127.0.0.1\"}}")]
    public void Watches_nothing_without_a_loopback_address(string? json) => Assert.Null(ProxyWatch.HealthUrl(json));

    [Fact]
    public void Down_needs_two_failures_and_recovery_is_told_once()
    {
        var watch = new ProxyWatch();
        Assert.Equal(ProxyChange.Display, watch.Observe(true));
        Assert.Equal(ProxyChange.None, watch.Observe(true));
        Assert.Equal(ProxyChange.None, watch.Observe(false));
        Assert.Equal(ProxyState.Up, watch.State);
        Assert.Equal(ProxyChange.None, watch.Observe(true));
        Assert.Equal(ProxyChange.None, watch.Observe(false));
        Assert.Equal(ProxyChange.Down, watch.Observe(false));
        Assert.Equal(ProxyChange.None, watch.Observe(false));
        Assert.Equal(ProxyState.Down, watch.State);
        Assert.Equal(ProxyChange.Recovered, watch.Observe(true));
        Assert.Equal(ProxyChange.None, watch.Observe(true));
    }

    [Fact]
    public void Starting_down_is_told_too()
    {
        var watch = new ProxyWatch();
        Assert.Equal(ProxyChange.None, watch.Observe(false));
        Assert.Equal(ProxyChange.Down, watch.Observe(false));
    }
}
