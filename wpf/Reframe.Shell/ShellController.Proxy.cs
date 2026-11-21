using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Watches the local proxy the ChatGPT threads talk to (<see cref="ProxyWatch"/>): a check every half minute,
/// shown at the foot of the session list, and a toast when it goes down or comes back while a ChatGPT thread runs.
/// Stop, start and restart go through the proxy's own supervisor only (<see cref="ProxyControl"/>).</summary>
sealed partial class ShellController
{
    static readonly TimeSpan ProxyEvery = TimeSpan.FromSeconds(30);
    static readonly HttpClient ProxyClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    readonly ProxyWatch proxy = new();
    DispatcherTimer? proxyTimer;
    string? proxyAt;
    DateTimeOffset? proxyCheckedAt;

    public event Action? ProxyChanged;

    ProxyInstall? proxyInstall;
    string? proxyBusy;

    /// <summary>What the proxy buttons are doing now ("Restarting…"), or null.</summary>
    public string? ProxyBusy => proxyBusy;
    /// <summary>Whether Reframe can stop and start the proxy (its supervisor and launcher are installed).</summary>
    public bool ProxyControllable => proxyInstall is not null && Writable;
    /// <summary>How many ChatGPT and Gemini threads run now (they lose their model while the proxy is down).</summary>
    public int ChatGptRunning => State.Sessions.Values.Count(r => r.Launch.Provider is "chatgpt" or "gemini" && Runs(r.Id));

    public ProxyState ProxyState => proxy.State;
    public string? ProxyAddress => proxyAt;
    public DateTimeOffset? ProxyCheckedAt => proxyCheckedAt;

    void StartProxyWatch()
    {
        if (Fixture is not null) return;
        proxyInstall = ProxyControl.Find(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
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

    static Uri? ProxyUrl()
    {
        try
        {
            return ProxyWatch.HealthUrl(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static async Task<bool> ProbeProxyAsync(Uri url)
    {
        try
        {
            using var response = await ProxyClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>"stop", "start" or "restart", through the proxy's own supervisor only; a proxy it does not run is left
    /// alone. Says how it went in a toast and in main.log.</summary>
    public async Task ProxyActionAsync(string action)
    {
        if (proxyInstall is not { } install || proxyBusy is not null || !Writable) return;
        proxyBusy = action switch { "stop" => "Stopping…", "start" => "Starting…", _ => "Restarting…" };
        ProxyChanged?.Invoke();
        string? failure = null;
        try
        {
            if (action is "stop" or "restart")
            {
                failure = await Task.Run(() => ProxyControl.Stop(ProxyControl.Instance, TimeSpan.FromSeconds(15))) switch
                {
                    ProxyStop.NotRunning => "No supervisor runs this proxy (it was started by hand), so Reframe left it alone.",
                    ProxyStop.TimedOut => "The proxy did not stop within 15 seconds; nothing else was touched.",
                    _ => null,
                };
                if (failure is null && action == "stop")
                {
                    proxy.Observe(false);
                    proxy.Observe(false);
                    proxyCheckedAt = DateTimeOffset.Now;
                }
            }
            if (failure is null && action is "start" or "restart")
            {
                var skill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "approach-recovery-ledger", "SKILL.md");
                failure = ProxyControl.Start(install, Path.Combine(App.ProfileDir, "proxy-scratch"), skill);
                if (failure is null)
                {
                    var url = ProxyUrl();
                    var up = false;
                    for (var i = 0; i < 30 && url is not null && !(up = await ProbeProxyAsync(url)); i++) await Task.Delay(1000);
                    if (up)
                    {
                        proxy.Observe(true);
                        proxyCheckedAt = DateTimeOffset.Now;
                    }
                    else failure = "The proxy did not answer within 30 seconds of starting.";
                }
            }
        }
        finally
        {
            proxyBusy = null;
            ProxyChanged?.Invoke();
        }
        var done = action switch { "stop" => "stopped", "start" => "started", _ => "restarted" };
        Trace.WriteLine($"proxy: {action} {(failure is null ? "done" : "failed: " + failure)}");
        toasts?.Show("", failure is null ? $"Proxy {done}" : $"Proxy not {done}",
            failure ?? (action == "stop" ? "The ChatGPT threads cannot reach their model until it is started." : "The ChatGPT threads can reach their model."));
    }

    async Task CheckProxyAsync()
    {
        if (proxyBusy is not null) return;
        var url = ProxyUrl();
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
        var answered = await ProbeProxyAsync(url);
        if (proxyBusy is not null) return;
        proxyCheckedAt = DateTimeOffset.Now;
        var change = proxy.Observe(answered);
        if (change == ProxyChange.None) return;
        Trace.WriteLine($"proxy: {proxyAt} {(change == ProxyChange.Down ? "is not answering" : change == ProxyChange.Recovered ? "answers again" : "answers")}");
        ProxyChanged?.Invoke();
        if (change == ProxyChange.Display || ChatGptRunning == 0) return;
        if (change == ProxyChange.Down) toasts?.Show("", "Proxy not answering", "The ChatGPT threads cannot reach their model until it is back.");
        else toasts?.Show("", "Proxy is back", "The ChatGPT threads can reach their model again.");
    }
}
