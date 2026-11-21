//! The `launch.spawnPlan` method: what reframe starts for a session, as src/main/claude/spawn-plan.ts (`spawnPlan`)
//! works it out: the program (claude found as app.ts findClaude and resolve.ts find it, on Windows), its arguments
//! (args.ts), folder, environment (env.ts, providers.ts), the hook settings file to write (settings.ts) and the
//! spawn's meta, for a Windows folder or one in WSL. Nothing is started, written or asked: the caller writes the
//! settings file and spawns. The shared fixtures in test/spawn-plan-fixtures hold both to the same results.
//! A Gemini thread runs Google's Antigravity CLI (agy) instead: its plan has the files of the session's hooks and
//! thread tools (`files`, a `.agents` folder agy reads through `--add-dir`) in place of the settings file.

use std::fs;
use std::path::Path;

use serde_json::{json, Map, Value};

/// What a plan looks at on disk.
pub trait PlanFs {
    fn is_file(&self, file: &str) -> bool;
    /// The file's text (invalid UTF-8 replaced, as Node reads it), or None when it cannot be read.
    fn read_file(&self, file: &str) -> Option<String>;
    fn exists(&self, file: &str) -> bool;
    fn is_dir(&self, dir: &str) -> bool;
    /// Whether `<home>/projects/<any>/<id>.jsonl` is there (registry.ts transcriptExists).
    fn transcript_exists(&self, home: &str, claude_session_id: &str) -> bool;
}

/// The machine's own disk.
pub struct RealFs;

impl PlanFs for RealFs {
    fn is_file(&self, file: &str) -> bool {
        fs::metadata(file).is_ok_and(|m| m.is_file())
    }
    fn read_file(&self, file: &str) -> Option<String> {
        fs::read(file).ok().map(|b| String::from_utf8_lossy(&b).into_owned())
    }
    fn exists(&self, file: &str) -> bool {
        fs::metadata(file).is_ok()
    }
    fn is_dir(&self, dir: &str) -> bool {
        fs::metadata(dir).is_ok_and(|m| m.is_dir())
    }
    fn transcript_exists(&self, home: &str, id: &str) -> bool {
        let projects = Path::new(home).join("projects");
        let Ok(dirs) = fs::read_dir(&projects) else {
            return false;
        };
        dirs.flatten()
            .any(|dir| fs::metadata(dir.path().join(format!("{id}.jsonl"))).is_ok())
    }
}

const HOOK_EVENTS: [&str; 3] = ["UserPromptSubmit", "Stop", "Notification"];
const STATUS_LINE_EVENT: &str = "StatusLine";
const HOOK_TOKEN_HEADER: &str = "X-Reframe-Token";
const FRESH_NOTE: &str = "no transcript yet, starting fresh under the same id";
const WSL_SHARES: [&str; 2] = ["\\\\wsl.localhost", "\\\\wsl$"];
const ROUTING_ENV: [&str; 15] = [
    "ANTHROPIC_BASE_URL",
    "ANTHROPIC_AUTH_TOKEN",
    "ANTHROPIC_API_KEY",
    "ANTHROPIC_MODEL",
    "ANTHROPIC_DEFAULT_OPUS_MODEL",
    "ANTHROPIC_DEFAULT_SONNET_MODEL",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL",
    "ANTHROPIC_SMALL_FAST_MODEL",
    "CLAUDE_CODE_SUBAGENT_MODEL",
    "ANTHROPIC_CUSTOM_HEADERS",
    "ANTHROPIC_CUSTOM_MODEL_OPTION",
    "ANTHROPIC_CUSTOM_MODEL_OPTION_NAME",
    "CLAUDE_CODE_USE_BEDROCK",
    "CLAUDE_CODE_USE_VERTEX",
    "CLAUDE_CODE_USE_FOUNDRY",
];
/// env.ts PARENT_SESSION_MARKERS (any case).
const PARENT_SESSION_MARKERS: [&str; 9] = [
    "CLAUDECODE",
    "CLAUDE_PID",
    "CLAUDE_CODE_CHILD_SESSION",
    "CLAUDE_CODE_SESSION_ID",
    "CLAUDE_CODE_SESSION_ATTENDED",
    "CLAUDE_CODE_BRIDGE_SESSION_ID",
    "CLAUDE_CODE_MESSAGING_SOCKET",
    "CLAUDE_CODE_MESSAGING_TOKEN",
    "CLAUDE_CODE_ENTRYPOINT",
];

type Env = Map<String, Value>;

/// JavaScript truthiness of an optional JSON value.
fn truthy(value: Option<&Value>) -> bool {
    value.is_some_and(crate::launch::truthy)
}

fn str_of<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(Value::as_str)
}

/// A non-empty string (`||` in the TypeScript).
fn nonempty<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    str_of(value, key).filter(|s| !s.is_empty())
}

fn required<'a>(value: &'a Value, key: &str) -> Result<&'a str, String> {
    str_of(value, key).ok_or_else(|| format!("{key} must be a string"))
}

fn is_js(file: &str) -> bool {
    let l = file.to_lowercase();
    l.ends_with(".js") || l.ends_with(".cjs") || l.ends_with(".mjs")
}

// ----- paths (Node's path.win32) -----

fn is_sep(c: char) -> bool {
    c == '\\' || c == '/'
}

/// path.win32.normalize.
pub fn win32_normalize(path: &str) -> String {
    let chars: Vec<char> = path.chars().collect();
    let len = chars.len();
    if len == 0 {
        return ".".into();
    }
    let mut root_end = 0;
    let mut device: Option<String> = None;
    let mut absolute = false;
    if len == 1 {
        return if is_sep(chars[0]) { "\\".into() } else { path.into() };
    }
    if is_sep(chars[0]) {
        absolute = true;
        root_end = 1;
        if is_sep(chars[1]) {
            // A UNC root: \\server\share
            let mut j = 2;
            let last = j;
            while j < len && !is_sep(chars[j]) {
                j += 1;
            }
            if j < len && j != last {
                let server: String = chars[last..j].iter().collect();
                while j < len && is_sep(chars[j]) {
                    j += 1;
                }
                if j < len {
                    let start = j;
                    while j < len && !is_sep(chars[j]) {
                        j += 1;
                    }
                    let share: String = chars[start..j].iter().collect();
                    if j == len {
                        return format!("\\\\{server}\\{share}\\");
                    }
                    device = Some(format!("\\\\{server}\\{share}"));
                    root_end = j;
                }
            }
        }
    } else if chars[0].is_ascii_alphabetic() && chars[1] == ':' {
        device = Some(chars[..2].iter().collect());
        root_end = 2;
        if len > 2 && is_sep(chars[2]) {
            absolute = true;
            root_end = 3;
        }
    }
    let rest: String = chars[root_end..].iter().collect();
    let mut stack: Vec<&str> = Vec::new();
    for part in rest.split(is_sep) {
        match part {
            "" | "." => {}
            ".." => {
                if stack.last().is_some_and(|p| *p != "..") {
                    stack.pop();
                } else if !absolute {
                    stack.push("..");
                }
            }
            p => stack.push(p),
        }
    }
    let mut tail = stack.join("\\");
    if tail.is_empty() && !absolute {
        tail = ".".into();
    }
    if !tail.is_empty() && is_sep(chars[len - 1]) {
        tail.push('\\');
    }
    match device {
        None if absolute => format!("\\{tail}"),
        None => tail,
        Some(d) if absolute => format!("{d}\\{tail}"),
        Some(d) => format!("{d}{tail}"),
    }
}

/// path.win32.join.
pub fn win32_join(parts: &[&str]) -> String {
    let joined: Vec<&str> = parts.iter().copied().filter(|p| !p.is_empty()).collect();
    if joined.is_empty() {
        return ".".into();
    }
    win32_normalize(&joined.join("\\"))
}

/// path.win32.dirname, for the files resolve.ts takes it of.
fn win32_dirname(path: &str) -> String {
    let chars: Vec<char> = path.chars().collect();
    let len = chars.len();
    if len == 0 {
        return ".".into();
    }
    let mut root_end = 0;
    if is_sep(chars[0]) {
        root_end = 1;
        if len > 1 && is_sep(chars[1]) {
            // \\server\share\ is the root
            let mut j = 2;
            while j < len && !is_sep(chars[j]) {
                j += 1;
            }
            if j < len && j > 2 {
                while j < len && is_sep(chars[j]) {
                    j += 1;
                }
                if j < len {
                    while j < len && !is_sep(chars[j]) {
                        j += 1;
                    }
                    if j == len {
                        return path.into();
                    }
                    root_end = j + 1;
                }
            }
        }
    } else if len >= 2 && chars[0].is_ascii_alphabetic() && chars[1] == ':' {
        root_end = if len > 2 && is_sep(chars[2]) { 3 } else { 2 };
    }
    let mut end: Option<usize> = None;
    let mut matched = true;
    let mut i = len;
    while i > root_end {
        i -= 1;
        if is_sep(chars[i]) {
            if !matched {
                end = Some(i);
                break;
            }
        } else {
            matched = false;
        }
    }
    match end {
        Some(e) => chars[..e].iter().collect(),
        None if root_end == 0 => ".".into(),
        None => chars[..root_end].iter().collect(),
    }
}

/// encodeURIComponent.
fn encode_uri_component(text: &str) -> String {
    let mut out = String::new();
    for c in text.chars() {
        if c.is_ascii_alphanumeric() || "-_.!~*'()".contains(c) {
            out.push(c);
        } else {
            let mut buf = [0u8; 4];
            for b in c.encode_utf8(&mut buf).bytes() {
                out.push_str(&format!("%{b:02X}"));
            }
        }
    }
    out
}

// ----- WSL (src/shared/wsl.ts) -----

fn trim_seps_end(s: &str) -> &str {
    s.trim_end_matches(is_sep)
}

/// parseWslPath: the distribution and Linux path of a folder in WSL.
fn parse_wsl_path(windows_path: &str, share: &str) -> Option<(String, String)> {
    let normalized = windows_path.replace('/', "\\");
    let roots: Vec<&str> = (!share.is_empty())
        .then_some(share)
        .into_iter()
        .chain(WSL_SHARES)
        .collect();
    for root in roots {
        let prefix = format!("{}\\", trim_seps_end(root));
        let n = prefix.chars().count();
        let head: String = normalized.chars().take(n).collect();
        if head.to_lowercase() != prefix.to_lowercase() {
            continue;
        }
        let rest: String = normalized.chars().skip(n).collect();
        let mut parts = rest.split('\\').filter(|p| !p.is_empty());
        let distro = parts.next()?.to_owned();
        let path = format!("/{}", parts.collect::<Vec<_>>().join("/"));
        return Some((distro, path));
    }
    None
}

fn wsl_windows_path(distro: &str, linux_path: &str, share: &str) -> String {
    let mut parts = vec![trim_seps_end(share), distro];
    parts.extend(linux_path.split('/').filter(|p| !p.is_empty()));
    parts.join("\\")
}

// ----- the command line (args.ts, resolve.ts cmdCommandLine) -----

fn claude_args(session: &Value, mode: &str, settings: Option<&str>, prompt: bool) -> Vec<String> {
    let id = str_of(session, "claudeSessionId").unwrap_or("").to_owned();
    let mut args = if mode == "new" {
        vec!["--session-id".to_owned(), id]
    } else {
        vec!["--resume".to_owned(), id]
    };
    let launch = session.get("launch").cloned().unwrap_or(Value::Null);
    let name = match session.get("claudeName") {
        None => session.get("name"),
        some => some,
    };
    if let Some(Value::String(name)) = name.filter(|n| truthy(Some(n))) {
        args.extend(["--name".into(), name.clone()]);
    }
    if let Some(model) = nonempty(&launch, "model") {
        args.extend(["--model".into(), model.into()]);
    } else if str_of(&launch, "provider") == Some("anthropic") {
        args.extend(["--model".into(), "sonnet".into()]);
    }
    if let Some(mode) = nonempty(&launch, "permissionMode") {
        args.extend(["--permission-mode".into(), mode.into()]);
    }
    if let Some(effort) = nonempty(&launch, "effort") {
        args.extend(["--effort".into(), effort.into()]);
    }
    if mode == "new" && truthy(launch.get("worktree")) && !truthy(launch.get("worktreeCreated")) {
        args.push("--worktree".into());
        if let Some(name) = nonempty(&launch, "worktreeName") {
            args.push(name.into());
        }
    }
    for dir in launch.get("addDirs").and_then(Value::as_array).into_iter().flatten() {
        if let Some(dir) = dir.as_str() {
            args.extend(["--add-dir".into(), dir.into()]);
        }
    }
    // Claude in Chrome: on or off by the user's setting; absent leaves it to the CLI's own.
    match launch.get("chrome").and_then(Value::as_bool) {
        Some(true) => args.push("--chrome".into()),
        Some(false) => args.push("--no-chrome".into()),
        None => {}
    }
    if let Some(settings) = settings.filter(|s| !s.is_empty()) {
        args.extend(["--settings".into(), settings.into()]);
    }
    if let Some(text) = nonempty(&launch, "initialPrompt") {
        if mode == "new" && !truthy(session.get("used")) && prompt {
            args.extend(["--".into(), text.into()]);
        }
    }
    args
}

fn cmd_command_line(script: &str, args: &[String]) -> String {
    let plain = |a: &str| {
        !a.is_empty()
            && a.chars()
                .all(|c| c.is_ascii_alphanumeric() || "_-.:\\/=@+,".contains(c))
    };
    let quoted: Vec<String> = std::iter::once(script)
        .chain(args.iter().map(String::as_str))
        .map(|a| {
            if plain(a) {
                a.to_owned()
            } else {
                format!("\"{}\"", a.replace('"', ""))
            }
        })
        .collect();
    format!("/d /s /c \"{}\"", quoted.join(" "))
}

// ----- the environment (env.ts, providers.ts) -----

fn claude_env(base: &Env, extra: &Env) -> Env {
    let mut env = Env::new();
    for (key, value) in base {
        if value.is_string() && !PARENT_SESSION_MARKERS.iter().any(|m| m.eq_ignore_ascii_case(key)) {
            env.insert(key.clone(), value.clone());
        }
    }
    for (key, value) in extra {
        env.insert(key.clone(), value.clone());
    }
    env
}

fn provider_env(mut env: Env, provider: Option<&str>) -> Env {
    if provider != Some("anthropic") {
        return env;
    }
    let keys: Vec<String> = env
        .keys()
        .filter(|k| ROUTING_ENV.iter().any(|r| r.eq_ignore_ascii_case(k)))
        .cloned()
        .collect();
    for key in keys {
        env.shift_remove(&key);
    }
    env.insert("ANTHROPIC_BASE_URL".into(), json!("https://api.anthropic.com"));
    env
}

fn provider_settings(provider: Option<&str>) -> Map<String, Value> {
    let mut out = Map::new();
    if provider != Some("anthropic") {
        return out;
    }
    let env: Map<String, Value> = ROUTING_ENV
        .iter()
        .map(|k| {
            let v = if *k == "ANTHROPIC_BASE_URL" {
                "https://api.anthropic.com"
            } else {
                ""
            };
            ((*k).to_owned(), json!(v))
        })
        .collect();
    out.insert("env".into(), Value::Object(env));
    out.insert("apiKeyHelper".into(), json!(""));
    out.insert("model".into(), json!("sonnet"));
    out
}

/// settings.ts hookSettingsJson.
fn hook_settings_json(
    port: u64,
    token: &str,
    session_id: &str,
    provider: Option<&str>,
    status_line: Option<Value>,
    message_tag: Option<Value>,
) -> String {
    let id = encode_uri_component(session_id);
    let mut hooks = Map::new();
    for event in HOOK_EVENTS {
        hooks.insert(
            event.into(),
            json!([{ "hooks": [{
                "type": "http",
                "url": format!("http://127.0.0.1:{port}/hook/{id}/{event}"),
                "headers": { HOOK_TOKEN_HEADER: token },
            }] }]),
        );
    }
    if let Some(tag) = message_tag {
        hooks.insert("PreToolUse".into(), tag);
    }
    let mut out = Map::new();
    out.insert("hooks".into(), Value::Object(hooks));
    if let Some(line) = status_line {
        out.insert("statusLine".into(), line);
    }
    out.extend(provider_settings(provider));
    serde_json::to_string_pretty(&Value::Object(out)).unwrap_or_default()
}

/// The status line that hands Claude Code's status line input (its `rate_limits`: the quota) to reframe. Without a
/// status line of the user's own, curl posts it to the hook server. With one, the host's relay (`relay`, the host's
/// executable) posts it, then runs the user's command with the same input and prints what it prints, so the user's
/// status line stays as it is; without a relay (a WSL session) the user's status line is left alone.
fn status_line_settings(
    fs: &dyn PlanFs,
    port: u64,
    token: &str,
    session_id: &str,
    claude_home: &str,
    relay: Option<&str>,
) -> Option<Value> {
    let id = encode_uri_component(session_id);
    let url = format!("http://127.0.0.1:{port}/hook/{id}/{STATUS_LINE_EVENT}");
    let curl = || {
        Some(json!({
            "type": "command",
            "command": format!("curl.exe -s -m 2 -o NUL -X POST -H \"{HOOK_TOKEN_HEADER}: {token}\" -H \"content-type: application/json\" --data-binary @- \"{url}\""),
        }))
    };
    let file = win32_join(&[claude_home, "settings.json"]);
    if !fs.exists(&file) {
        return curl();
    }
    // Settings that cannot be read, are not JSON or are JSON `null` count as having a status line: left alone.
    let settings: Value = serde_json::from_str(&fs.read_file(&file)?).ok()?;
    if settings.is_null() {
        return None;
    }
    let line = settings.get("statusLine");
    if !truthy(line) {
        return curl();
    }
    let mut line = line?.clone();
    let command = line.get("command").and_then(Value::as_str)?;
    if line.get("type").and_then(Value::as_str) != Some("command") || command.trim().is_empty() {
        return None;
    }
    let hex: String = command.bytes().map(|b| format!("{b:02x}")).collect();
    line["command"] = json!(format!("\"{}\" {STATUS_LINE_RELAY_ARG} {url} {token} {hex}", relay?));
    Some(line)
}

/// The PreToolUse hook that tags what the session sends another thread (SendMessage) with the provider and the
/// exact model it runs on (reframe-host's msgtag.rs), so the receiving thread knows whether a Claude or an OpenAI
/// model wrote it.
fn message_tag_hook(relay: &str, provider: Option<&str>) -> Value {
    let provider = if provider == Some("anthropic") {
        "anthropic"
    } else {
        "openai"
    };
    json!([{ "matcher": "SendMessage", "hooks": [{
        "type": "command",
        "command": format!("\"{relay}\" {MESSAGE_TAG_ARG} {provider}"),
        "timeout": 5,
    }] }])
}

/// The MCP server (`--mcp-config`, as JSON) of the host's thread tools (reframe-host's threads.rs): its `open_thread`
/// asks reframe, through the hook server, to open another thread with a first prompt. Not for WSL sessions, nor for a
/// claude that runs through cmd.exe (whose command line would have to carry the JSON's quotes).
/// With `tasks_dir` (the profile's scheduled tasks), the server also has the task tools.
fn thread_tools_config(relay: &str, port: u64, token: &str, session_id: &str, tasks_dir: Option<&str>) -> String {
    let args = thread_tools_args(port, token, session_id, tasks_dir);
    json!({ "mcpServers": { "reframe": { "type": "stdio", "command": relay, "args": args } } }).to_string()
}

fn thread_tools_args(port: u64, token: &str, session_id: &str, tasks_dir: Option<&str>) -> Vec<String> {
    let url = format!(
        "http://127.0.0.1:{port}/hook/{}/OpenThread",
        encode_uri_component(session_id)
    );
    let mut args = vec![THREAD_TOOLS_ARG.to_owned(), url, token.to_owned()];
    args.extend(tasks_dir.map(str::to_owned));
    args
}

/// The host executable's argument that makes it the thread tools' MCP server (reframe-host's threads.rs).
pub const THREAD_TOOLS_ARG: &str = "--thread-tools";

/// The host executable's argument that makes it the message tagger (reframe-host's msgtag.rs).
pub const MESSAGE_TAG_ARG: &str = "--tag-message";

/// The host executable's argument that makes it the status line relay (reframe-host's statusline.rs).
pub const STATUS_LINE_RELAY_ARG: &str = "--status-line-relay";

// ----- finding claude (resolve.ts, app.ts findClaude) -----

struct Binary {
    file: String,
    prefix_args: Vec<String>,
    env: Env,
    source: String,
    via_cmd: Option<String>,
}

fn plain_binary(file: String, source: &str) -> Binary {
    Binary {
        file,
        prefix_args: vec![],
        env: Env::new(),
        source: source.into(),
        via_cmd: None,
    }
}

/// The `"%dp0%\x"` / `"%~dp0\x"` targets of a batch file.
fn batch_targets(text: &str) -> Vec<String> {
    let chars: Vec<char> = text.chars().collect();
    let mut out = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '"' {
            let mut j = i + 1;
            let at = |j: usize, c: char| chars.get(j).is_some_and(|x| x.eq_ignore_ascii_case(&c));
            if at(j, '%') {
                j += 1;
                if at(j, '~') {
                    j += 1;
                }
                if at(j, 'd') && at(j + 1, 'p') && at(j + 2, '0') {
                    j += 3;
                    if at(j, '%') {
                        j += 1;
                    }
                    if at(j, '\\') {
                        j += 1;
                        let start = j;
                        while j < chars.len() && chars[j] != '"' {
                            j += 1;
                        }
                        if j < chars.len() && j > start {
                            out.push(chars[start..j].iter().collect());
                            i = j + 1;
                            continue;
                        }
                    }
                }
            }
        }
        i += 1;
    }
    out
}

fn wrap(file: &str, fs: &dyn PlanFs, find_node: &dyn Fn() -> Option<String>) -> Binary {
    let lower = file.to_lowercase();
    if !(lower.ends_with(".cmd") || lower.ends_with(".bat")) {
        return plain_binary(file.into(), file);
    }
    let text = fs.read_file(file).unwrap_or_default();
    let dir = win32_dirname(file);
    let targets = batch_targets(&text);
    if let Some(script) = targets.iter().find(|t| is_js(t)) {
        let beside = win32_join(&[&dir, "node.exe"]);
        let node = if fs.is_file(&beside) { Some(beside) } else { find_node() };
        if let Some(node) = node {
            return Binary {
                file: node,
                prefix_args: vec![win32_join(&[&dir, script])],
                env: Env::new(),
                source: file.into(),
                via_cmd: None,
            };
        }
    }
    let program = targets.iter().find(|t| {
        let l = t.to_lowercase();
        l.ends_with(".exe") && !(l == "node.exe" || l.ends_with("\\node.exe"))
    });
    if let Some(program) = program {
        return plain_binary(win32_join(&[&dir, program]), file);
    }
    Binary {
        via_cmd: Some(file.into()),
        ..plain_binary("cmd.exe".into(), file)
    }
}

fn resolve(env: &Env, override_: Option<&str>, node_runner: Option<(String, Env)>, fs: &dyn PlanFs) -> Option<Binary> {
    if let Some(over) = override_.filter(|o| !o.is_empty()) {
        if !fs.is_file(over) {
            return None;
        }
        if is_js(over) {
            let (file, env) = node_runner?;
            return Some(Binary {
                file,
                prefix_args: vec![over.into()],
                env,
                source: over.into(),
                via_cmd: None,
            });
        }
        return Some(wrap(over, fs, &|| None));
    }
    let dirs = path_dirs(env);
    let exts: Vec<String> = match env.get("PATHEXT").and_then(Value::as_str) {
        Some(p) => p.split(';').filter(|e| !e.is_empty()).map(str::to_lowercase).collect(),
        None => vec![".com".into(), ".exe".into(), ".bat".into(), ".cmd".into()],
    };
    let on_path = |name: &str, only: &[String]| -> Option<String> {
        for dir in &dirs {
            for ext in only {
                let candidate = win32_join(&[dir, &format!("{name}{ext}")]);
                if fs.is_file(&candidate) {
                    return Some(candidate);
                }
            }
        }
        None
    };
    let claude = on_path("claude", &exts)?;
    let node_exe = [".exe".to_owned()];
    Some(wrap(&claude, fs, &|| on_path("node", &node_exe)))
}

/// The folders of the environment's PATH (any case), quotes taken off.
fn path_dirs(env: &Env) -> Vec<String> {
    let path = env
        .iter()
        .find(|(k, _)| k.to_uppercase() == "PATH")
        .and_then(|(_, v)| v.as_str())
        .unwrap_or("");
    path.split(';')
        .map(|d| {
            let d = d.trim();
            if d.len() >= 2 && d.starts_with('"') && d.ends_with('"') {
                d[1..d.len() - 1].to_owned()
            } else {
                d.to_owned()
            }
        })
        .filter(|d| !d.is_empty())
        .collect()
}

/// An environment variable (any case), unless empty.
fn env_value<'a>(env: &'a Env, key: &str) -> Option<&'a str> {
    env.iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(key))
        .and_then(|(_, v)| v.as_str())
        .filter(|s| !s.is_empty())
}

// ----- Antigravity CLI (agy): Gemini threads -----

/// The host executable's argument that makes it the relay of agy's hooks (reframe-host's agyhook.rs).
pub const AGY_HOOK_ARG: &str = "--agy-hook";

/// Where agy keeps its conversations: `<USERPROFILE>\.gemini\antigravity-cli`, unless the input names it (`agyHome`).
fn agy_home(input: &Value, env: &Env) -> Option<String> {
    if let Some(home) = nonempty(input, "agyHome") {
        return Some(home.to_owned());
    }
    Some(win32_join(&[
        env_value(env, "USERPROFILE")?,
        ".gemini",
        "antigravity-cli",
    ]))
}

/// The transcript agy writes for a conversation (the Chat view reads the same file).
pub fn agy_transcript(home: &str, conversation_id: &str) -> String {
    win32_join(&[
        home,
        "brain",
        conversation_id,
        ".system_generated",
        "logs",
        "transcript_full.jsonl",
    ])
}

/// agy: `agyBin` or REFRAME_AGY_BIN when given (and there), else agy.exe on PATH, else in winget's Links folder
/// (where `winget install Google.AntigravityCLI` puts it).
fn find_agy(input: &Value, env: &Env, fs: &dyn PlanFs) -> Option<String> {
    if let Some(over) = nonempty(input, "agyBin").or_else(|| env_value(env, "REFRAME_AGY_BIN")) {
        return fs.is_file(over).then(|| over.to_owned());
    }
    path_dirs(env)
        .iter()
        .map(|dir| win32_join(&[dir, "agy.exe"]))
        .chain(env_value(env, "LOCALAPPDATA").map(|l| win32_join(&[l, "Microsoft", "WinGet", "Links", "agy.exe"])))
        .find(|file| fs.is_file(file))
}

/// agy's arguments: the conversation resumed, the model (its id carries the effort: gemini-4-neon-high), the permission
/// mode, the folders (the hooks' own first) and a new thread's first prompt (`-i`: the session stays open after it).
fn agy_args(session: &Value, mode: &str, root: Option<&str>) -> Vec<String> {
    let launch = session.get("launch").cloned().unwrap_or(Value::Null);
    let mut args: Vec<String> = Vec::new();
    if mode == "resume" {
        args.extend([
            "--conversation".into(),
            str_of(session, "claudeSessionId").unwrap_or("").to_owned(),
        ]);
    }
    if let Some(model) = nonempty(&launch, "model") {
        args.extend(["--model".into(), model.into()]);
    }
    match nonempty(&launch, "permissionMode") {
        Some("bypassPermissions") => args.push("--dangerously-skip-permissions".into()),
        Some("acceptEdits") => args.extend(["--mode".into(), "accept-edits".into()]),
        Some(mode @ ("plan" | "auto")) => args.extend(["--mode".into(), mode.into()]),
        _ => {}
    }
    let dirs = launch
        .get("addDirs")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(Value::as_str);
    for dir in root.into_iter().chain(dirs) {
        args.extend(["--add-dir".into(), dir.into()]);
    }
    if let Some(text) = nonempty(&launch, "initialPrompt") {
        if mode == "new" && !truthy(session.get("used")) {
            args.extend(["-i".into(), text.into()]);
        }
    }
    args
}

/// The session's `.agents/hooks.json`: the hooks its status follows, run through the host's relay (agy runs a hook's
/// command through cmd.exe in that folder, with the session's environment).
fn agy_hooks_json(relay: &str, port: u64, token: &str, session_id: &str) -> String {
    let base = format!("http://127.0.0.1:{port}/hook/{}/", encode_uri_component(session_id));
    let handler = |event: &str| json!([{ "command": format!("\"{relay}\" {AGY_HOOK_ARG} {base} {token} {event}"), "timeout": 10 }]);
    let hooks = json!({ "reframe": {
        "SessionStart": handler("SessionStart"),
        "PreInvocation": handler("PreInvocation"),
        "Stop": handler("Stop"),
    } });
    serde_json::to_string_pretty(&hooks).unwrap_or_default()
}

/// The session's `.agents/mcp_config.json`: the host's thread tools, as `--mcp-config` gives them to claude.
fn agy_mcp_config(relay: &str, port: u64, token: &str, session_id: &str, tasks_dir: Option<&str>) -> String {
    let config = json!({ "mcpServers": { "reframe": {
        "command": relay,
        "args": thread_tools_args(port, token, session_id, tasks_dir),
    } } });
    serde_json::to_string_pretty(&config).unwrap_or_default()
}

fn env_of(value: Option<&Value>) -> Env {
    value.and_then(Value::as_object).cloned().unwrap_or_default()
}

fn find_claude(
    input: &Value,
    provider: Option<&str>,
    env: &Env,
    fs: &dyn PlanFs,
    warnings: &mut Vec<String>,
) -> Option<Binary> {
    let which = provider.unwrap_or("chatgpt");
    if let Some(refused) = nonempty(input, "refused") {
        warnings.push(format!("provider refused by launch config: {which}: {refused}"));
        return None;
    }
    let node = match input.get("nodeRunner").filter(|v| v.is_object()) {
        Some(runner) => Some((
            str_of(runner, "file").unwrap_or("").to_owned(),
            env_of(runner.get("env")),
        )),
        None => env
            .get("REFRAME_NODE")
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
            .map(|n| (n.to_owned(), Env::new())),
    };
    let env_str = |k: &str| env.get(k).and_then(Value::as_str).filter(|s| !s.is_empty());
    let over: Option<String> = match input.get("adopted").filter(|v| v.is_object()) {
        Some(adopted) => {
            if adopted.get("ok") != Some(&Value::Bool(true)) {
                let reason = str_of(adopted, "reason").unwrap_or("");
                warnings.push(format!("adopted client refused: {which}: {reason}"));
                return None;
            }
            str_of(adopted, "path").map(str::to_owned)
        }
        None => (if provider == Some("anthropic") {
            env_str("REFRAME_ANTHROPIC_CLAUDE_BIN")
        } else {
            None
        })
        .or_else(|| nonempty(input, "claudeBin"))
        .or_else(|| env_str("REFRAME_CLAUDE_BIN"))
        .map(str::to_owned),
    };
    if let Some(o) = over.as_deref().filter(|o| !o.is_empty() && is_js(o)) {
        if node.is_none() {
            warnings.push(format!(
                "a .js claude needs a node to run it (nodeRunner or REFRAME_NODE): {o}"
            ));
            return None;
        }
    }
    resolve(env, over.as_deref(), node, fs)
}

// ----- the plan -----

/// `launch.spawnPlan`: the plan for `input` (docs/ptyhost-protocol.md), or why the input is not one.
pub fn spawn_plan(input: &Value, fs: &dyn PlanFs) -> Result<Value, String> {
    let mut session = input
        .get("session")
        .filter(|s| s.is_object())
        .cloned()
        .ok_or("session must be an object")?;
    for key in ["id", "claudeSessionId", "cwd"] {
        required(&session, key).map_err(|e| format!("session.{e}"))?;
    }
    let requested_mode = required(input, "mode")?;
    let fresh = requested_mode == "fresh";
    let requested = match requested_mode {
        "new" | "fresh" => "new",
        "resume" => "resume",
        other => return Err(format!("mode {other} is not new, resume or fresh")),
    };
    let mut patch = None;
    if fresh {
        let id = nonempty(input, "freshClaudeSessionId").ok_or("freshClaudeSessionId is required")?;
        session["claudeSessionId"] = json!(id);
        session["used"] = json!(false);
        patch = Some(json!({ "claudeSessionId": id, "used": false }));
    }
    let env = input
        .get("env")
        .and_then(Value::as_object)
        .cloned()
        .ok_or("env must be an object")?;
    let claude_home = required(input, "claudeHome")?;
    let settings_dir = required(input, "settingsDir")?;
    let hooks = input
        .get("hooks")
        .filter(|h| h.is_object())
        .ok_or("hooks must be an object")?;
    let port = hooks
        .get("port")
        .and_then(Value::as_u64)
        .ok_or("hooks.port must be a number")?;
    let token = required(hooks, "token").map_err(|e| format!("hooks.{e}"))?;
    let id = str_of(&session, "id").unwrap_or("").to_owned();
    let claude_session_id = str_of(&session, "claudeSessionId").unwrap_or("").to_owned();
    let cwd = str_of(&session, "cwd").unwrap_or("").to_owned();
    let provider = session
        .get("launch")
        .and_then(|l| str_of(l, "provider"))
        .map(str::to_owned);
    let provider = provider.as_deref();
    let used = truthy(session.get("used"));
    let mut warnings: Vec<String> = Vec::new();
    let fail = |error: &str, warnings: Vec<String>| Ok(json!({ "ok": false, "error": error, "warnings": warnings }));
    let launch_mode = |transcript: bool| -> Option<&'static str> {
        if requested != "resume" || transcript {
            Some(requested)
        } else if used {
            None
        } else {
            Some("new")
        }
    };
    let finish = |mut plan: Map<String, Value>, patch: Option<Value>, warnings: Vec<String>| {
        if let Some(patch) = patch {
            plan.insert("patch".into(), patch);
        }
        plan.insert("warnings".into(), json!(warnings));
        Ok(Value::Object(plan))
    };

    let wsl = input.get("wsl").filter(|w| w.is_object());
    let share = wsl.and_then(|w| str_of(w, "share")).unwrap_or("");

    // A Gemini thread: agy, in a Windows folder.
    if provider == Some("gemini") {
        if wsl.is_some() && parse_wsl_path(&cwd, share).is_some() {
            warnings.push("Gemini threads (Antigravity CLI) run in Windows folders only".into());
            return fail("agyNotFound", warnings);
        }
        if !fs.exists(&cwd) {
            return fail("folderMissing", warnings);
        }
        let home = agy_home(input, &env).unwrap_or_default();
        let Some(mode) = launch_mode(requested != "resume" || fs.is_file(&agy_transcript(&home, &claude_session_id)))
        else {
            return fail("transcriptMissing", warnings);
        };
        let Some(agy) = find_agy(input, &env, fs) else {
            return fail("agyNotFound", warnings);
        };
        if mode != requested {
            warnings.push(FRESH_NOTE.into());
        }
        // The hooks and thread tools: in a folder of the session's own, beside the hook settings of claude's sessions.
        let relay = nonempty(input, "statusLineRelay");
        let root = relay.map(|_| win32_join(&[settings_dir, &format!("{id}-agy")]));
        let mut files = Vec::new();
        if let (Some(relay), Some(root)) = (relay, root.as_deref()) {
            files.push(json!({ "file": win32_join(&[root, ".agents", "hooks.json"]), "content": agy_hooks_json(relay, port, token, &id) }));
            let tools = agy_mcp_config(relay, port, token, &id, nonempty(input, "tasksDir"));
            files.push(json!({ "file": win32_join(&[root, ".agents", "mcp_config.json"]), "content": tools }));
        }
        let mut extra = Env::new();
        extra.insert("REFRAME_SESSION".into(), json!(id));
        let mut plan = Map::new();
        plan.insert("ok".into(), json!(true));
        plan.insert("mode".into(), json!(mode));
        plan.insert("file".into(), json!(agy));
        plan.insert("args".into(), json!(agy_args(&session, mode, root.as_deref())));
        plan.insert("cwd".into(), json!(cwd));
        plan.insert("env".into(), Value::Object(claude_env(&env, &extra)));
        plan.insert("files".into(), Value::Array(files));
        plan.insert(
            "meta".into(),
            json!({ "agy": { "home": home, "conversationId": claude_session_id } }),
        );
        return finish(plan, patch, warnings);
    }
    if let (Some(wsl), Some((distro, linux_path))) = (wsl, wsl.and_then(|_| parse_wsl_path(&cwd, share))) {
        let info = wsl.get("info").filter(|i| i.is_object());
        let Some((info, info_claude)) = info.and_then(|i| nonempty(i, "claude").map(|c| (i, c))) else {
            return fail("claudeNotFound", warnings);
        };
        if !fs.is_dir(&cwd) {
            return fail("folderMissing", warnings);
        }
        let home = wsl_windows_path(&distro, str_of(info, "claudeHome").unwrap_or(""), share);
        let Some(mode) = launch_mode(requested != "resume" || fs.transcript_exists(&home, &claude_session_id)) else {
            return fail("transcriptMissing", warnings);
        };
        if mode != requested {
            warnings.push(FRESH_NOTE.into());
        }
        let command = wsl.get("command").cloned().unwrap_or(Value::Null);
        let mut args: Vec<Value> = command
            .get("prefixArgs")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        for a in ["-d", &distro, "--cd", &linux_path, "--exec", "env"] {
            args.push(json!(a));
        }
        args.push(json!(format!("PATH={}", str_of(info, "path").unwrap_or(""))));
        args.push(json!(info_claude));
        let settings = (provider == Some("anthropic"))
            .then(|| serde_json::to_string(&Value::Object(provider_settings(Some("anthropic")))).unwrap_or_default());
        args.extend(
            claude_args(&session, mode, settings.as_deref(), true)
                .into_iter()
                .map(Value::String),
        );
        let mut plan = Map::new();
        plan.insert("ok".into(), json!(true));
        plan.insert("mode".into(), json!(mode));
        plan.insert("file".into(), command.get("file").cloned().unwrap_or(json!("")));
        plan.insert("args".into(), Value::Array(args));
        plan.insert("cwd".into(), json!(str_of(wsl, "homeDir").unwrap_or("")));
        plan.insert(
            "env".into(),
            Value::Object(claude_env(&env, &env_of(command.get("env")))),
        );
        plan.insert(
            "meta".into(),
            json!({ "wsl": { "distro": distro, "home": home, "claudeSessionId": claude_session_id, "share": share } }),
        );
        return finish(plan, patch, warnings);
    }

    if !fs.exists(&cwd) {
        return fail("folderMissing", warnings);
    }
    let Some(mode) = launch_mode(requested != "resume" || fs.transcript_exists(claude_home, &claude_session_id)) else {
        return fail("transcriptMissing", warnings);
    };
    let Some(claude) = find_claude(input, provider, &env, fs, &mut warnings) else {
        return fail("claudeNotFound", warnings);
    };
    if mode != requested {
        warnings.push(FRESH_NOTE.into());
    }
    if claude.via_cmd.is_some() {
        warnings.push(format!(
            "claude is a launcher reframe does not know, it runs through cmd.exe: {}",
            claude.source
        ));
    }
    let file = win32_join(&[settings_dir, &format!("{id}.json")]);
    let capture = provider == Some("anthropic") && truthy(input.get("captureStatusLine"));
    let in_wsl = input.get("wsl").is_some_and(Value::is_object);
    let relay = nonempty(input, "statusLineRelay").filter(|_| !in_wsl);
    let status_line = capture
        .then(|| status_line_settings(fs, port, token, &id, claude_home, relay))
        .flatten();
    let content = hook_settings_json(
        port,
        token,
        &id,
        provider,
        status_line,
        relay.map(|relay| message_tag_hook(relay, provider)),
    );
    let args = match &claude.via_cmd {
        Some(script) => json!(cmd_command_line(
            script,
            &claude_args(&session, mode, Some(&file), false)
        )),
        None => {
            let mut args = claude.prefix_args.clone();
            if let Some(relay) = relay {
                args.extend([
                    "--mcp-config".into(),
                    thread_tools_config(relay, port, token, &id, nonempty(input, "tasksDir")),
                ]);
            }
            args.extend(claude_args(&session, mode, Some(&file), true));
            json!(args)
        }
    };
    let mut extra = claude.env.clone();
    extra.insert("REFRAME_SESSION".into(), json!(id));
    let mut meta = Map::new();
    meta.insert("claudeHome".into(), json!(claude_home));
    if claude.via_cmd.is_some() {
        meta.insert("launched".into(), json!({ "claudeSessionId": claude_session_id }));
    }
    let mut plan = Map::new();
    plan.insert("ok".into(), json!(true));
    plan.insert("mode".into(), json!(mode));
    plan.insert("file".into(), json!(claude.file));
    plan.insert("args".into(), args);
    plan.insert("cwd".into(), json!(cwd));
    plan.insert(
        "env".into(),
        Value::Object(provider_env(claude_env(&env, &extra), provider)),
    );
    plan.insert("settings".into(), json!({ "file": file, "content": content }));
    plan.insert("meta".into(), Value::Object(meta));
    finish(plan, patch, warnings)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn claude_in_chrome_is_set_by_the_launch_not_left_to_the_cli_when_chosen() {
        let args_of = |launch: Value| {
            let session = json!({ "claudeSessionId": "s1", "launch": launch });
            claude_args(&session, "new", None, false)
        };
        let on = args_of(json!({ "provider": "anthropic", "chrome": true }));
        assert!(on.contains(&"--chrome".to_owned()) && !on.contains(&"--no-chrome".to_owned()));
        let off = args_of(json!({ "provider": "anthropic", "chrome": false }));
        assert!(off.contains(&"--no-chrome".to_owned()) && !off.contains(&"--chrome".to_owned()));
        let unset = args_of(json!({ "provider": "anthropic" }));
        assert!(!unset.iter().any(|a| a == "--chrome" || a == "--no-chrome"));
    }

    #[test]
    fn the_host_gives_sessions_its_thread_tools() {
        let config: Value =
            serde_json::from_str(&thread_tools_config("C:/h/reframe-host.exe", 4100, "tok", "s 1", None)).unwrap();
        assert_eq!(
            config,
            json!({ "mcpServers": { "reframe": { "type": "stdio", "command": "C:/h/reframe-host.exe",
                "args": ["--thread-tools", "http://127.0.0.1:4100/hook/s%201/OpenThread", "tok"] } } })
        );
        let config: Value = serde_json::from_str(&thread_tools_config(
            "C:/h/reframe-host.exe",
            4100,
            "tok",
            "s",
            Some("D:/p/tasks"),
        ))
        .unwrap();
        assert_eq!(
            config["mcpServers"]["reframe"]["args"],
            json!([
                "--thread-tools",
                "http://127.0.0.1:4100/hook/s/OpenThread",
                "tok",
                "D:/p/tasks"
            ])
        );
    }

    #[test]
    fn messages_to_other_threads_are_tagged_by_the_host() {
        let hook = message_tag_hook("C:/h/reframe-host.exe", None);
        let content: Value =
            serde_json::from_str(&hook_settings_json(4100, "tok", "s", None, None, Some(hook))).unwrap();
        let pre = &content["hooks"]["PreToolUse"][0];
        assert_eq!(pre["matcher"], json!("SendMessage"));
        assert_eq!(
            pre["hooks"][0]["command"],
            json!("\"C:/h/reframe-host.exe\" --tag-message openai")
        );
        let anthropic = message_tag_hook("C:/h/reframe-host.exe", Some("anthropic"));
        assert!(anthropic[0]["hooks"][0]["command"]
            .as_str()
            .unwrap()
            .ends_with(" anthropic"));
        let none: Value = serde_json::from_str(&hook_settings_json(4100, "tok", "s", None, None, None)).unwrap();
        assert!(none["hooks"].get("PreToolUse").is_none());
    }

    #[test]
    fn status_line_is_captured_and_relayed_to_the_users_own() {
        let home = "C:/u/.claude";
        let settings = win32_join(&[home, "settings.json"]);
        let with = |text: &str| FixtureFs(json!({ "files": { settings.clone(): text } }));
        let relay = Some("C:/h/reframe-host.exe");
        // No status line of the user's (no settings, or none set): curl posts the input.
        let direct = status_line_settings(&FixtureFs(json!({})), 4100, "tok", "s 1", home, relay).unwrap();
        let unset = status_line_settings(&with(r#"{"theme":"dark"}"#), 4100, "tok", "s 1", home, relay);
        assert_eq!(unset.as_ref(), Some(&direct));
        assert!(direct["command"].as_str().unwrap().starts_with("curl.exe "));
        assert!(direct["command"]
            .as_str()
            .unwrap()
            .contains("http://127.0.0.1:4100/hook/s%201/StatusLine"));
        // The user's own: relayed through the host, its other settings kept.
        let own = with(r#"{"statusLine":{"type":"command","command":"\"C:/x.exe\" go","padding":0}}"#);
        let relayed = status_line_settings(&own, 4100, "tok", "s 1", home, relay).unwrap();
        let hex: String = "\"C:/x.exe\" go".bytes().map(|b| format!("{b:02x}")).collect();
        assert_eq!(
            relayed["command"],
            json!(format!(
                "\"C:/h/reframe-host.exe\" --status-line-relay http://127.0.0.1:4100/hook/s%201/StatusLine tok {hex}"
            ))
        );
        assert_eq!(relayed["padding"], json!(0));
        // Without a relay the user's status line is left alone.
        assert_eq!(status_line_settings(&own, 4100, "tok", "s 1", home, None), None);
        // Settings that are not JSON count as a status line of the user's that cannot be relayed.
        assert_eq!(status_line_settings(&with("{"), 4100, "tok", "s 1", home, relay), None);
    }

    /// A fixture's made-up disk (spawn-plan.fixtures.test.ts fixtureFs).
    struct FixtureFs(Value);

    impl PlanFs for FixtureFs {
        fn is_file(&self, file: &str) -> bool {
            self.0["files"].get(file).is_some()
        }
        fn read_file(&self, file: &str) -> Option<String> {
            self.0["files"].get(file).and_then(Value::as_str).map(str::to_owned)
        }
        fn exists(&self, file: &str) -> bool {
            self.is_file(file) || self.is_dir(file)
        }
        fn is_dir(&self, dir: &str) -> bool {
            self.0["dirs"].as_array().is_some_and(|d| d.iter().any(|x| x == dir))
        }
        fn transcript_exists(&self, home: &str, id: &str) -> bool {
            self.0["transcripts"]
                .as_array()
                .is_some_and(|t| t.iter().any(|x| x[0] == home && x[1] == id))
        }
    }

    #[test]
    fn matches_the_shared_fixtures() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../test/spawn-plan-fixtures");
        let mut names: Vec<_> = fs::read_dir(root.join("inputs"))
            .unwrap()
            .map(|e| e.unwrap().file_name().into_string().unwrap())
            .filter(|n| n.ends_with(".json"))
            .collect();
        names.sort();
        assert!(names.len() > 30);
        let mut failed = Vec::new();
        for name in &names {
            let fixture: Value = serde_json::from_slice(&fs::read(root.join("inputs").join(name)).unwrap()).unwrap();
            let plan = spawn_plan(&fixture["input"], &FixtureFs(fixture["fs"].clone())).unwrap();
            let actual = serde_json::to_string_pretty(&plan).unwrap() + "\n";
            let expected = fs::read_to_string(root.join("expected").join(name))
                .unwrap()
                .replace("\r\n", "\n");
            if actual != expected {
                failed.push(format!("{name}:\n{actual}"));
            }
        }
        assert!(
            failed.is_empty(),
            "differs from the TypeScript plans:\n{}",
            failed.join("\n")
        );
    }

    #[test]
    fn a_gemini_thread_runs_agy_with_its_hooks_and_thread_tools() {
        let agy = "C:\\u\\AppData\\Local\\Microsoft\\WinGet\\Links\\agy.exe";
        let home = "C:\\u\\.gemini\\antigravity-cli";
        let input = |mode: &str, launch: Value, used: bool| {
            json!({
                "session": { "id": "s1", "claudeSessionId": "c1", "cwd": "D:\\p", "launch": launch, "used": used },
                "mode": mode, "env": { "USERPROFILE": "C:\\u", "LOCALAPPDATA": "C:\\u\\AppData\\Local", "Path": "C:\\w" },
                "claudeHome": "C:\\u\\.claude", "settingsDir": "D:\\r\\hooks", "hooks": { "port": 4100, "token": "tok" },
                "statusLineRelay": "C:\\h\\reframe-host.exe", "tasksDir": "D:\\r\\tasks",
            })
        };
        let transcript = agy_transcript(home, "c1");
        let fs = FixtureFs(json!({ "files": { agy: "", transcript.clone(): "" }, "dirs": ["D:\\p"] }));
        let launch = json!({ "provider": "gemini", "model": "gemini-4-neon-high", "permissionMode": "bypassPermissions",
            "initialPrompt": "hi", "addDirs": ["D:\\x"] });
        let plan = spawn_plan(&input("new", launch.clone(), false), &fs).unwrap();
        assert_eq!(plan["ok"], json!(true));
        assert_eq!(plan["file"], json!(agy));
        assert_eq!(
            plan["args"],
            json!([
                "--model",
                "gemini-4-neon-high",
                "--dangerously-skip-permissions",
                "--add-dir",
                "D:\\r\\hooks\\s1-agy",
                "--add-dir",
                "D:\\x",
                "-i",
                "hi"
            ])
        );
        assert_eq!(plan["env"]["REFRAME_SESSION"], json!("s1"));
        assert_eq!(plan["meta"], json!({ "agy": { "home": home, "conversationId": "c1" } }));
        assert!(plan.get("settings").is_none());
        let files = plan["files"].as_array().unwrap();
        assert_eq!(files[0]["file"], json!("D:\\r\\hooks\\s1-agy\\.agents\\hooks.json"));
        let hooks: Value = serde_json::from_str(files[0]["content"].as_str().unwrap()).unwrap();
        assert_eq!(
            hooks["reframe"]["Stop"][0]["command"],
            json!("\"C:\\h\\reframe-host.exe\" --agy-hook http://127.0.0.1:4100/hook/s1/ tok Stop")
        );
        assert!(hooks["reframe"].get("PreToolUse").is_none());
        let tools: Value = serde_json::from_str(files[1]["content"].as_str().unwrap()).unwrap();
        assert_eq!(
            tools["mcpServers"]["reframe"]["args"],
            json!([
                "--thread-tools",
                "http://127.0.0.1:4100/hook/s1/OpenThread",
                "tok",
                "D:\\r\\tasks"
            ])
        );
        // Resumed: its conversation, no first prompt again.
        let resumed = spawn_plan(&input("resume", launch.clone(), true), &fs).unwrap();
        assert_eq!(resumed["mode"], json!("resume"));
        assert_eq!(
            &resumed["args"].as_array().unwrap()[..2],
            &[json!("--conversation"), json!("c1")]
        );
        assert!(!resumed["args"].as_array().unwrap().contains(&json!("-i")));
        // Without agy, or a used conversation agy has no transcript of: the tile says why.
        let no_agy = FixtureFs(json!({ "dirs": ["D:\\p"] }));
        assert_eq!(
            spawn_plan(&input("new", launch.clone(), false), &no_agy).unwrap()["error"],
            json!("agyNotFound")
        );
        let gone = FixtureFs(json!({ "files": { agy: "" }, "dirs": ["D:\\p"] }));
        assert_eq!(
            spawn_plan(&input("resume", launch, true), &gone).unwrap()["error"],
            json!("transcriptMissing")
        );
        // agy on PATH comes first; the other permission modes.
        let on_path = FixtureFs(json!({ "files": { "C:\\w\\agy.exe": "", agy: "" }, "dirs": ["D:\\p"] }));
        let plan = spawn_plan(
            &input(
                "new",
                json!({ "provider": "gemini", "permissionMode": "acceptEdits" }),
                false,
            ),
            &on_path,
        )
        .unwrap();
        assert_eq!(plan["file"], json!("C:\\w\\agy.exe"));
        assert_eq!(
            &plan["args"].as_array().unwrap()[..2],
            &[json!("--mode"), json!("accept-edits")]
        );
    }

    #[test]
    fn refuses_bad_input() {
        assert!(spawn_plan(&json!({}), &RealFs).is_err());
        let input = json!({ "session": { "id": "a", "claudeSessionId": "b", "cwd": "c" }, "mode": "fresh", "env": {}, "claudeHome": "h", "settingsDir": "d", "hooks": { "port": 1, "token": "t" } });
        assert_eq!(
            spawn_plan(&input, &RealFs).unwrap_err(),
            "freshClaudeSessionId is required"
        );
    }

    #[test]
    fn normalises_paths_as_node() {
        assert_eq!(win32_join(&["C:/a//b/", "x.json"]), "C:\\a\\b\\x.json");
        assert_eq!(win32_join(&["C:\\a\\npm", "..\\pnpm\\x.js"]), "C:\\a\\pnpm\\x.js");
        assert_eq!(win32_join(&["\\\\server\\share\\d", "f"]), "\\\\server\\share\\d\\f");
        assert_eq!(win32_dirname("C:\\a\\b.cmd"), "C:\\a");
        assert_eq!(win32_dirname("C:\\b.cmd"), "C:\\");
        assert_eq!(encode_uri_component("s 1/é?"), "s%201%2F%C3%A9%3F");
    }
}
