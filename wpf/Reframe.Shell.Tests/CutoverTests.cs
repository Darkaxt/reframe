using System.Security.Cryptography;
using System.Text.Json;
using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The cutover from reframe.exe (S14): launch-config.test.ts's direct start, the profile routes, --quit over the
/// single-instance pipe, the managed login entry and the managed shortcuts.</summary>
public class CutoverTests
{
    static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reframe-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    static (string Dir, string Config, string Claude, string Helper) Config(Func<string, string, object>? make = null)
    {
        var dir = NewDir();
        var claude = Path.Combine(dir, "claude.exe");
        File.WriteAllText(claude, "fake claude");
        var helper = Path.Combine(dir, "helper.py");
        File.WriteAllText(helper, "fake helper");
        var config = Path.Combine(dir, LaunchConfig.FileName);
        object value = make?.Invoke(claude, helper) ?? new
        {
            userData = Path.Combine(dir, "profile"), runtime = Path.Combine(dir, "runtime"), claudeHome = Path.Combine(dir, ".claude"),
            claudeBin = new { path = claude, sha256 = Sha(claude) },
            anthropicClaudeBin = new { path = claude, sha256 = "0".PadLeft(64, '0') },
            history = new { python = claude, helper, helperSha256 = Sha(helper) },
        };
        File.WriteAllText(config, JsonSerializer.Serialize(value));
        return (dir, config, claude, helper);
    }

    [Fact]
    public void Evaluate_binds_each_cli_by_hash_and_refuses_a_changed_one()
    {
        var (dir, config, claude, helper) = Config();
        var plan = LaunchConfig.Evaluate(config)!;
        Assert.Equal(Path.Combine(dir, "profile"), plan.UserData);
        Assert.Equal(claude, plan.Env["REFRAME_CLAUDE_BIN"]);
        Assert.False(plan.Env.ContainsKey("REFRAME_ANTHROPIC_CLAUDE_BIN"));
        Assert.Contains("changed", plan.Refused["anthropic"]);
        Assert.Equal(helper, plan.Env["REFRAME_HISTORY_HELPER"]);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void Evaluate_is_null_without_absolute_folders_or_a_readable_file()
    {
        var (_, config, _, _) = Config((_, _) => new { userData = "relative", runtime = @"D:\r", claudeHome = @"D:\c" });
        Assert.Null(LaunchConfig.Evaluate(config));
        Assert.Null(LaunchConfig.Evaluate(Path.Combine(NewDir(), "missing.json")));
    }

    [Fact]
    public void Evaluate_takes_the_adopted_clients_when_both_are_intact()
    {
        var (dir, config, claude, _) = Config();
        var record = Path.Combine(dir, "client-adoption.json");
        File.WriteAllText(record, JsonSerializer.Serialize(new
        {
            schema = "reframe.client-adoption.v1", version = "2.1.0",
            clients = new { chatgpt = new { binary = claude, sha256 = Sha(claude) }, anthropic = new { binary = claude, sha256 = Sha(claude) } },
        }));
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            userData = Path.Combine(dir, "p"), runtime = Path.Combine(dir, "r"), claudeHome = Path.Combine(dir, "c"), clientAdoption = record,
        }));
        var plan = LaunchConfig.Evaluate(config)!;
        Assert.Equal(record, plan.Env["REFRAME_CLIENT_ADOPTION"]);
        Assert.Equal(claude, plan.Env["REFRAME_ANTHROPIC_CLAUDE_BIN"]);
        Assert.Empty(plan.Refused);
    }

    [Fact]
    public void Resolve_prefers_the_argument_then_the_launcher_then_the_config_beside_the_executable()
    {
        var (dir, _, claude, _) = Config();
        var env = new Dictionary<string, string?> { ["REFRAME_USER_DATA"] = @"D:\Launcher\Profile", ["CLAUDE_CONFIG_DIR"] = @"D:\x" };
        Assert.Equal(@"D:\Arg", LaunchConfig.Resolve(@"D:\Arg", env, dir, dir).Profile);
        Assert.Equal(@"D:\Launcher\Profile", LaunchConfig.Resolve(null, env, dir, dir).Profile);
        Assert.Equal(@"D:\x", env["CLAUDE_CONFIG_DIR"]);

        env.Remove("REFRAME_USER_DATA");
        var (profile, applied) = LaunchConfig.Resolve(null, env, dir, dir);
        Assert.Equal(Path.Combine(dir, "profile"), profile);
        Assert.NotNull(applied);
        Assert.Equal(profile, env["REFRAME_USER_DATA"]);
        Assert.Equal(Path.Combine(dir, "runtime"), env["TEMP"]);
        Assert.True(Directory.Exists(Path.Combine(dir, "runtime")));
        Assert.Null(env["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(claude, env["REFRAME_CLAUDE_BIN"]);
        Assert.False(env.ContainsKey("REFRAME_APP_ID"));

        Assert.Null(LaunchConfig.Resolve(null, new Dictionary<string, string?>(), NewDir(), dir).Profile);
    }

    [Fact]
    public void A_direct_start_of_a_managed_release_takes_the_launchers_app_id()
    {
        var root = NewDir();
        var release = Path.Combine(root, "releases", "0.10.1-claudegpt.31");
        Directory.CreateDirectory(release);
        Directory.CreateDirectory(Path.Combine(root, "maintenance"));
        var profile = Path.Combine(root, "profiles", "ClaudeGPT");
        Assert.Null(LaunchConfig.ManagedIdentity(release, profile, @"C:\Programs"));
        File.WriteAllText(Path.Combine(root, "maintenance", "start-claudegpt.vbs"), "");
        var id = LaunchConfig.ManagedIdentity(release, profile, @"C:\Programs")!.Value;
        Assert.Equal("Darkaxt.Reframe.ClaudeGPT", id.AppId);
        Assert.Equal(@"C:\Programs\Reframe - ClaudeGPT.lnk", id.Shortcut);
        Assert.Equal(Path.Combine(root, "maintenance", "start-claudegpt.vbs"), id.Launcher);
        Assert.Null(LaunchConfig.ManagedIdentity(root, profile, @"C:\Programs"));
    }

    [Fact]
    public async Task Quit_asks_the_profiles_running_shell_to_quit()
    {
        var profile = NewDir();
        Assert.False(SingleInstance.Running(profile));
        var quit = new TaskCompletionSource();
        using (var first = SingleInstance.TryAcquire(profile, () => { }, () => quit.TrySetResult()))
        {
            Assert.True(SingleInstance.Running(profile));
            Assert.True(await SingleInstance.RequestQuitAsync(profile, TimeSpan.FromSeconds(10)));
            await quit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(SingleInstance.Running(profile));
        Assert.False(await SingleInstance.RequestQuitAsync(profile, TimeSpan.FromMilliseconds(300)));
    }

    sealed class RunKeys : IRunKeys
    {
        public readonly Dictionary<string, string> Run = [];
        public readonly Dictionary<string, byte[]> Approved = [];
        public string? GetRun(string name) => Run.GetValueOrDefault(name);
        public void SetRun(string name, string command) => Run[name] = command;
        public void DeleteRun(string name) => Run.Remove(name);
        public byte[]? GetApproved(string name) => Approved.GetValueOrDefault(name);
        public void SetApproved(string name, byte[] value) => Approved[name] = value;
        public void DeleteApproved(string name) => Approved.Remove(name);
    }

    [Fact]
    public void A_managed_shell_takes_reframes_login_entry_over()
    {
        const string exe = @"D:\Tools\Reframe\releases\0.10.1-claudegpt.31\reframe.exe";
        var keys = new RunKeys();
        var autostart = new Autostart(keys, @"D:\Tools\Reframe\profiles\ClaudeGPT", exe, "Darkaxt.Reframe.ClaudeGPT");
        Assert.Equal("Darkaxt.Reframe.ClaudeGPT", autostart.Name);
        Assert.Equal($"\"{exe}\" --autostart", autostart.Command);
        Assert.False(autostart.Repoint());
        Assert.Empty(keys.Run);

        var disabled = new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        keys.Run["Darkaxt.Reframe.ClaudeGPT"] = "\"D:\\Tools\\Reframe\\releases\\0.10.1-claudegpt.29\\reframe.exe\" --autostart";
        keys.Approved["Darkaxt.Reframe.ClaudeGPT"] = disabled;
        Assert.True(autostart.Repoint());
        Assert.Equal(autostart.Command, keys.Run["Darkaxt.Reframe.ClaudeGPT"]);
        Assert.Same(disabled, keys.Approved["Darkaxt.Reframe.ClaudeGPT"]);
        Assert.False(autostart.Repoint());

        Assert.StartsWith("Reframe Shell ", new Autostart(keys, @"D:\P", exe).Name);
    }

    [Fact]
    public void The_managed_shortcuts_run_the_launcher_with_the_app_id_and_clsid()
    {
        const string appId = "Darkaxt.Reframe.ClaudeGPT";
        var env = new Dictionary<string, string?>
        {
            ["REFRAME_APP_ID"] = appId, ["REFRAME_APP_SHORTCUT"] = @"C:\P\Reframe - ClaudeGPT.lnk",
            ["REFRAME_APP_LAUNCHER"] = @"D:\Tools\Reframe\maintenance\start-claudegpt.vbs", ["SystemRoot"] = @"C:\Windows",
        };
        var wanted = ManagedShortcuts.Wanted(env, appId, @"D:\x\reframe.exe");
        Assert.Equal([@"C:\P\Reframe - ClaudeGPT.lnk", @"D:\Tools\Reframe\Reframe.lnk"], wanted.Select(s => s.File));
        Assert.All(wanted, s =>
        {
            Assert.Equal(@"C:\Windows\System32\wscript.exe", s.Target);
            Assert.Equal("//B //Nologo \"D:\\Tools\\Reframe\\maintenance\\start-claudegpt.vbs\"", s.Arguments);
            Assert.Equal(@"D:\Tools\Reframe", s.WorkingDirectory);
            Assert.Equal(Notifications.ToastActivatorClsid(appId), s.ToastActivatorClsid);
        });
        Assert.Empty(ManagedShortcuts.Wanted(env, "other", @"D:\x\reframe.exe"));
        Assert.Empty(ManagedShortcuts.Wanted(new Dictionary<string, string?> { ["REFRAME_APP_ID"] = appId }, appId, "x"));

        Assert.Equal(ManagedShortcuts.Operation.Create, ManagedShortcuts.Decide(null, wanted[0]));
        Assert.Equal(ManagedShortcuts.Operation.Update, ManagedShortcuts.Decide(new(null, null), wanted[0]));
        Assert.Equal(ManagedShortcuts.Operation.None,
            ManagedShortcuts.Decide(new(appId, wanted[0].ToastActivatorClsid.ToLowerInvariant().Trim('{', '}')), wanted[0]));
    }

    [Fact]
    public void A_shortcut_file_is_written_whole_then_only_its_app_id_is_updated()
    {
        var dir = NewDir();
        var lnk = Path.Combine(dir, "Reframe - Test.lnk");
        var spec = new ManagedShortcuts.Spec(lnk, Path.Combine(Environment.SystemDirectory, "wscript.exe"), "//B //Nologo \"D:\\x\\start.vbs\"",
            dir, "Reframe - Test", Path.Combine(Environment.SystemDirectory, "wscript.exe"), "Darkaxt.Reframe.Test", Notifications.ToastActivatorClsid("Darkaxt.Reframe.Test"));
        Assert.Equal(["" + lnk + ": Create"], ManagedShortcuts.Apply([spec]));
        var link = ShortcutFile.ReadLink(lnk);
        Assert.Equal(spec.Target, link.Target, ignoreCase: true);
        Assert.Equal(spec.Arguments, link.Arguments);
        Assert.Equal("Darkaxt.Reframe.Test", link.AppUserModelId);
        Assert.Equal(spec.ToastActivatorClsid, link.ToastActivatorClsid);
        Assert.Equal([lnk + ": None"], ManagedShortcuts.Apply([spec]));

        var other = spec with { AppUserModelId = "Darkaxt.Reframe.Other", ToastActivatorClsid = Notifications.ToastActivatorClsid("Darkaxt.Reframe.Other"), Arguments = "ignored" };
        Assert.Equal([lnk + ": Update"], ManagedShortcuts.Apply([other]));
        link = ShortcutFile.ReadLink(lnk);
        Assert.Equal("Darkaxt.Reframe.Other", link.AppUserModelId);
        Assert.Equal(spec.Arguments, link.Arguments);
    }
}
