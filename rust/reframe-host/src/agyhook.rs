//! The agy hook relay: `reframe-host --agy-hook <hook url base> <token> <event>`, the command of the hooks a Gemini
//! thread's Antigravity CLI runs (reframe-services' spawn_plan writes them into the session's `.agents/hooks.json`).
//! agy gives it the hook's input (JSON on standard input: conversationId, transcriptPath, cwd, ...). The events the
//! session's status follows are posted to the host's hook server as Claude Code's hooks post them (`session_id`,
//! `transcript_path`, `cwd`): SessionStart names the conversation, PreInvocation (a model call) is a turn going on,
//! Stop its end. agy is answered with an empty object (nothing to change) and exit 0, whatever happened: the relay
//! never holds agy up (the post takes two seconds at most).

use std::io::Read;

use serde_json::{json, Value};

const MAX_INPUT: u64 = 4 * 1024 * 1024;

/// The host's event for an agy hook event, with its body; None: nothing to tell.
pub fn translate(event: &str, input: &Value) -> Option<(&'static str, Value)> {
    let host_event = match event {
        "SessionStart" => "SessionStart",
        "PreInvocation" => "UserPromptSubmit",
        // A Stop while agy goes on by itself (not fully idle) does not end the turn.
        "Stop" if input.get("fullyIdle").and_then(Value::as_bool) != Some(false) => "Stop",
        _ => return None,
    };
    let text = |key: &str| input.get(key).and_then(Value::as_str).unwrap_or("");
    Some((
        host_event,
        json!({
            "session_id": text("conversationId"),
            "transcript_path": text("transcriptPath"),
            "cwd": text("cwd"),
            "model": text("modelName"),
            "hook_event_name": host_event,
        }),
    ))
}

pub fn run(args: &[String]) -> i32 {
    let mut input = Vec::new();
    let _ = std::io::stdin().take(MAX_INPUT).read_to_end(&mut input);
    if let [base, token, event] = args {
        let parsed: Value = serde_json::from_slice(&input).unwrap_or(Value::Null);
        if let Some((host_event, body)) = translate(event, &parsed) {
            let _ = crate::statusline::post(&format!("{base}{host_event}"), token, body.to_string().as_bytes());
        }
    }
    println!("{{}}");
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn agy_hooks_become_the_status_events() {
        let input = json!({ "conversationId": "c1", "transcriptPath": "C:/b/c1/t.jsonl", "cwd": "D:/p", "modelName": "gemini-4-neon-high" });
        let (event, body) = translate("SessionStart", &input).unwrap();
        assert_eq!(event, "SessionStart");
        assert_eq!(
            body,
            json!({ "session_id": "c1", "transcript_path": "C:/b/c1/t.jsonl", "cwd": "D:/p",
                "model": "gemini-4-neon-high", "hook_event_name": "SessionStart" })
        );
        assert_eq!(translate("PreInvocation", &input).unwrap().0, "UserPromptSubmit");
        assert_eq!(translate("Stop", &json!({ "fullyIdle": true })).unwrap().0, "Stop");
        assert_eq!(translate("Stop", &json!({})).unwrap().0, "Stop");
        assert!(translate("Stop", &json!({ "fullyIdle": false })).is_none());
        assert!(translate("PreToolUse", &input).is_none());
    }
}
