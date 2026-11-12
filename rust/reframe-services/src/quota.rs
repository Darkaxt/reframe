//! The quota counters' sources (src/main/quota.ts): LLM Coordinator's installed native client, asked once for its
//! usage snapshot (read only), and whether the user's Claude Code settings set a status line.

use std::fs;
use std::io::{ErrorKind, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

use serde_json::Value;

use crate::launch::truthy;

/// How long the Coordinator is given to answer (readCodexQuota's default).
pub const TIMEOUT: Duration = Duration::from_secs(20);
/// The most of its output that is taken (execFile's maxBuffer); more fails the read.
const MAX_OUTPUT: u64 = 64 * 1024 * 1024;

/// The installed Coordinator's executable, from its active.json (`releases/<release>/LLMCoordinator.exe`).
pub fn coordinator_executable(local_app_data: &str) -> Option<PathBuf> {
    if local_app_data.is_empty() {
        return None;
    }
    let root = Path::new(local_app_data).join("Tools").join("llm-coordinator");
    let active: Value = serde_json::from_slice(&fs::read(root.join("active.json")).ok()?).ok()?;
    let release = active.get("release")?.as_str()?;
    let valid = !release.is_empty()
        && release
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"_.-".contains(&b));
    if !valid {
        return None;
    }
    let exe = root.join("releases").join(release).join("LLMCoordinator.exe");
    exe.exists().then_some(exe)
}

/// The Coordinator's answer to `usage.snapshot` (its JSON as it wrote it), or None when it cannot be started, fails,
/// writes more than 64 MiB or no JSON, or does not exit in `timeout` (then it is killed, as execFile kills it).
pub fn read_codex_usage(exe: &Path, timeout: Duration) -> Option<Value> {
    let mut command = Command::new(exe);
    command
        .arg("--native-client-stdin-active")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    let mut child = command.spawn().ok()?;
    if let Some(mut stdin) = child.stdin.take() {
        let _ = stdin.write_all(br#"{"method":"usage.snapshot","hours":24}"#);
    }
    let deadline = Instant::now() + timeout;
    let mut stdout = child.stdout.take()?;
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let mut output = Vec::new();
        let read = (&mut stdout).take(MAX_OUTPUT + 1).read_to_end(&mut output);
        let _ = tx.send(read.ok().filter(|_| output.len() as u64 <= MAX_OUTPUT).map(|_| output));
    });
    // The deadline covers the whole run: a Coordinator that closes its output and goes on is killed too.
    let finished = match rx.recv_timeout(deadline.saturating_duration_since(Instant::now())) {
        Ok(Some(output)) => wait_until(&mut child, deadline).map(|status| (output, status)),
        _ => None,
    };
    let Some((output, status)) = finished else {
        // Timed out or too much: the process goes (its children are not looked for, as with execFile).
        let _ = child.kill();
        let _ = child.wait();
        return None;
    };
    if !status.success() {
        return None;
    }
    serde_json::from_str(&String::from_utf8_lossy(&output)).ok()
}

/// The process's exit, if it comes before `deadline` (std has no wait with a timeout: it is asked every 20 ms).
fn wait_until(child: &mut Child, deadline: Instant) -> Option<ExitStatus> {
    loop {
        if let Some(status) = child.try_wait().ok()? {
            return Some(status);
        }
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() {
            return None;
        }
        std::thread::sleep(left.min(Duration::from_millis(20)));
    }
}

/// Whether the user's Claude Code settings set a status line; true also when they cannot be read (but are there).
pub fn user_has_status_line(claude_home: &str) -> bool {
    match fs::read(Path::new(claude_home).join("settings.json")) {
        Err(error) => error.kind() != ErrorKind::NotFound,
        Ok(bytes) => match serde_json::from_str::<Value>(&String::from_utf8_lossy(&bytes)) {
            // A file that is not JSON, or JSON `null` (whose statusLine cannot be read), counts as one.
            Err(_) | Ok(Value::Null) => true,
            Ok(settings) => settings.get("statusLine").is_some_and(truthy),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;

    #[test]
    fn finds_the_active_release() {
        let dir = TestDir::new();
        let local = dir.text("");
        assert_eq!(coordinator_executable(&local), None);
        dir.write("Tools/llm-coordinator/active.json", r#"{"release":"1.2.3"}"#);
        assert_eq!(coordinator_executable(&local), None);
        let exe = dir.write("Tools/llm-coordinator/releases/1.2.3/LLMCoordinator.exe", "");
        assert_eq!(coordinator_executable(&local), Some(exe));
        dir.write("Tools/llm-coordinator/active.json", r#"{"release":"../1.2.3"}"#);
        assert_eq!(coordinator_executable(&local), None);
        assert_eq!(coordinator_executable(""), None);
    }

    #[test]
    fn reads_status_line_settings() {
        let dir = TestDir::new();
        let home = dir.text("");
        assert!(!user_has_status_line(&home));
        for (settings, has) in [
            (r#"{"statusLine":{"type":"command"}}"#, true),
            (r#"{"statusLine":""}"#, false),
            (r#"{"statusLine":null}"#, false),
            (r#"{"theme":"dark"}"#, false),
            ("[]", false),
            ("null", true),
            ("{not json", true),
        ] {
            dir.write("settings.json", settings);
            assert_eq!(user_has_status_line(&home), has, "{settings}");
        }
    }

    #[test]
    fn a_missing_coordinator_gives_nothing() {
        let dir = TestDir::new();
        assert_eq!(read_codex_usage(&dir.path().join("none.exe"), TIMEOUT), None);
    }
}
