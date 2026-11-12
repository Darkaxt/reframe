//! A stand-in for LLM Coordinator's native client in the PTY host conformance tests, copied in as a fake
//! `<LOCALAPPDATA>/Tools/llm-coordinator/releases/<release>/LLMCoordinator.exe`. It does what `fake.json` next to it
//! says (`{ "stdout": "...", "exitCode": 0, "sleepMs": 0, "closeStdout": false }`; with closeStdout it
//! writes, closes its output, then sleeps) and writes what it was asked to `asked.json`
//! (`{ "pid", "args", "stdin" }`) before doing it.
#![forbid(unsafe_code)]

use std::io::{Read, Write};

fn main() {
    let exe = std::env::current_exe().expect("own path");
    let dir = exe.parent().expect("own folder");
    let behaviour: serde_json::Value =
        serde_json::from_slice(&std::fs::read(dir.join("fake.json")).unwrap_or_default()).unwrap_or_default();
    let mut stdin = String::new();
    let _ = std::io::stdin().read_to_string(&mut stdin);
    let args: Vec<String> = std::env::args().skip(1).collect();
    let asked = serde_json::json!({ "pid": std::process::id(), "args": args, "stdin": stdin });
    let _ = std::fs::write(dir.join("asked.json"), asked.to_string());
    let sleep = || {
        if let Some(ms) = behaviour.get("sleepMs").and_then(serde_json::Value::as_u64) {
            std::thread::sleep(std::time::Duration::from_millis(ms));
        }
    };
    let close_first = behaviour.get("closeStdout").and_then(serde_json::Value::as_bool) == Some(true);
    if !close_first {
        sleep();
    }
    if let Some(out) = behaviour.get("stdout").and_then(serde_json::Value::as_str) {
        let mut stdout = std::io::stdout();
        let _ = stdout.write_all(out.as_bytes());
        let _ = stdout.flush();
    }
    if close_first {
        // The output ends while the process goes on.
        reframe_conpty::close_stdout();
        sleep();
    }
    let code = behaviour
        .get("exitCode")
        .and_then(serde_json::Value::as_i64)
        .unwrap_or(0);
    std::process::exit(code as i32);
}
