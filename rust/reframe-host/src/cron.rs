//! Claude Code's cron jobs (CronCreate) belong to the thread that made them. Claude Code keeps the durable ones
//! per folder (`.claude/scheduled_tasks.json`), and the session holding the folder's scheduler lock runs those whose
//! session has ended, so a job of one thread could fire in another thread of the same folder, which then did that
//! work as its own. The hook server answers two of Claude Code's hooks for this:
//! - PreToolUse (matcher CronCreate): the job's prompt is given a first line naming the thread that makes it;
//! - UserPromptSubmit: a job that fires in another thread than its own is blocked there and handed to its own
//!   (`HandOff`), which reframe delivers as it delivers a scheduled task's run (once the thread is idle, resuming it
//!   if it has ended).

use serde_json::{json, Value};

pub const TAG_START: &str = "[Reframe: a scheduled job of the thread ";
pub const TOOL: &str = "CronCreate";

/// What the hook server answers a hook with.
#[derive(Debug, PartialEq)]
pub enum Answer {
    /// The hook's answer; nothing else is told.
    Reply(Value),
    /// The prompt of `owner`'s job, blocked in this thread: handed to `owner`.
    HandOff {
        reply: Value,
        owner: String,
        prompt: String,
    },
}

fn tag(thread: &str) -> String {
    format!("{TAG_START}{thread}]")
}

/// The thread a job's prompt names on its first line.
pub fn owner(prompt: &str) -> Option<&str> {
    let (id, _) = prompt.trim_start().strip_prefix(TAG_START)?.split_once(']')?;
    (!id.is_empty()).then_some(id)
}

/// The answer to `event` of the thread `thread`; None leaves the hook to the rest of the host (its status).
pub fn answer(thread: &str, event: &str, payload: &Value) -> Option<Answer> {
    match event {
        // Only CronCreate's are sent here; any other call goes through as it is.
        "PreToolUse" => Some(Answer::Reply(tag_job(thread, payload).unwrap_or_else(|| json!({})))),
        "UserPromptSubmit" => {
            let prompt = payload.get("prompt")?.as_str()?;
            let owner = owner(prompt).filter(|owner| *owner != thread)?;
            Some(Answer::HandOff {
                reply: json!({
                    "decision": "block",
                    "reason": "Reframe: this scheduled job belongs to another thread; it was handed to that thread.",
                }),
                owner: owner.to_string(),
                prompt: prompt.to_string(),
            })
        }
        _ => None,
    }
}

/// CronCreate's input with its prompt naming this thread (a job copied from another's is this thread's now).
fn tag_job(thread: &str, payload: &Value) -> Option<Value> {
    if payload.get("tool_name")?.as_str()? != TOOL {
        return None;
    }
    let mut input = payload.get("tool_input")?.clone();
    let prompt = input.get("prompt")?.as_str()?;
    let rest = match owner(prompt) {
        Some(_) => prompt.trim_start().split_once('\n').map_or("", |(_, rest)| rest),
        None => prompt,
    };
    input["prompt"] = json!(format!("{}\n{rest}", tag(thread)));
    Some(json!({ "hookSpecificOutput": { "hookEventName": "PreToolUse", "updatedInput": input } }))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn create(prompt: &str) -> Value {
        json!({ "hook_event_name": "PreToolUse", "tool_name": "CronCreate",
            "tool_input": { "cron": "7,27,47 * * * *", "durable": true, "recurring": true, "prompt": prompt } })
    }

    fn tagged(thread: &str, prompt: &str) -> String {
        match answer(thread, "PreToolUse", &create(prompt)) {
            Some(Answer::Reply(reply)) => {
                let input = &reply["hookSpecificOutput"]["updatedInput"];
                assert_eq!(input["durable"], json!(true));
                assert_eq!(input["cron"], json!("7,27,47 * * * *"));
                input["prompt"].as_str().unwrap().to_string()
            }
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn a_job_names_the_thread_that_makes_it() {
        let prompt = tagged("t-1", "Check the build.");
        assert_eq!(prompt, "[Reframe: a scheduled job of the thread t-1]\nCheck the build.");
        assert_eq!(owner(&prompt), Some("t-1"));
        // A job made from another thread's prompt is this thread's.
        assert_eq!(
            tagged("t-2", &prompt),
            "[Reframe: a scheduled job of the thread t-2]\nCheck the build."
        );
        // Other tools go through as they are.
        let bash = json!({ "tool_name": "Bash", "tool_input": { "command": "ls" } });
        assert_eq!(answer("t-1", "PreToolUse", &bash), Some(Answer::Reply(json!({}))));
    }

    #[test]
    fn a_job_fired_in_another_thread_is_handed_to_its_own() {
        let prompt = tagged("t-1", "Check the build.");
        let submit = |prompt: &str| json!({ "hook_event_name": "UserPromptSubmit", "prompt": prompt });
        assert_eq!(answer("t-1", "UserPromptSubmit", &submit(&prompt)), None);
        match answer("t-2", "UserPromptSubmit", &submit(&prompt)) {
            Some(Answer::HandOff {
                reply,
                owner,
                prompt: handed,
            }) => {
                assert_eq!(reply["decision"], json!("block"));
                assert_eq!(owner, "t-1");
                assert_eq!(handed, prompt);
            }
            other => panic!("{other:?}"),
        }
        assert_eq!(answer("t-2", "UserPromptSubmit", &submit("hello")), None);
        assert_eq!(answer("t-2", "Stop", &json!({})), None);
    }
}
