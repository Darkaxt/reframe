using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>
/// Where a start takes its profile and routes from (S14 cutover), as reframe.exe did (src/main/index.ts and
/// launch-config.ts): <c>--profile</c>, else REFRAME_USER_DATA (the managed launcher, maintenance/windows/start-managed.ps1,
/// sets it with the CLI bindings, runtime and history helper), else a direct start (autostart, a shortcut straight to
/// the executable) reads <c>reframe-config.json</c> beside the executable and sets what the launcher would have set; a
/// variable the launcher did set wins. Without any of them the shell keeps its own test profile.
/// </summary>
public static class LaunchConfig
{
    public const string FileName = "reframe-config.json";
    const string AdoptionSchema = "reframe.client-adoption.v1";

    /// <summary>A launch config's plan (evaluateLaunchConfig): its folders, the variables it gives, what it refuses.</summary>
    public sealed record Plan(string File, string UserData, string Runtime, string ClaudeHome,
        Dictionary<string, string> Env, Dictionary<string, string> Refused, List<string> Problems);

    static bool Absolute(string? p) => !string.IsNullOrEmpty(p) && Path.IsPathFullyQualified(p);

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>check: null when the binding's file has its sha256, else why it is refused.</summary>
    static string? Check(string? path, string? sha256, string what)
    {
        if (!Absolute(path)) return $"{what}: no binary configured";
        try
        {
            var actual = Sha256(path!);
            return actual == (sha256 ?? "").ToLowerInvariant() ? null : $"{what}: {path} changed (sha256 {actual})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{what}: {path} cannot be read";
        }
    }

    /// <summary>adoptedClient (src/main/client-adoption.ts): the provider's adopted binary, or why there is none.</summary>
    static (string? Path, string? Refused) Adopted(string record, string provider)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(record).TrimStart('﻿')).RootElement;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"client adoption record {record} cannot be read");
        }
        if (Str(root, "schema") != AdoptionSchema) return (null, $"{record} is not a client adoption record");
        var binding = root.TryGetProperty("clients", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty(provider, out var b) ? b : default;
        var file = Str(binding, "binary");
        var expected = Str(binding, "sha256");
        if (!Absolute(file) || expected is null) return (null, $"no adopted {provider} client in {record}");
        string actual;
        try { actual = Sha256(file!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, $"adopted {provider} client {file} cannot be read"); }
        return actual == expected.ToLowerInvariant() ? (file, null) : (null, $"adopted {provider} client {file} changed (sha256 {actual})");
    }

    /// <summary>evaluateLaunchConfig: the plan of <paramref name="file"/>, or null when it cannot be read or lacks an
    /// absolute profile, runtime or Claude folder. Changes nothing.</summary>
    public static Plan? Evaluate(string file)
    {
        JsonElement config;
        try
        {
            config = JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿')).RootElement;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        string? userData = Str(config, "userData"), runtime = Str(config, "runtime"), claudeHome = Str(config, "claudeHome");
        if (!Absolute(userData) || !Absolute(runtime) || !Absolute(claudeHome)) return null;
        var plan = new Plan(file, userData!, runtime!, claudeHome!, [], [], []);
        var adoption = Str(config, "clientAdoption");
        var chatgpt = Absolute(adoption) ? Adopted(adoption!, "chatgpt") : ((string?)null, (string?)null);
        var anthropic = Absolute(adoption) ? Adopted(adoption!, "anthropic") : ((string?)null, (string?)null);
        if (chatgpt.Item1 is { } c && anthropic.Item1 is { } a)
        {
            plan.Env["REFRAME_CLIENT_ADOPTION"] = adoption!;
            plan.Env["REFRAME_CLAUDE_BIN"] = c;
            plan.Env["REFRAME_ANTHROPIC_CLAUDE_BIN"] = a;
        }
        else
        {
            foreach (var reason in new[] { chatgpt.Item2, anthropic.Item2 }) if (reason is not null) plan.Problems.Add(reason);
            foreach (var (provider, key, what, variable) in new[] {
                ("chatgpt", "claudeBin", "ChatGPT CLI", "REFRAME_CLAUDE_BIN"),
                ("anthropic", "anthropicClaudeBin", "Anthropic CLI", "REFRAME_ANTHROPIC_CLAUDE_BIN") })
            {
                var binding = config.TryGetProperty(key, out var v) ? v : default;
                if (Check(Str(binding, "path"), Str(binding, "sha256"), what) is { } refused) plan.Refused[provider] = refused;
                else plan.Env[variable] = Str(binding, "path")!;
            }
        }
        var history = config.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var helper = history.ValueKind == JsonValueKind.Object ? Check(Str(history, "helper"), Str(history, "helperSha256"), "history helper") : "history helper: not configured";
        if (helper is null && Absolute(Str(history, "python")))
        {
            plan.Env["REFRAME_HISTORY_PYTHON"] = Str(history, "python")!;
            plan.Env["REFRAME_HISTORY_HELPER"] = Str(history, "helper")!;
            plan.Env["REFRAME_HISTORY_HELPER_SHA256"] = Str(history, "helperSha256")!;
        }
        else plan.Problems.Add(helper ?? "history helper: not configured");
        return plan;
    }

    static readonly Regex ProfileName = new("^[A-Za-z0-9-]+$");

    /// <summary>The managed launcher's fixed app id for a release under <c>&lt;root&gt;\releases\&lt;v&gt;</c> (start-managed.ps1):
    /// <c>Darkaxt.Reframe.&lt;profile folder&gt;</c> when <c>&lt;root&gt;\maintenance\start-&lt;profile&gt;.vbs</c> exists, with the Start
    /// menu shortcut and the launcher it names; null otherwise.</summary>
    public static (string AppId, string Shortcut, string Launcher)? ManagedIdentity(string executableDir, string profileDir, string programsDir)
    {
        var release = Path.GetFullPath(executableDir).TrimEnd('\\');
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(release)), "releases", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(release))!;
        var name = Path.GetFileName(Path.GetFullPath(profileDir).TrimEnd('\\'));
        var launcher = Path.Combine(root, "maintenance", $"start-{name.ToLowerInvariant()}.vbs");
        if (!ProfileName.IsMatch(name) || !File.Exists(launcher)) return null;
        return ($"Darkaxt.Reframe.{name}", Path.Combine(programsDir, $"Reframe - {name}.lnk"), launcher);
    }

    /// <summary>What a start sets in <paramref name="env"/> (the process environment): the profile, and for a direct start
    /// of a managed release the config's routes (applyLaunchPlan) and the launcher's app id variables. Returns the
    /// profile, or null when none was given (the shell's test profile applies), and the config's problems.</summary>
    public static (string? Profile, Plan? Applied) Resolve(string? profileArgument, IDictionary<string, string?> env, string executableDir, string programsDir)
    {
        if (profileArgument is not null) return (Path.GetFullPath(profileArgument), null);
        if (env.TryGetValue("REFRAME_USER_DATA", out var fromLauncher) && !string.IsNullOrEmpty(fromLauncher))
            return (Path.GetFullPath(fromLauncher), null);
        if (env.TryGetValue("REFRAME_E2E", out var e2e) && !string.IsNullOrEmpty(e2e)) return (null, null);
        if (Evaluate(Path.Combine(executableDir, FileName)) is not { } plan) return (null, null);
        Directory.CreateDirectory(plan.Runtime);
        env["REFRAME_USER_DATA"] = plan.UserData;
        env["REFRAME_RUNTIME"] = plan.Runtime;
        env["TEMP"] = env["TMP"] = plan.Runtime;
        env["REFRAME_CLAUDE_HOME"] = plan.ClaudeHome;
        foreach (var name in new[] { "CLAUDE_CONFIG_DIR", "REFRAME_CLAUDE_BIN", "REFRAME_ANTHROPIC_CLAUDE_BIN", "REFRAME_CLIENT_ADOPTION" }) env[name] = null;
        foreach (var (k, v) in plan.Env) env[k] = v;
        // The same app id (and shortcuts) as a start through the launcher, so toasts and the autostart entry match.
        if (ManagedIdentity(executableDir, plan.UserData, programsDir) is { } id)
        {
            env["REFRAME_APP_ID"] = id.AppId;
            env["REFRAME_APP_SHORTCUT"] = id.Shortcut;
            env["REFRAME_APP_LAUNCHER"] = id.Launcher;
        }
        return (plan.UserData, plan);
    }
}
