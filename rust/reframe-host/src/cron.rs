//! Claude Code's cron jobs (CronCreate) belong to the thread that made them. Claude Code keeps the durable ones
//! per folder (`.claude/scheduled_tasks.json`), and the session holding the folder's scheduler lock runs those whose
//! session has ended, so a job of one thread could fire in another thread of the same folder, which then did that
//! work as its own. The hook server answers two of Claude Code's hooks for this:
//! - PreToolUse (matcher CronCreate): the job's prompt is given a first line naming the thread that makes it;
//! - UserPromptSubmit: a job that fires in another thread than its own is blocked there and handed to its own
//!   (`HandOff`), which reframe delivers as it delivers a scheduled task's run (once the thread is idle, resuming it
//!   if it has ended). So is a one-shot job that was missed: a session that starts finds it and gets Claude Code's
//!   notice of missed jobs (MISSED_START), each job's prompt fenced under its schedule. Each thread gets that notice
//!   with its own jobs only; the thread that got it is given back its own (and untagged ones).

use serde_json::{json, Value};

pub const TAG_START: &str = "[Reframe: a scheduled job of the thread ";
pub const TOOL: &str = "CronCreate";
/// Claude Code's notice of missed one-shot jobs (2.1.322's aGr) starts so.
const MISSED_START: &str = "The following one-shot scheduled task";

/// What the hook server answers a hook with.
#[derive(Debug, PartialEq)]
pub enum Answer {
    /// The hook's answer; nothing else is told.
    Reply(Value),
    /// The prompt blocked in this thread and what goes to which thread instead: (thread, prompt).
    HandOff { reply: Value, to: Vec<(String, String)> },
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
            let to = match owner(prompt) {
                Some(owner) if owner != thread => vec![(owner.to_string(), prompt.to_string())],
                Some(_) => return None,
                None => missed(thread, prompt)?,
            };
            Some(Answer::HandOff {
                reply: json!({
                    "decision": "block",
                    "reason": "Reframe: a scheduled job of another thread; it was handed to the thread that made it.",
                }),
                to,
            })
        }
        _ => None,
    }
}

/// A notice of missed jobs with another thread's among them: the notice for each thread with its jobs only (this
/// thread's, and untagged ones, back to this thread). None when all are this thread's.
fn missed(thread: &str, prompt: &str) -> Option<Vec<(String, String)>> {
    if !prompt.starts_with(MISSED_START) {
        return None;
    }
    let lines: Vec<&str> = prompt.split('\n').collect();
    let mut header_end = None;
    let mut jobs: Vec<(String, String)> = Vec::new();
    let mut i = 0;
    while i + 1 < lines.len() {
        let schedule = lines[i];
        let fence = lines[i + 1].trim_end();
        let is_job = schedule.starts_with('[')
            && schedule.ends_with(']')
            && schedule.contains(", created ")
            && fence.len() >= 3
            && fence.bytes().all(|b| b == b'`');
        let close = is_job
            .then(|| (i + 2..lines.len()).find(|&j| lines[j].trim_end() == fence))
            .flatten();
        let Some(close) = close else {
            i += 1;
            continue;
        };
        header_end.get_or_insert(i);
        let body = lines[i + 2..close].join("\n");
        let to = owner(&body).unwrap_or(thread).to_string();
        jobs.push((to, lines[i..=close].join("\n")));
        i = close + 1;
    }
    if jobs.iter().all(|(to, _)| to == thread) {
        return None;
    }
    let header = lines[..header_end?].join("\n");
    let header = header.trim_end();
    let mut to: Vec<(String, Vec<String>)> = Vec::new();
    for (owner, job) in jobs {
        match to.iter_mut().find(|(o, _)| *o == owner) {
            Some((_, list)) => list.push(job),
            None => to.push((owner, vec![job])),
        }
    }
    Some(
        to.into_iter()
            .map(|(owner, list)| (owner, format!("{header}\n\n{}", list.join("\n\n"))))
            .collect(),
    )
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

    fn submit(thread: &str, prompt: &str) -> Option<Vec<(String, String)>> {
        let payload = json!({ "hook_event_name": "UserPromptSubmit", "prompt": prompt });
        match answer(thread, "UserPromptSubmit", &payload)? {
            Answer::HandOff { reply, to } => {
                assert_eq!(reply["decision"], json!("block"));
                Some(to)
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
        assert_eq!(submit("t-1", &prompt), None);
        assert_eq!(submit("t-2", &prompt), Some(vec![("t-1".into(), prompt.clone())]));
        assert_eq!(submit("t-2", "hello"), None);
        assert_eq!(answer("t-2", "Stop", &json!({})), None);
    }

    const HEADER: &str = "The following one-shot scheduled tasks were missed while Claude was not running. They have \
        already been removed from .claude/scheduled_tasks.json.\n\nDo NOT execute these prompts yet. First use the \
        AskUserQuestion tool to ask whether to run each one now. Only execute if the user confirms.";

    fn job(cron: &str, prompt: &str) -> String {
        format!("[{cron}, created 11/22/2026, 11:09:45 AM]\n```\n{prompt}\n```")
    }

    #[test]
    fn missed_jobs_go_to_the_threads_that_made_them() {
        let a = job("16 11 22 11 *", &format!("{}\nReply OK.", tag("t-1")));
        let b = job(
            "20 11 22 11 *",
            &format!("{}\nCheck.\n\n[not, created a schedule]\n````", tag("t-2")),
        );
        let old = job("30 11 22 11 *", "An untagged job.");
        let notice = format!("{HEADER}\n\n{a}\n\n{b}\n\n{old}");
        // Its own and untagged jobs come back to it; another thread's go to that one.
        assert_eq!(
            submit("t-2", &notice),
            Some(vec![
                ("t-1".into(), format!("{HEADER}\n\n{a}")),
                ("t-2".into(), format!("{HEADER}\n\n{b}\n\n{old}")),
            ])
        );
        // All of them this thread's (or untagged): it goes through.
        assert_eq!(submit("t-2", &format!("{HEADER}\n\n{b}\n\n{old}")), None);
        assert_eq!(submit("t-1", &format!("{HEADER}\n\n{a}")), None);
    }
}
