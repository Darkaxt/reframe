//! Scheduled tasks, the thread tools' side (threads.rs): a thread asked by the user to do something on a schedule
//! saves it as `<tasks dir>/<id>/automation.toml`, in the format of Codex's automations (`version`, `id`, `kind`,
//! `name`, `prompt`, `status`, `rrule`, `target_thread_id`, `notification_policy`, `created_at`, `updated_at`), the
//! target being the thread that saved it. Reframe's shell watches the folder, works out when each task is due and
//! types its prompt into the thread at that time (resuming the thread first when it has ended). Here the tasks are
//! only saved, listed, changed and deleted.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde_json::{json, Value};

pub const FILE: &str = "automation.toml";
const MAX_PROMPT: usize = 19_000;
const NOTIFY: [&str; 3] = ["important", "all", "failed_runs_only"];

/// A task file's keys and values (strings, integers and booleans; Codex writes nothing else), in file order kept
/// by key name so that keys this side does not know survive a change.
pub type Fields = BTreeMap<String, Field>;

#[derive(Debug, Clone, PartialEq)]
pub enum Field {
    Text(String),
    Int(i64),
    Bool(bool),
}

impl Field {
    fn text(&self) -> Option<&str> {
        match self {
            Field::Text(s) => Some(s),
            _ => None,
        }
    }
}

/// Reads the flat `key = value` TOML Codex and reframe write; None when a line is not one of those.
pub fn parse(text: &str) -> Option<Fields> {
    let mut fields = Fields::new();
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let (key, value) = line.split_once('=')?;
        let key = key.trim();
        if key.is_empty() || !key.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-') {
            return None;
        }
        let value = value.trim();
        let field = if let Some(inner) = value.strip_prefix('"') {
            Field::Text(unescape(inner.strip_suffix('"')?)?)
        } else if value == "true" || value == "false" {
            Field::Bool(value == "true")
        } else {
            Field::Int(value.parse().ok()?)
        };
        fields.insert(key.to_owned(), field);
    }
    Some(fields)
}

fn unescape(text: &str) -> Option<String> {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next()? {
            'n' => out.push('\n'),
            't' => out.push('\t'),
            'r' => out.push('\r'),
            '"' => out.push('"'),
            '\\' => out.push('\\'),
            'u' => {
                let code: String = chars.by_ref().take(4).collect();
                out.push(char::from_u32(u32::from_str_radix(&code, 16).ok()?)?);
            }
            _ => return None,
        }
    }
    Some(out)
}

fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len() + 2);
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 || c as u32 == 0x7f => out.push_str(&format!("\\u{:04X}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

/// The file's text: Codex's keys first in its order, then any others.
pub fn write(fields: &Fields) -> String {
    const ORDER: [&str; 11] = [
        "version",
        "id",
        "kind",
        "name",
        "prompt",
        "status",
        "rrule",
        "target_thread_id",
        "notification_policy",
        "created_at",
        "updated_at",
    ];
    let mut out = String::new();
    let keys = ORDER
        .iter()
        .map(|k| k.to_string())
        .filter(|k| fields.contains_key(k))
        .chain(fields.keys().filter(|k| !ORDER.contains(&k.as_str())).cloned());
    for key in keys {
        let value = match &fields[&key] {
            Field::Text(s) => format!("\"{}\"", escape(s)),
            Field::Int(n) => n.to_string(),
            Field::Bool(b) => b.to_string(),
        };
        out.push_str(&format!("{key} = {value}\n"));
    }
    out
}

/// What is wrong with a recurrence rule (RFC 5545, the part reframe's scheduler follows), or None. An optional
/// `DTSTART[;TZID=zone]:YYYYMMDDTHHMMSS` line may come first, then the rule, with or without `RRULE:`.
pub fn rule_problem(rule: &str) -> Option<String> {
    let mut lines: Vec<&str> = rule.lines().map(str::trim).filter(|l| !l.is_empty()).collect();
    if lines.first().is_some_and(|l| l.starts_with("DTSTART")) {
        let start = lines.remove(0);
        let value = start.rsplit(':').next().unwrap_or("");
        let digits = value.trim_end_matches('Z').replace('T', "");
        if value == start || digits.len() != 14 || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return Some("DTSTART must look like DTSTART;TZID=Europe/Bucharest:20270301T170000.".into());
        }
    }
    let [rule] = lines.as_slice() else {
        return Some("Give one rule, for example FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=15.".into());
    };
    let rule = rule.strip_prefix("RRULE:").unwrap_or(rule);
    let mut freq = None;
    for part in rule.split(';').filter(|p| !p.is_empty()) {
        let Some((key, value)) = part.split_once('=') else {
            return Some(format!("{part} is not KEY=VALUE."));
        };
        let numbers = |min: i64, max: i64| {
            value
                .split(',')
                .all(|n| n.parse::<i64>().is_ok_and(|n| (min..=max).contains(&n)))
        };
        let ok = match key {
            "FREQ" => {
                freq = Some(value);
                ["MINUTELY", "HOURLY", "DAILY", "WEEKLY", "MONTHLY"].contains(&value)
            }
            "INTERVAL" => numbers(1, 1000),
            "COUNT" => numbers(1, 100_000),
            "BYHOUR" => numbers(0, 23),
            "BYMINUTE" | "BYSECOND" => numbers(0, 59),
            "BYMONTHDAY" => numbers(1, 31),
            "BYDAY" => value.split(',').all(|d| ["MO", "TU", "WE", "TH", "FR", "SA", "SU"].contains(&d)),
            "WKST" => value == "MO",
            "UNTIL" => {
                let digits = value.trim_end_matches('Z').replace('T', "");
                (digits.len() == 8 || digits.len() == 14) && digits.bytes().all(|b| b.is_ascii_digit())
            }
            _ => return Some(format!("{key} is not supported (FREQ, INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYHOUR, BYMINUTE, BYSECOND are).")),
        };
        if !ok {
            return Some(format!("{part} has a value out of range."));
        }
    }
    match freq {
        None => Some("FREQ is required (MINUTELY, HOURLY, DAILY, WEEKLY or MONTHLY).".into()),
        Some("MINUTELY")
            if !rule.split(';').any(|p| {
                p.strip_prefix("INTERVAL=")
                    .and_then(|n| n.parse::<u32>().ok())
                    .is_some_and(|n| n >= 5)
            }) =>
        {
            Some("A MINUTELY rule needs INTERVAL=5 or more.".into())
        }
        _ => None,
    }
}

/// A folder name for a task called `name`, not taken yet in `dir`.
fn new_id(dir: &Path, name: &str) -> String {
    let mut slug = String::new();
    for c in name.to_lowercase().chars() {
        if c.is_ascii_alphanumeric() {
            slug.push(c);
        } else if !slug.ends_with('-') && !slug.is_empty() {
            slug.push('-');
        }
        if slug.len() >= 60 {
            break;
        }
    }
    let slug = slug.trim_end_matches('-');
    let slug = if slug.is_empty() { "task" } else { slug };
    (1..)
        .map(|n| if n == 1 { slug.to_owned() } else { format!("{slug}-{n}") })
        .find(|id| !dir.join(id).exists())
        .unwrap_or_default()
}

fn valid_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 80 && id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn save(dir: &Path, fields: &Fields) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)?;
    let file = dir.join(FILE);
    let temp = dir.join(format!("{FILE}.tmp"));
    std::fs::write(&temp, write(fields))?;
    std::fs::rename(&temp, &file)
}

fn load(dir: &Path) -> Option<Fields> {
    parse(&std::fs::read_to_string(dir.join(FILE)).ok()?)
}

/// The tools: `schedule_task`, `list_tasks`, `update_task`, `delete_task`.
pub struct Tasks {
    pub dir: PathBuf,
    /// The reframe session that calls them (the target of the tasks it saves).
    pub session_id: String,
}

pub const NAMES: [&str; 4] = ["schedule_task", "list_tasks", "update_task", "delete_task"];

pub fn tools() -> Vec<Value> {
    let rule = "When it runs: an RFC 5545 recurrence rule in local time, e.g. FREQ=DAILY;BYHOUR=9;BYMINUTE=0, \
        FREQ=WEEKLY;BYDAY=MO,TH;BYHOUR=17;BYMINUTE=30, FREQ=HOURLY;INTERVAL=6, FREQ=DAILY;BYHOUR=10;BYMINUTE=0;COUNT=8 \
        (stops after 8 runs). FREQ is MINUTELY (INTERVAL 5 or more), HOURLY, DAILY, WEEKLY or MONTHLY.";
    vec![
        json!({
            "name": "schedule_task",
            "description": "Schedule a task for this thread, as the user asks for one (\"check X every Monday\", \"remind me \
                tomorrow at 9\"): at each run Reframe types the prompt into this thread, resuming it first if it has ended, \
                or (new_chat) starts a new thread with it in this thread's folder. Runs missed while Reframe was closed run \
                once when it opens again.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "A short name for the task." },
                    "prompt": { "type": "string", "description": "What to do at each run, complete on its own (the run may come days later): the goal, where to look, when to stay quiet and when to tell the user." },
                    "rrule": { "type": "string", "description": rule },
                    "new_chat": { "type": "boolean", "description": "Start each run in a new thread instead of this one (default false)." },
                    "notify": { "type": "string", "enum": NOTIFY, "description": "Notifications Reframe shows for runs: important (default: runs that could not be delivered), all, or failed_runs_only." },
                },
                "required": ["name", "prompt", "rrule"],
            },
        }),
        json!({
            "name": "list_tasks",
            "description": "List the scheduled tasks of this thread (all: every thread's), with their ids, rules and status.",
            "inputSchema": { "type": "object", "properties": { "all": { "type": "boolean" } } },
        }),
        json!({
            "name": "update_task",
            "description": "Change a scheduled task (by id, from list_tasks): its name, prompt, rule, or status (PAUSED stops its runs, ACTIVE starts them again).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "id": { "type": "string" },
                    "name": { "type": "string" },
                    "prompt": { "type": "string" },
                    "rrule": { "type": "string", "description": rule },
                    "status": { "type": "string", "enum": ["ACTIVE", "PAUSED"] },
                    "notify": { "type": "string", "enum": NOTIFY },
                },
                "required": ["id"],
            },
        }),
        json!({
            "name": "delete_task",
            "description": "Delete a scheduled task (by id, from list_tasks) once it is no longer needed.",
            "inputSchema": { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] },
        }),
    ]
}

/// The short guide the server gives the threads with its tools (MCP `instructions`).
pub const INSTRUCTIONS: &str = "Reframe runs scheduled tasks for its threads, like Codex automations. When the user asks \
    for something to happen later or repeatedly, save it with schedule_task instead of waiting or looping: Reframe types \
    the prompt into this thread at each run (resuming the thread if it has ended) and keeps the schedule while the thread \
    is closed. Write the prompt so it stands on its own, does the same thing each time, stays quiet when nothing \
    changed, and says when to tell the user. A message starting with \"[Reframe scheduled task: ...]\" is such a run: do \
    it, then go back to what you were doing. Pause or delete tasks that are no longer needed (list_tasks, update_task, \
    delete_task).";

impl Tasks {
    pub fn call(&self, tool: &str, arguments: &Value) -> Result<String, String> {
        let text = |key: &str| {
            arguments
                .get(key)
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|s| !s.is_empty())
        };
        match tool {
            "schedule_task" => {
                let (Some(name), Some(prompt), Some(rule)) = (text("name"), text("prompt"), text("rrule")) else {
                    return Err("Give the task a name, a prompt and an rrule.".into());
                };
                check_prompt(prompt)?;
                if let Some(problem) = rule_problem(rule) {
                    return Err(problem);
                }
                let notify = notify_of(text("notify"))?.unwrap_or("important");
                let new_chat = arguments.get("new_chat").and_then(Value::as_bool).unwrap_or(false);
                std::fs::create_dir_all(&self.dir).map_err(|e| e.to_string())?;
                let id = new_id(&self.dir, name);
                let now = now_ms();
                let mut fields = Fields::new();
                fields.insert("version".into(), Field::Int(1));
                fields.insert("id".into(), Field::Text(id.clone()));
                fields.insert(
                    "kind".into(),
                    Field::Text(if new_chat { "new_chat" } else { "heartbeat" }.into()),
                );
                fields.insert("name".into(), Field::Text(name.into()));
                fields.insert("prompt".into(), Field::Text(prompt.into()));
                fields.insert("status".into(), Field::Text("ACTIVE".into()));
                fields.insert("rrule".into(), Field::Text(rule.into()));
                fields.insert("target_thread_id".into(), Field::Text(self.session_id.clone()));
                fields.insert("notification_policy".into(), Field::Text(notify.into()));
                fields.insert("created_at".into(), Field::Int(now));
                fields.insert("updated_at".into(), Field::Int(now));
                save(&self.dir.join(&id), &fields).map_err(|e| e.to_string())?;
                Ok(format!(
                    "Scheduled \"{name}\" (id {id}, {rule}). Reframe shows it under Tasks in the top bar; it runs while \
                     Reframe is open, and a run missed while it was closed runs once when it opens."
                ))
            }
            "list_tasks" => {
                let all = arguments.get("all").and_then(Value::as_bool).unwrap_or(false);
                let mut tasks: Vec<Value> = std::fs::read_dir(&self.dir)
                    .into_iter()
                    .flatten()
                    .flatten()
                    .filter_map(|entry| load(&entry.path()))
                    .filter(|f| {
                        all || f.get("target_thread_id").and_then(Field::text) == Some(self.session_id.as_str())
                    })
                    .map(|f| {
                        let get = |k: &str| f.get(k).and_then(Field::text).unwrap_or("");
                        json!({ "id": get("id"), "name": get("name"), "status": get("status"), "rrule": get("rrule"),
                            "kind": get("kind"), "prompt": get("prompt") })
                    })
                    .collect();
                tasks.sort_by(|a, b| a["id"].as_str().cmp(&b["id"].as_str()));
                Ok(if tasks.is_empty() {
                    "No scheduled tasks.".into()
                } else {
                    Value::Array(tasks).to_string()
                })
            }
            "update_task" | "delete_task" => {
                let Some(id) = text("id").filter(|id| valid_id(id)) else {
                    return Err("Give the task's id (list_tasks shows it).".into());
                };
                let dir = self.dir.join(id);
                let Some(mut fields) = load(&dir) else {
                    return Err(format!("There is no task {id}."));
                };
                if tool == "delete_task" {
                    std::fs::remove_dir_all(&dir).map_err(|e| e.to_string())?;
                    return Ok(format!("Deleted the task {id}."));
                }
                if let Some(name) = text("name") {
                    fields.insert("name".into(), Field::Text(name.into()));
                }
                if let Some(prompt) = text("prompt") {
                    check_prompt(prompt)?;
                    fields.insert("prompt".into(), Field::Text(prompt.into()));
                }
                if let Some(rule) = text("rrule") {
                    if let Some(problem) = rule_problem(rule) {
                        return Err(problem);
                    }
                    fields.insert("rrule".into(), Field::Text(rule.into()));
                }
                match text("status") {
                    Some(s @ ("ACTIVE" | "PAUSED")) => {
                        fields.insert("status".into(), Field::Text(s.into()));
                    }
                    Some(_) => return Err("status is ACTIVE or PAUSED.".into()),
                    None => {}
                }
                if let Some(notify) = notify_of(text("notify"))? {
                    fields.insert("notification_policy".into(), Field::Text(notify.into()));
                }
                fields.insert("updated_at".into(), Field::Int(now_ms()));
                save(&dir, &fields).map_err(|e| e.to_string())?;
                Ok(format!("Updated the task {id}."))
            }
            _ => Err("Unknown tool.".into()),
        }
    }
}

fn check_prompt(prompt: &str) -> Result<(), String> {
    if prompt.chars().count() > MAX_PROMPT {
        return Err("The prompt is too long; put the details in a file and point to it.".into());
    }
    Ok(())
}

fn notify_of(value: Option<&str>) -> Result<Option<&str>, String> {
    match value {
        None => Ok(None),
        Some(v) if NOTIFY.contains(&v) => Ok(Some(v)),
        Some(_) => Err("notify is important, all or failed_runs_only.".into()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp() -> PathBuf {
        let dir = std::env::temp_dir().join(format!("reframe-tasks-{}-{}", std::process::id(), now_ms()));
        let _ = std::fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn reads_and_writes_codex_files() {
        let text = "version = 1\nid = \"watch\"\nkind = \"heartbeat\"\nname = \"Watch \\\"it\\\"\"\nprompt = \"a\\nb \\u00e9\"\nstatus = \"ACTIVE\"\nrrule = \"DTSTART;TZID=Europe/Bucharest:20270301T170000\\nRRULE:FREQ=WEEKLY;BYDAY=MO\"\ntarget_thread_id = \"01a\"\ncreated_at = 1794783674280\nupdated_at = 1794783674280\nextra = true\n";
        let fields = parse(text).unwrap();
        assert_eq!(fields["name"], Field::Text("Watch \"it\"".into()));
        assert_eq!(fields["prompt"], Field::Text("a\nb é".into()));
        assert_eq!(fields["extra"], Field::Bool(true));
        assert_eq!(parse(&write(&fields)).unwrap(), fields);
        assert!(write(&fields).starts_with("version = 1\nid = \"watch\"\n"));
        assert_eq!(parse("not toml"), None);
    }

    #[test]
    fn checks_rules() {
        for good in [
            "FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=15;BYSECOND=0",
            "DTSTART;TZID=Europe/Bucharest:20270301T170000\nRRULE:FREQ=WEEKLY;BYDAY=MO;BYHOUR=17;BYMINUTE=0;BYSECOND=0",
            "FREQ=DAILY;BYHOUR=10;BYMINUTE=0;BYSECOND=0;COUNT=8",
            "FREQ=HOURLY;INTERVAL=6",
            "FREQ=MINUTELY;INTERVAL=5",
            "FREQ=MONTHLY;BYMONTHDAY=1;UNTIL=20271231",
        ] {
            assert_eq!(rule_problem(good), None, "{good}");
        }
        for bad in [
            "BYHOUR=9",
            "FREQ=YEARLY",
            "FREQ=DAILY;BYHOUR=24",
            "FREQ=MINUTELY",
            "FREQ=DAILY;BYSETPOS=1",
            "FREQ=WEEKLY;BYDAY=1MO",
        ] {
            assert!(rule_problem(bad).is_some(), "{bad}");
        }
    }

    #[test]
    fn schedules_lists_updates_and_deletes() {
        let tasks = Tasks {
            dir: temp(),
            session_id: "s1".into(),
        };
        let made = tasks
            .call(
                "schedule_task",
                &json!({ "name": "Check mirror, weekly!", "prompt": "Check it", "rrule": "FREQ=DAILY;BYHOUR=9" }),
            )
            .unwrap();
        assert!(made.contains("check-mirror-weekly"), "{made}");
        tasks
            .call(
                "schedule_task",
                &json!({ "name": "Check mirror weekly", "prompt": "x", "rrule": "FREQ=DAILY", "new_chat": true }),
            )
            .unwrap();
        let fields = load(&tasks.dir.join("check-mirror-weekly")).unwrap();
        assert_eq!(fields["target_thread_id"], Field::Text("s1".into()));
        assert_eq!(fields["kind"], Field::Text("heartbeat".into()));
        assert_eq!(
            load(&tasks.dir.join("check-mirror-weekly-2")).unwrap()["kind"],
            Field::Text("new_chat".into())
        );
        let other = Tasks {
            dir: tasks.dir.clone(),
            session_id: "s2".into(),
        };
        assert_eq!(other.call("list_tasks", &json!({})).unwrap(), "No scheduled tasks.");
        let all: Value = serde_json::from_str(&other.call("list_tasks", &json!({ "all": true })).unwrap()).unwrap();
        assert_eq!(all.as_array().unwrap().len(), 2);
        tasks
            .call(
                "update_task",
                &json!({ "id": "check-mirror-weekly", "status": "PAUSED" }),
            )
            .unwrap();
        assert_eq!(
            load(&tasks.dir.join("check-mirror-weekly")).unwrap()["status"],
            Field::Text("PAUSED".into())
        );
        assert!(tasks
            .call(
                "update_task",
                &json!({ "id": "check-mirror-weekly", "rrule": "FREQ=YEARLY" })
            )
            .is_err());
        assert!(tasks.call("update_task", &json!({ "id": "../x" })).is_err());
        tasks
            .call("delete_task", &json!({ "id": "check-mirror-weekly" }))
            .unwrap();
        assert!(!tasks.dir.join("check-mirror-weekly").exists());
        assert!(tasks
            .call(
                "schedule_task",
                &json!({ "name": "a", "prompt": "b", "rrule": "BYHOUR=1" })
            )
            .is_err());
        let _ = std::fs::remove_dir_all(&tasks.dir);
    }
}
