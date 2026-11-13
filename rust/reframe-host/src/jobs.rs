//! The `cleanup`, `wsl` and `history` services (docs/ptyhost-protocol.md, "Services"): reframe's cleanup at start,
//! its WSL distributions and its chat history jobs, as src/main/cleanup.ts, src/main/wsl/distros.ts and
//! src/main/history-helper.ts run them (the Node host runs those very modules). What they do lives in
//! reframe-services (cleanup.rs, wsl.rs, history.rs); ending orphaned claudes, which needs the system's processes,
//! lives here.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use reframe_services::history::{self, HelperEnv};
use reframe_services::wsl::{self, Distros, WslCommand};
use reframe_sessions::registry;
use serde_json::{json, Value};

use crate::transcripts::{SvcError, Work};

pub fn serves(service: &str) -> bool {
    matches!(service, "cleanup" | "wsl" | "history")
}

fn bad(message: impl Into<String>) -> SvcError {
    SvcError {
        code: "bad-params",
        message: message.into(),
    }
}

fn io(message: String) -> SvcError {
    SvcError { code: "io", message }
}

fn text(params: &Value, key: &str) -> Result<String, SvcError> {
    match params.get(key) {
        Some(Value::String(s)) if !s.is_empty() => Ok(s.clone()),
        Some(Value::String(_)) | None | Some(Value::Null) => Err(bad(format!("{key} is required"))),
        _ => Err(bad(format!("{key} must be a string"))),
    }
}

fn optional_text(params: &Value, key: &str) -> Result<Option<String>, SvcError> {
    match params.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) => Ok(Some(s.clone()).filter(|s| !s.is_empty())),
        _ => Err(bad(format!("{key} must be a string"))),
    }
}

fn absolute(params: &Value, key: &str) -> Result<String, SvcError> {
    let value = text(params, key)?;
    if !reframe_services::is_absolute(&value) {
        return Err(bad(format!("{key} must be an absolute path")));
    }
    Ok(value)
}

fn pid_of(value: Option<&Value>, key: &str) -> Result<u32, SvcError> {
    value
        .and_then(|v| v.as_f64())
        .filter(|n| n.fract() == 0.0 && *n > 0.0 && *n <= f64::from(u32::MAX))
        .map(|n| n as u32)
        .ok_or_else(|| bad(format!("{key} must be a process id")))
}

fn list<'a>(params: &'a Value, key: &str) -> Result<&'a Vec<Value>, SvcError> {
    params
        .get(key)
        .and_then(Value::as_array)
        .ok_or_else(|| bad(format!("{key} must be a list")))
}

fn strings(params: &Value, key: &str) -> Result<Vec<String>, SvcError> {
    list(params, key)?
        .iter()
        .map(|v| v.as_str().map(str::to_owned))
        .collect::<Option<Vec<_>>>()
        .ok_or_else(|| bad(format!("{key} must be a list of strings")))
}

/// A distribution's name: a plain name (never a path that would reach past the share).
fn distro(params: &Value) -> Result<String, SvcError> {
    let name = text(params, "distro")?;
    if name.contains(['\\', '/']) || name == "." || name == ".." {
        return Err(bad("distro must be a distribution's name"));
    }
    Ok(name)
}

fn now_ms() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs_f64() * 1000.0)
        .unwrap_or(0.0)
}

/// A random (version 4) UUID, as crypto.randomUUID gives one.
fn random_uuid() -> String {
    let mut b = [0u8; 16];
    getrandom::fill(&mut b).expect("the system's random source is available");
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    let hex: String = b.iter().map(|x| format!("{x:02x}")).collect();
    format!(
        "{}-{}-{}-{}-{}",
        &hex[0..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..32]
    )
}

/// The wsl.exe and share a request names, else the host's (REFRAME_WSL_EXE, REFRAME_WSL_SHARE).
fn wsl_where(params: &Value) -> Result<Where, SvcError> {
    let env = |key: &str| std::env::var(key).ok();
    let share = optional_text(params, "share")?.unwrap_or_else(|| wsl::wsl_share(env));
    let command = match params.get("command") {
        None | Some(Value::Null) => wsl::wsl_command(env),
        Some(c) => {
            let shape = || bad("command must be { file, prefixArgs, env }");
            let file = c
                .get("file")
                .and_then(Value::as_str)
                .filter(|f| !f.is_empty())
                .ok_or_else(shape)?;
            let prefix_args = c
                .get("prefixArgs")
                .and_then(Value::as_array)
                .and_then(|a| {
                    a.iter()
                        .map(|v| v.as_str().map(str::to_owned))
                        .collect::<Option<Vec<_>>>()
                })
                .ok_or_else(shape)?;
            let env = match c.get("env") {
                None => Vec::new(),
                Some(Value::Object(map)) => map
                    .iter()
                    .filter_map(|(k, v)| v.as_str().map(|v| (k.clone(), v.to_owned())))
                    .collect(),
                Some(_) => return Err(shape()),
            };
            Some(WslCommand {
                file: file.to_owned(),
                prefix_args,
                env,
            })
        }
    };
    Ok((command, share))
}

/// history_backend.py and the folder it runs in, as reframe gives them; the Python is the host's own pinned one.
fn helper_paths(params: &Value) -> Result<(PathBuf, PathBuf), SvcError> {
    let backend = absolute(params, "backend")?;
    let name = backend.rsplit(['\\', '/']).next().unwrap_or("");
    if !name.eq_ignore_ascii_case("history_backend.py") {
        return Err(bad("backend must be history_backend.py"));
    }
    Ok((PathBuf::from(backend), PathBuf::from(absolute(params, "cwd")?)))
}

fn archive_root(params: &Value) -> Result<String, SvcError> {
    let root = text(params, "root")?;
    if !history::is_archive_root(&root, history::cwd_drive()) {
        return Err(bad("root must be an absolute folder on a non-C drive"));
    }
    Ok(root)
}

/// A wsl.exe (none: not available) and the share.
type Where = (Option<WslCommand>, String);

/// The WSL distributions, one `Distros` per wsl.exe and share (each keeps what its distributions answered).
#[derive(Default)]
pub struct Jobs {
    distros: Mutex<HashMap<Where, Arc<Distros>>>,
}

impl Jobs {
    fn wsl(&self, params: &Value) -> Result<Arc<Distros>, SvcError> {
        let key = wsl_where(params)?;
        let mut all = self.distros.lock().unwrap();
        Ok(all
            .entry(key.clone())
            .or_insert_with(|| Arc::new(Distros::new(key.0, key.1)))
            .clone())
    }

    /// The work of a `cleanup`, `wsl` or `history` request (its parameters checked now).
    pub fn work(&self, service: &str, method: &str, params: &Value) -> Result<Work, SvcError> {
        Ok(match (service, method) {
            ("cleanup", "files") => {
                let drops = absolute(params, "drops")?;
                let hooks = absolute(params, "hooks")?;
                let ids: HashSet<String> = strings(params, "sessionIds")?.into_iter().collect();
                let now = match params.get("now") {
                    None | Some(Value::Null) => None,
                    Some(v) => Some(v.as_f64().ok_or_else(|| bad("now must be a number"))?),
                };
                Box::new(move || {
                    let (drops, hooks) = reframe_services::cleanup::clean_up(
                        Path::new(&drops),
                        Path::new(&hooks),
                        &ids,
                        now.unwrap_or_else(now_ms),
                    );
                    Ok(json!({ "drops": drops, "hooks": hooks }))
                })
            }
            ("cleanup", "orphans") => {
                let home = PathBuf::from(absolute(params, "claudeHome")?);
                let sessions = list(params, "sessions")?
                    .iter()
                    .map(|s| Ok((pid_of(s.get("pid"), "pid")?, text(s, "claudeSessionId")?)))
                    .collect::<Result<Vec<_>, SvcError>>()?;
                let hosted = list(params, "hosted")?
                    .iter()
                    .map(|p| pid_of(Some(p), "pid"))
                    .collect::<Result<HashSet<_>, SvcError>>()?;
                Box::new(move || Ok(json!({ "killed": end_orphans(&home, &sessions, &hosted) })))
            }
            ("wsl", "list") => {
                let wsl = self.wsl(params)?;
                Box::new(move || Ok(json!(wsl.list())))
            }
            ("wsl", "running") => {
                let wsl = self.wsl(params)?;
                Box::new(move || Ok(json!(wsl.running())))
            }
            ("wsl", "info") => {
                let name = distro(params)?;
                let wsl = self.wsl(params)?;
                Box::new(move || Ok(wsl.info(&name)))
            }
            ("wsl", "isClaude") => {
                let name = distro(params)?;
                let pid = pid_of(params.get("pid"), "pid")?;
                let wsl = self.wsl(params)?;
                Box::new(move || Ok(json!(wsl.is_claude(&name, pid))))
            }
            ("wsl", "stopClaude") => {
                let name = distro(params)?;
                let pid = pid_of(params.get("pid"), "pid")?;
                let wsl = self.wsl(params)?;
                Box::new(move || Ok(json!(wsl.stop_claude(&name, pid))))
            }
            ("wsl", "parsePath") => {
                let path = text(params, "path")?;
                let share = optional_text(params, "share")?;
                let parsed = wsl::parse_wsl_path(&path, share.as_deref())
                    .map_or(Value::Null, |p| json!({ "distro": p.distro, "path": p.path }));
                Box::new(move || Ok(parsed))
            }
            ("wsl", "windowsPath") => {
                let path = wsl::wsl_windows_path(
                    &text(params, "distro")?,
                    &text(params, "path")?,
                    &text(params, "share")?,
                );
                Box::new(move || Ok(json!(path)))
            }
            ("wsl", "linuxPath") => {
                let path = text(params, "path")?;
                let name = text(params, "distro")?;
                let mount_root = text(params, "mountRoot")?;
                let share = optional_text(params, "share")?;
                let linux = wsl::linux_path_of(&path, &name, &mount_root, share.as_deref());
                Box::new(move || Ok(json!(linux)))
            }
            ("history", "prepare") => {
                let (backend, cwd) = helper_paths(params)?;
                let home = text(params, "home")?;
                let input = history::prepare_input(&archive_root(params)?, &strings(params, "ids")?, random_uuid);
                Box::new(move || {
                    let result = history::run_helper(
                        &HelperEnv::from_env(),
                        &backend,
                        &cwd,
                        "prepare",
                        &home,
                        &[],
                        Some(&input),
                    )
                    .map_err(io)?;
                    Ok(result.get("results").cloned().unwrap_or(Value::Null))
                })
            }
            ("history", "retire") => {
                let (backend, cwd) = helper_paths(params)?;
                let home = text(params, "home")?;
                let input = history::retire_input(list(params, "jobs")?).map_err(bad)?;
                Box::new(move || {
                    let result = history::run_helper(
                        &HelperEnv::from_env(),
                        &backend,
                        &cwd,
                        "retire",
                        &home,
                        &[],
                        Some(&input),
                    )
                    .map_err(io)?;
                    Ok(result.get("results").cloned().unwrap_or(Value::Null))
                })
            }
            ("history", "recover") => {
                let (backend, cwd) = helper_paths(params)?;
                let home = text(params, "home")?;
                let extra = vec!["--operation".to_owned(), archive_root(params)?];
                Box::new(move || {
                    history::run_helper(&HelperEnv::from_env(), &backend, &cwd, "recover", &home, &extra, None)
                        .map_err(io)
                })
            }
            ("history", "restore") => {
                let (backend, cwd) = helper_paths(params)?;
                let home = text(params, "home")?;
                let mut extra = Vec::new();
                for key in ["id", "operation", "archive", "sha256"] {
                    extra.push(format!("--{key}"));
                    extra.push(text(params, key)?);
                }
                Box::new(move || {
                    history::run_helper(&HelperEnv::from_env(), &backend, &cwd, "restore", &home, &extra, None)
                        .map_err(io)?;
                    Ok(Value::Null)
                })
            }
            _ => {
                return Err(SvcError {
                    code: "unknown-method",
                    message: format!("{service}.{method}"),
                })
            }
        })
    }
}

/// endOrphans (src/main/cleanup.ts): kills the claudes of sessions that survived a crash of a previous run — a
/// process that runs, is not `hosted`, and whose registry entry is that session's and that very process's — and
/// removes the entry after it. The pids killed, in order.
pub fn end_orphans(home: &Path, sessions: &[(u32, String)], hosted: &HashSet<u32>) -> Vec<u32> {
    let mut killed = Vec::new();
    for (pid, claude_session_id) in sessions {
        let pid = *pid;
        if !reframe_conpty::process_alive(pid) || hosted.contains(&pid) {
            continue;
        }
        let Some(entry) = registry::read_entry(home, pid) else {
            continue;
        };
        if entry.session_id != *claude_session_id
            || !registry::is_running(&entry, reframe_conpty::process_alive, reframe_conpty::process_start)
        {
            continue;
        }
        kill_tree(pid);
        // forgetRegistryEntry: once the process is gone.
        if !reframe_conpty::process_alive(pid) && registry::read_entry(home, pid).is_some() {
            let _ = std::fs::remove_file(home.join("sessions").join(format!("{pid}.json")));
        }
        killed.push(pid);
    }
    killed.sort_unstable();
    killed.dedup();
    killed
}

/// killTree: `taskkill /PID <pid> /T /F`, hidden, given 10 s.
fn kill_tree(pid: u32) {
    let mut command = Command::new("taskkill");
    command
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    let Ok(mut child) = command.spawn() else {
        return;
    };
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        if !matches!(child.try_wait(), Ok(None)) {
            return;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    let _ = child.kill();
    let _ = child.wait();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn random_uuids_are_version_4() {
        let id = random_uuid();
        assert_eq!(id.len(), 36);
        assert_eq!(&id[14..15], "4");
        assert!(matches!(&id[19..20], "8" | "9" | "a" | "b"));
        assert_ne!(id, random_uuid());
    }

    #[test]
    fn checks_its_parameters() {
        let jobs = Jobs::default();
        let code =
            |service: &str, method: &str, params: Value| jobs.work(service, method, &params).err().map(|e| e.code);
        assert_eq!(
            code(
                "cleanup",
                "files",
                json!({ "drops": "rel", "hooks": "D:\\h", "sessionIds": [] })
            ),
            Some("bad-params")
        );
        assert_eq!(
            code(
                "cleanup",
                "files",
                json!({ "drops": "D:\\d", "hooks": "D:\\h", "sessionIds": [1] })
            ),
            Some("bad-params")
        );
        assert_eq!(
            code(
                "cleanup",
                "orphans",
                json!({ "claudeHome": "D:\\c", "sessions": [{ "pid": -1, "claudeSessionId": "x" }], "hosted": [] })
            ),
            Some("bad-params")
        );
        assert_eq!(code("wsl", "info", json!({ "distro": "..\\x" })), Some("bad-params"));
        assert_eq!(
            code("wsl", "info", json!({ "distro": "Ubuntu", "command": { "file": "" } })),
            Some("bad-params")
        );
        let helper =
            json!({ "backend": "D:\\r\\other.py", "cwd": "D:\\u", "home": "D:\\h", "root": "D:\\a", "ids": [] });
        assert_eq!(code("history", "prepare", helper), Some("bad-params"));
        let on_c = json!({ "backend": "D:\\r\\history_backend.py", "cwd": "D:\\u", "home": "D:\\h", "root": "C:\\a", "ids": [] });
        assert_eq!(code("history", "prepare", on_c), Some("bad-params"));
        let shred = json!({ "backend": "D:\\r\\history_backend.py", "cwd": "D:\\u", "home": "D:\\h",
            "jobs": [{ "prepared": {}, "mode": "shred", "metadata": {} }] });
        assert_eq!(code("history", "retire", shred), Some("bad-params"));
        assert_eq!(code("history", "nope", json!({})), Some("unknown-method"));
        assert_eq!(code("wsl", "parsePath", json!({ "path": "\\\\wsl$\\U\\x" })), None);
    }
}
