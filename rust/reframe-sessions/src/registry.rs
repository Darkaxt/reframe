//! Claude Code's per-process session registry (`<home>/sessions/<pid>.json`), read as src/main/claude/registry.ts
//! reads it. The format is undocumented, so everything is parsed defensively.

use std::path::Path;

use serde_json::{json, Map, Value};

/// A parsed registry entry (`RegistryEntry`).
#[derive(Clone, Debug, PartialEq)]
pub struct Entry {
    pub pid: f64,
    pub session_id: String,
    pub cwd: Option<String>,
    pub name: Option<String>,
    /// Whether the user named the session ('user'), or Claude did ('auto', 'derived').
    pub name_source: Option<String>,
    /// idle, busy or waiting.
    pub status: Option<String>,
    /// When the process started (ms since 1970).
    pub started_at: Option<f64>,
    /// The process's start as the system tells it (on Windows its creation time).
    pub proc_start: Option<String>,
}

impl Entry {
    /// The process id, if it is one.
    pub fn pid(&self) -> Option<u32> {
        (self.pid.fract() == 0.0 && self.pid >= 0.0 && self.pid <= f64::from(u32::MAX)).then_some(self.pid as u32)
    }

    /// As JSON.stringify writes what parseRegistryEntry gives (absent fields left out).
    pub fn to_json(&self) -> Value {
        let mut v = Map::new();
        v.insert("pid".into(), number(self.pid));
        v.insert("sessionId".into(), json!(self.session_id));
        let mut text = |key: &str, value: &Option<String>| {
            if let Some(value) = value {
                v.insert(key.into(), json!(value));
            }
        };
        text("cwd", &self.cwd);
        text("name", &self.name);
        text("nameSource", &self.name_source);
        text("status", &self.status);
        if let Some(at) = self.started_at {
            v.insert("startedAt".into(), number(at));
        }
        if let Some(start) = &self.proc_start {
            v.insert("procStart".into(), json!(start));
        }
        Value::Object(v)
    }
}

/// A number as JavaScript writes it: whole ones without a fraction.
fn number(n: f64) -> Value {
    if n.fract() == 0.0 && n.abs() < 9.007_199_254_740_992e15 {
        json!(n as i64)
    } else {
        json!(n)
    }
}

/// parseRegistryEntry.
pub fn parse_entry(value: &Value) -> Option<Entry> {
    let v = value.as_object()?;
    let pid = v.get("pid")?.as_f64()?;
    let session_id = v.get("sessionId")?.as_str().filter(|s| !s.is_empty())?.to_string();
    let text = |key: &str| v.get(key).and_then(Value::as_str).map(str::to_string);
    Some(Entry {
        pid,
        session_id,
        cwd: text("cwd"),
        name: text("name").filter(|s| !s.is_empty()),
        name_source: text("nameSource"),
        status: text("status").filter(|s| matches!(s.as_str(), "idle" | "busy" | "waiting")),
        started_at: v.get("startedAt").and_then(Value::as_f64),
        proc_start: text("procStart").filter(|s| !s.is_empty()),
    })
}

/// readRegistryEntry: the entry of process `pid`, if its file names that process.
pub fn read_entry(home: &Path, pid: u32) -> Option<Entry> {
    let bytes = std::fs::read(home.join("sessions").join(format!("{pid}.json"))).ok()?;
    let value: Value = serde_json::from_str(&String::from_utf8_lossy(&bytes)).ok()?;
    parse_entry(&value).filter(|e| e.pid == f64::from(pid))
}

/// findRegistryEntry: the entry of a running claude by its session id; `alive` tells it from one left behind.
pub fn find_entry(home: &Path, session_id: &str, alive: impl Fn(&Entry) -> bool) -> Option<Entry> {
    let files = std::fs::read_dir(home.join("sessions")).ok()?;
    for file in files.flatten() {
        let name = file.file_name();
        let Some(stem) = name.to_str().and_then(|n| n.strip_suffix(".json")) else {
            continue;
        };
        if stem.is_empty() || !stem.bytes().all(|b| b.is_ascii_digit()) {
            continue;
        }
        // As Number() reads the name: `0012.json` is looked for as `12.json`.
        let Ok(pid) = stem.parse::<u32>() else {
            continue;
        };
        if let Some(entry) = read_entry(home, pid) {
            if entry.session_id == session_id && alive(&entry) {
                return Some(entry);
            }
        }
    }
    None
}

/// isRunningEntry: the claude an entry names runs (and is the one that wrote it, where its start can be told).
pub fn is_running(entry: &Entry, alive: impl Fn(u32) -> bool, start_of: impl Fn(u32) -> Option<String>) -> bool {
    let Some(pid) = entry.pid() else {
        return false;
    };
    if !alive(pid) {
        return false;
    }
    let start = entry.proc_start.as_ref().and_then(|_| start_of(pid));
    start.is_none() || start == entry.proc_start
}

/// looksLikeClaude (src/main/wsl/distros.ts): a Linux command line (NUL-separated) that runs Claude Code.
pub fn looks_like_claude(cmdline: &str) -> bool {
    cmdline.split('\0').take(2).any(|arg| {
        arg == "claude"
            || arg.ends_with("/claude")
            || arg.contains("/claude-code/")
            || arg.contains("/.local/share/claude/")
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_defensively() {
        assert_eq!(parse_entry(&json!({ "pid": 1 })), None);
        assert_eq!(parse_entry(&json!({ "pid": "1", "sessionId": "a" })), None);
        assert_eq!(parse_entry(&json!({ "pid": 1, "sessionId": "" })), None);
        let e =
            parse_entry(&json!({ "pid": 1, "sessionId": "a", "name": "", "status": "odd", "procStart": "" })).unwrap();
        assert_eq!((e.name, e.status, e.proc_start), (None, None, None));
        let e = parse_entry(&json!({ "pid": 12, "sessionId": "a", "cwd": "C:\\x", "status": "busy", "startedAt": 5 }));
        assert_eq!(
            e.unwrap().to_json(),
            json!({ "pid": 12, "sessionId": "a", "cwd": "C:\\x", "status": "busy", "startedAt": 5 })
        );
    }

    #[test]
    fn tells_claude_command_lines() {
        assert!(looks_like_claude("claude\0--resume"));
        assert!(looks_like_claude(
            "node\0/home/me/.npm/lib/node_modules/@anthropic-ai/claude-code/cli.js"
        ));
        assert!(looks_like_claude("/home/me/.local/share/claude/versions/2.1.0\0"));
        assert!(!looks_like_claude("bash\0-c\0claude"));
        assert!(!looks_like_claude(""));
    }
}
