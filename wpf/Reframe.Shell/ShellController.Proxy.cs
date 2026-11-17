using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Watches the local proxy the ChatGPT threads talk to (<see cref="ProxyWatch"/>): a check every half minute,
/// shown at the foot of the session list, and a toast when it goes down or comes back while a ChatGPT thread runs.
/// Observation only; the proxy is neither started nor changed.</summary>
sealed partial class ShellController
{
    static readonly TimeSpan ProxyEvery = TimeSpan.FromSeconds(30);
    static readonly HttpClient ProxyClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    readonly ProxyWatch proxy = new();
    DispatcherTimer? proxyTimer;
    string? proxyAt;
    DateTimeOffset? proxyCheckedAt;

    public event Action? ProxyChanged;

    public ProxyState ProxyState => proxy.State;
    public string? ProxyAddress => proxyAt;
    public DateTimeOffset? ProxyCheckedAt => proxyCheckedAt;

    void StartProxyWatch()
    {
        if (Fixture is not null) return;
        proxyTimer = new DispatcherTimer { Interval = ProxyEvery };
        proxyTimer.Tick += async (_, _) => await CheckProxyAsync();
        proxyTimer.Start();
        _ = CheckProxyAsync();
    }

    void StopProxyWatch()
    {
        proxyTimer?.Stop();
        proxyTimer = null;
    }

    async Task CheckProxyAsync()
    {
        Uri? url = null;
        try
        {
            url = ProxyWatch.HealthUrl(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (url is null)
        {
            if (proxy.State != Core.ProxyState.Unknown)
            {
                proxy.Reset();
                proxyAt = null;
                ProxyChanged?.Invoke();
            }
            return;
        }
        proxyAt = $"{url.Host}:{url.Port}";
        var answered = false;
        try
        {
            using var response = await ProxyClient.GetAsync(url);
            answered = response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
        proxyCheckedAt = DateTimeOffset.Now;
        var change = proxy.Observe(answered);
        if (change == ProxyChange.None) return;
        Trace.WriteLine($"proxy: {proxyAt} {(change == ProxyChange.Down ? "is not answering" : change == ProxyChange.Recovered ? "answers again" : "answers")}");
        ProxyChanged?.Invoke();
        if (change == ProxyChange.Display || !State.Sessions.Values.Any(r => r.Launch.Provider == "chatgpt" && Runs(r.Id))) return;
        if (change == ProxyChange.Down) toasts?.Show("", "Proxy not answering", "The ChatGPT threads cannot reach their model until it is back.");
        else toasts?.Show("", "Proxy is back", "The ChatGPT threads can reach their model again.");
    }
}
