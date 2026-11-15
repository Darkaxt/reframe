//! The message tagger: `reframe-host --tag-message <anthropic|openai>`, the PreToolUse hook (matcher SendMessage) of
//! the sessions reframe starts (reframe-services' spawn_plan). Claude Code gives it the hook input (JSON on standard
//! input); for a message to another thread it answers `updatedInput` with the message after a tag naming the
//! provider and the exact model the session runs on (the newest assistant entry's `model` in its transcript), so
//! the thread that receives it knows whether a Claude or an OpenAI model wrote it. It decides nothing else (no
//! `permissionDecision`: the call goes through Claude Code's permissions as before), and anything it cannot read
//! leaves the message as it is.

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};

use serde_json::{json, Value};

const MAX_INPUT: u64 = 4 * 1024 * 1024;
/// How much of the transcript's end is read for the model (its newest entries).
const TRANSCRIPT_TAIL: u64 = 1024 * 1024;
pub const TAG_START: &str = "[from a Reframe thread on ";

pub fn run(args: &[String]) -> i32 {
    let [provider] = args else {
        return 0;
    };
    let mut input = Vec::new();
    let _ = std::io::stdin().take(MAX_INPUT).read_to_end(&mut input);
    let Ok(hook) = serde_json::from_slice::<Value>(&input) else {
        return 0;
    };
    let model = hook.get("transcript_path").and_then(Value::as_str).and_then(last_model);
    if let Some(answer) = answer(&hook, provider, model.as_deref()) {
        println!("{answer}");
    }
    0
}

/// The hook's answer for a SendMessage with a text message not tagged yet; None leaves the call as it is.
fn answer(hook: &Value, provider: &str, model: Option<&str>) -> Option<Value> {
    if hook.get("tool_name")?.as_str()? != "SendMessage" {
        return None;
    }
    let mut input = hook.get("tool_input")?.clone();
    let message = input.get("message")?.as_str()?;
    if message.starts_with(TAG_START) {
        return None;
    }
    input["message"] = json!(format!("{}\n{message}", tag(provider, model)));
    Some(json!({ "hookSpecificOutput": { "hookEventName": "PreToolUse", "updatedInput": input } }))
}

fn tag(provider: &str, model: Option<&str>) -> String {
    let family = if provider == "anthropic" {
        "Claude (Anthropic)"
    } else {
        "OpenAI"
    };
    match model {
        Some(model) => format!("{TAG_START}{family}, model {model}]"),
        None => format!("{TAG_START}{family}]"),
    }
}

/// The `model` of the transcript's newest assistant entry (not Claude Code's own `<synthetic>` ones).
fn last_model(path: &str) -> Option<String> {
    let mut file = File::open(path).ok()?;
    let length = file.metadata().ok()?.len();
    let start = length.saturating_sub(TRANSCRIPT_TAIL);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut tail = Vec::new();
    file.read_to_end(&mut tail).ok()?;
    let text = String::from_utf8_lossy(&tail);
    // From the middle of the file the first line is cut: it does not parse and is passed over.
    text.lines().rev().find_map(|line| {
        let entry = serde_json::from_str::<Value>(line).ok()?;
        if entry.get("type")?.as_str()? != "assistant" {
            return None;
        }
        let model = entry.get("message")?.get("model")?.as_str()?;
        (!model.is_empty() && !model.starts_with('<')).then(|| model.to_string())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn send(message: Value) -> Value {
        json!({ "hook_event_name": "PreToolUse", "tool_name": "SendMessage",
            "tool_input": { "to": "Docs", "message": message } })
    }

    #[test]
    fn tags_a_message_to_another_thread() {
        let answer = answer(&send(json!("hello")), "openai", Some("gpt-6.1-sol")).unwrap();
        assert_eq!(
            answer,
            json!({ "hookSpecificOutput": { "hookEventName": "PreToolUse", "updatedInput": {
                "to": "Docs",
                "message": "[from a Reframe thread on OpenAI, model gpt-6.1-sol]\nhello",
            } } })
        );
        let claude = answer_message(&send(json!("hi")), "anthropic", None);
        assert_eq!(claude, "[from a Reframe thread on Claude (Anthropic)]\nhi");
    }

    fn answer_message(hook: &Value, provider: &str, model: Option<&str>) -> String {
        answer(hook, provider, model).unwrap()["hookSpecificOutput"]["updatedInput"]["message"]
            .as_str()
            .unwrap()
            .to_string()
    }

    #[test]
    fn leaves_other_calls_alone() {
        let tagged = answer_message(&send(json!("hello")), "openai", None);
        assert_eq!(answer(&send(json!(tagged)), "openai", None), None);
        assert_eq!(
            answer(&send(json!({ "type": "shutdown_request" })), "openai", None),
            None
        );
        let bash = json!({ "tool_name": "Bash", "tool_input": { "command": "ls" } });
        assert_eq!(answer(&bash, "openai", None), None);
    }

    #[test]
    fn finds_the_newest_assistant_model() {
        let file = std::env::temp_dir().join(format!("reframe-msgtag-{}.jsonl", std::process::id()));
        let lines = [
            r#"{"type":"assistant","message":{"model":"claude-opus-5-5"}}"#,
            r#"{"type":"assistant","message":{"model":"gpt-6.1-sol"}}"#,
            r#"{"type":"assistant","message":{"model":"<synthetic>"}}"#,
            r#"{"type":"user","message":{"content":"x"}}"#,
        ];
        std::fs::write(&file, lines.join("\n")).unwrap();
        let model = last_model(file.to_str().unwrap());
        let _ = std::fs::remove_file(&file);
        assert_eq!(model.as_deref(), Some("gpt-6.1-sol"));
        assert_eq!(last_model("Z:/no/such/transcript.jsonl"), None);
    }
}
