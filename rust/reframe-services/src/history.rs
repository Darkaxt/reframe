//! Chat history jobs (the `history` service): archive, delete, restore and recovery, run by the pinned Python
//! helper maintenance/history_backend.py exactly as src/main/history-helper.ts runs it. The helper is wrapped, not
//! ported: it verifies its thread-cleanup helper (REFRAME_HISTORY_HELPER, REFRAME_HISTORY_HELPER_SHA256) itself.
//! The Python is the host's configured one (REFRAME_HISTORY_PYTHON, absolute, with the helper's sha256 given),
//! never one a request names.

use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, Stdio};

use serde_json::{json, Value};

pub const HELPER_MISSING: &str = "Archive/delete need the configured Python and verified thread-cleanup helper. See the ClaudeGPT installation guide.";

/// The helper's environment as the host has it.
pub struct HelperEnv {
    pub python: Option<String>,
    pub helper_sha256: Option<String>,
    pub runtime: Option<String>,
    pub temp: Option<String>,
    pub tmp: Option<String>,
}

impl HelperEnv {
    pub fn from_env() -> Self {
        let var = |key: &str| std::env::var(key).ok();
        Self {
            python: var("REFRAME_HISTORY_PYTHON"),
            helper_sha256: var("REFRAME_HISTORY_HELPER_SHA256"),
            runtime: var("REFRAME_RUNTIME"),
            temp: var("TEMP"),
            tmp: var("TMP"),
        }
    }

    /// historyPython: the configured Python, absolute, with the helper's sha256 given.
    pub fn python(&self) -> Result<&str, String> {
        match (&self.python, &self.helper_sha256) {
            (Some(python), Some(sha)) if !python.is_empty() && crate::is_absolute(python) && !sha.is_empty() => {
                Ok(python)
            }
            _ => Err(HELPER_MISSING.to_owned()),
        }
    }
}

/// isArchiveRoot (src/main/archive-root.ts, on Windows): an absolute folder not on the system drive C:. A path rooted
/// without a drive (`\x`) is on the drive of the current folder (`cwd_drive`), as path.win32.resolve takes it; device
/// paths (`\\?\C:\…`) spell the same drive.
pub fn is_archive_root(root: &str, cwd_drive: Option<char>) -> bool {
    if !crate::is_absolute(root) {
        return false;
    }
    let normalized = root.replace('/', "\\");
    let drive = if let Some(device) = normalized
        .strip_prefix("\\\\?\\")
        .or_else(|| normalized.strip_prefix("\\\\.\\"))
    {
        device.chars().next().filter(|_| device.chars().nth(1) == Some(':'))
    } else if normalized.starts_with("\\\\") {
        None
    } else if normalized.starts_with('\\') {
        cwd_drive
    } else {
        normalized.chars().next()
    };
    !drive.is_some_and(|d| d.eq_ignore_ascii_case(&'c'))
}

/// The drive letter of the current folder, if it has one.
pub fn cwd_drive() -> Option<char> {
    let cwd = std::env::current_dir().ok()?;
    let text = cwd.to_string_lossy().into_owned();
    let text = text.strip_prefix("\\\\?\\").unwrap_or(&text);
    let mut chars = text.chars();
    match (chars.next(), chars.next()) {
        (Some(letter), Some(':')) if letter.is_ascii_alphabetic() => Some(letter),
        _ => None,
    }
}

/// `path.win32.join(root, "operations", id)` for an absolute root: separators as backslashes, doubled ones single
/// (a UNC root keeps its leading two).
pub fn operation_folder(root: &str, id: &str) -> String {
    let normalized = root.replace('/', "\\");
    let (lead, rest) = match normalized.strip_prefix("\\\\") {
        Some(rest) => ("\\\\", rest.to_owned()),
        None => ("", normalized.clone()),
    };
    let mut parts: Vec<&str> = rest.split('\\').filter(|p| !p.is_empty()).collect();
    if parts.is_empty() && lead.is_empty() {
        parts.push("");
    }
    let base = format!("{lead}{}", parts.join("\\"));
    format!("{}\\operations\\{id}", base.trim_end_matches('\\'))
}

/// The helper's `prepare` input: each chat planned in an operation folder of its own under `<root>/operations`.
pub fn prepare_input(root: &str, ids: &[String], mut new_id: impl FnMut() -> String) -> Value {
    let jobs: Vec<Value> = ids
        .iter()
        .map(|id| json!({ "id": id, "operation": operation_folder(root, &new_id()) }))
        .collect();
    json!({ "jobs": jobs })
}

/// The helper's `retire` input from jobs `{ prepared, mode, metadata }`; Err for one that is not that.
pub fn retire_input(jobs: &[Value]) -> Result<Value, String> {
    let jobs = jobs
        .iter()
        .map(|job| {
            let mode = job.get("mode").and_then(Value::as_str);
            let prepared = job.get("prepared").filter(|p| p.is_object());
            let metadata = job.get("metadata").filter(|m| m.is_object());
            match (mode, prepared, metadata) {
                (Some(mode @ ("archive" | "delete")), Some(prepared), Some(metadata)) => {
                    let mut out = serde_json::Map::new();
                    for key in ["id", "operation", "plan", "sha256"] {
                        if let Some(value) = prepared.get(key) {
                            out.insert(key.into(), value.clone());
                        }
                    }
                    out.insert("mode".into(), json!(mode));
                    out.insert("metadata".into(), metadata.clone());
                    Ok(Value::Object(out))
                }
                _ => Err("jobs must be { prepared, mode: archive | delete, metadata }".to_owned()),
            }
        })
        .collect::<Result<Vec<_>, String>>()?;
    Ok(json!({ "jobs": jobs }))
}

/// Runs the helper's `command` once (`python -I -B <backend> <command> --home <home> …`, hidden, in `cwd`, with
/// TEMP/TMP at REFRAME_RUNTIME) and gives its `result`; Err with its error otherwise.
pub fn run_helper(
    env: &HelperEnv,
    backend: &Path,
    cwd: &Path,
    command: &str,
    home: &str,
    extra: &[String],
    input: Option<&Value>,
) -> Result<Value, String> {
    let python = env.python()?;
    let mut cmd = Command::new(python);
    cmd.args(["-I", "-B"])
        .arg(backend)
        .args([command, "--home", home])
        .args(extra)
        .current_dir(cwd)
        .stdin(if input.is_some() { Stdio::piped() } else { Stdio::null() })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (key, value) in [("TEMP", &env.temp), ("TMP", &env.tmp)] {
        if let Some(value) = env.runtime.as_ref().filter(|r| !r.is_empty()).or(value.as_ref()) {
            cmd.env(key, value);
        }
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    let mut child = cmd.spawn().map_err(|e| format!("spawn {python}: {e}"))?;
    if let (Some(input), Some(mut stdin)) = (input, child.stdin.take()) {
        let _ = stdin.write_all(input.to_string().as_bytes());
    }
    let mut stderr_pipe = child.stderr.take();
    let errors = std::thread::spawn(move || {
        let mut kept = Vec::new();
        if let Some(pipe) = stderr_pipe.as_mut() {
            let mut buffer = [0u8; 4096];
            while let Ok(n) = pipe.read(&mut buffer) {
                if n == 0 {
                    break;
                }
                if kept.len() < 8192 {
                    kept.extend_from_slice(&buffer[..n]);
                }
            }
        }
        String::from_utf8_lossy(&kept).into_owned()
    });
    let mut stdout = Vec::new();
    if let Some(mut pipe) = child.stdout.take() {
        let _ = pipe.read_to_end(&mut stdout);
    }
    let status = child.wait().map_err(|e| e.to_string())?;
    let stderr = errors.join().unwrap_or_default();
    let packet = serde_json::from_slice::<Value>(&stdout)
        .map_err(|e| e.to_string())
        .and_then(|p| match p {
            Value::Object(_) => Ok(p),
            _ => Err("the helper's answer is not an object".to_owned()),
        });
    match packet {
        Err(error) => Err(format!(
            "Chat operation failed; originals/recovery receipts are kept. {}",
            if stderr.is_empty() { error } else { stderr }
        )),
        Ok(packet) => {
            let ok = packet.get("ok").is_some_and(truthy);
            if !status.success() || !ok {
                Err(packet
                    .get("error")
                    .filter(|e| truthy(e))
                    .map(|e| e.as_str().map_or_else(|| e.to_string(), str::to_owned))
                    .unwrap_or_else(|| format!("Chat operation incomplete ({command}).")))
            } else {
                Ok(packet.get("result").cloned().unwrap_or(Value::Null))
            }
        }
    }
}

fn truthy(value: &Value) -> bool {
    crate::launch::truthy(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn archive_roots_as_reframe_takes_them() {
        for root in ["D:\\archives", "d:/a", "\\\\server\\share\\a", "\\\\?\\D:\\a", "E:\\"] {
            assert!(is_archive_root(root, Some('C')), "{root}");
        }
        for root in [
            "C:\\a",
            "c:/a",
            "\\\\?\\C:\\a",
            "\\\\.\\c:\\a",
            "relative",
            "D:",
            "",
            "\\a",
        ] {
            assert!(!is_archive_root(root, Some('C')), "{root}");
        }
        assert!(is_archive_root("\\a", Some('D')));
    }

    #[test]
    fn operation_folders_under_the_root() {
        assert_eq!(operation_folder("D:\\a", "x"), "D:\\a\\operations\\x");
        assert_eq!(operation_folder("D:/a//b/", "x"), "D:\\a\\b\\operations\\x");
        assert_eq!(operation_folder("\\\\srv\\s", "x"), "\\\\srv\\s\\operations\\x");
        let mut n = 0;
        let input = prepare_input("D:\\a", &["one".into(), "two".into()], || {
            n += 1;
            format!("u{n}")
        });
        assert_eq!(
            input,
            json!({ "jobs": [
                { "id": "one", "operation": "D:\\a\\operations\\u1" },
                { "id": "two", "operation": "D:\\a\\operations\\u2" },
            ] })
        );
    }

    #[test]
    fn retire_jobs_as_the_helper_takes_them() {
        let job = json!({
            "prepared": { "id": "c", "operation": "D:\\o", "plan": "D:\\o\\plan.json", "sha256": "ab", "files": 2, "bytes": 9 },
            "mode": "archive",
            "metadata": { "id": "c", "label": "L", "cwd": "D:\\p" },
        });
        assert_eq!(
            retire_input(&[job]).unwrap(),
            json!({ "jobs": [{ "id": "c", "operation": "D:\\o", "plan": "D:\\o\\plan.json", "sha256": "ab",
                "mode": "archive", "metadata": { "id": "c", "label": "L", "cwd": "D:\\p" } }] })
        );
        assert!(retire_input(&[json!({ "prepared": {}, "mode": "shred", "metadata": {} })]).is_err());
        assert!(retire_input(&[json!(5)]).is_err());
    }

    #[test]
    fn the_helper_runs_only_with_its_pinned_python() {
        let env = |python: Option<&str>, sha: Option<&str>| HelperEnv {
            python: python.map(Into::into),
            helper_sha256: sha.map(Into::into),
            runtime: None,
            temp: None,
            tmp: None,
        };
        for e in [
            env(None, Some("ab")),
            env(Some("python.exe"), Some("ab")),
            env(Some("D:\\py\\python.exe"), None),
            env(Some("D:\\py\\python.exe"), Some("")),
        ] {
            let outcome = run_helper(&e, Path::new("D:\\b.py"), Path::new("."), "recover", "h", &[], None);
            assert_eq!(outcome, Err(HELPER_MISSING.to_owned()));
        }
        assert_eq!(
            env(Some("D:\\py\\python.exe"), Some("ab")).python(),
            Ok("D:\\py\\python.exe")
        );
    }
}
