//! The thread tools: `reframe-host --thread-tools <url> <token>`, an MCP server (JSON-RPC over standard input and
//! output, one message per line) that the sessions reframe starts get through `--mcp-config` (reframe-services'
//! spawn_plan). Its one tool, `open_thread`, asks reframe to open another thread (a session of its own, in a tile the
//! user sees and can take over), on OpenAI or Claude, with a first prompt: it posts the request to the host's hook
//! server as the session's `OpenThread` event, and the attached reframe creates the thread as its New session dialog
//! would. The two threads then talk with message_thread, whatever their providers (SendMessage, tagged with each one's
//! provider and model by msgtag.rs, reaches only Claude sessions).

use std::io::{BufRead, Write};

use serde_json::{json, Value};

use crate::statusline;
use crate::tasks::{self, Tasks};

/// Characters: reframe takes a first prompt of up to 20000 (NewSession.MaxPrompt), with the line saying who opened it.
const MAX_PROMPT: usize = 19_000;

pub fn run(args: &[String]) -> i32 {
    let (url, token, tasks) = match args {
        [url, token] => (url, token, None),
        [url, token, dir] => (
            url,
            token,
            session_of(url).map(|session_id| Tasks {
                dir: dir.into(),
                session_id,
            }),
        ),
        _ => return 2,
    };
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();
    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            break;
        };
        let Ok(message) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        if let Some(answer) = answer(&message, tasks.as_ref(), &mut |body| {
            statusline::post(url, token, body).unwrap_or(false)
        }) {
            if writeln!(stdout, "{answer}").and_then(|_| stdout.flush()).is_err() {
                break;
            }
        }
    }
    0
}

/// The answer to one JSON-RPC message (none for a notification). `post` sends the request to reframe and says
/// whether the host took it.
/// The reframe session of the hook address `http://…/hook/<session id>/OpenThread`.
fn session_of(url: &str) -> Option<String> {
    let rest = &url[url.find("/hook/")? + 6..];
    crate::hooks::decode_uri_component(rest.split('/').next()?)
}

fn answer(message: &Value, tasks: Option<&Tasks>, post: &mut dyn FnMut(&[u8]) -> bool) -> Option<Value> {
    let id = message.get("id")?.clone();
    let method = message.get("method").and_then(Value::as_str).unwrap_or("");
    let result = match method {
        "initialize" => {
            let mut result = json!({
                "protocolVersion": message["params"]["protocolVersion"].as_str().unwrap_or("2025-06-18"),
                "capabilities": { "tools": {} },
                "serverInfo": { "name": "reframe", "version": env!("CARGO_PKG_VERSION") },
            });
            result["instructions"] = json!(if tasks.is_some() {
                format!("{} {}", tasks::INSTRUCTIONS, MESSAGING)
            } else {
                MESSAGING.to_owned()
            });
            result
        }
        "ping" => json!({}),
        "tools/list" => {
            let mut tools = vec![tool(), message_tool(), wake_tool(), close_tool()];
            if tasks.is_some() {
                tools.extend(tasks::tools());
            }
            json!({ "tools": tools })
        }
        "tools/call" if message["params"]["name"] == "open_thread" => call(&message["params"]["arguments"], post),
        "tools/call" if message["params"]["name"] == "message_thread" => {
            let arguments = &message["params"]["arguments"];
            wake(arguments, arguments["wake"].as_bool().unwrap_or(false), post)
        }
        "tools/call" if message["params"]["name"] == "wake_thread" => wake(&message["params"]["arguments"], true, post),
        "tools/call" if message["params"]["name"] == "close_thread" => close(&message["params"]["arguments"], post),
        "tools/call" => match (tasks, message["params"]["name"].as_str()) {
            (Some(tasks), Some(name)) if tasks::NAMES.contains(&name) => {
                match tasks.call(name, &message["params"]["arguments"]) {
                    Ok(text) => text_result(false, &text),
                    Err(text) => text_result(true, &text),
                }
            }
            _ => text_result(true, "Unknown tool."),
        },
        _ => {
            return Some(json!({ "jsonrpc": "2.0", "id": id,
                "error": { "code": -32601, "message": format!("Unknown method {method}") } }))
        }
    };
    Some(json!({ "jsonrpc": "2.0", "id": id, "result": result }))
}

fn tool() -> Value {
    json!({
        "name": "open_thread",
        "description": "Open another Reframe thread: a separate Claude Code session in a tile of its own that the user \
            sees and can take over, running on the provider you choose, OpenAI, Claude or Gemini. It starts with your prompt, told which \
            thread opened it. Once it runs, talk to it with message_thread to its name (whatever its provider; SendMessage \
            reaches only Claude sessions). Use it to hand work to an OpenAI model, or to get a second opinion.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": { "type": "string", "description": "The new thread's name, which message_thread reaches it by." },
                "prompt": { "type": "string", "description": "Its first prompt: the task, with what it needs to know." },
                "provider": { "type": "string", "enum": ["openai", "claude", "gemini"], "description": "Which models the new thread runs on: openai, claude or gemini (Google's, through the Antigravity CLI). Always chosen, never assumed." },
                "folder": { "type": "string", "description": "An absolute folder to work in; this thread's folder by default." },
                "model": { "type": "string", "description": "A model. For openai name one explicitly (the user's saved default is used if left out and can be outdated: an unknown model makes the thread fail at its first request, and Reframe then tells you). The user's default for the provider if left out." },
                "reasoning_effort": { "type": "string", "enum": ["low", "medium", "high", "xhigh", "max"], "description": "The reasoning effort; the user's default for the provider if left out." },
            },
            "required": ["name", "prompt", "provider"],
        },
    })
}

fn call(arguments: &Value, post: &mut dyn FnMut(&[u8]) -> bool) -> Value {
    let text = |key: &str| {
        arguments
            .get(key)
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
    };
    let (Some(name), Some(prompt)) = (text("name"), text("prompt")) else {
        return text_result(true, "Give the thread a name and a prompt.");
    };
    if prompt.chars().count() > MAX_PROMPT {
        return text_result(
            true,
            "The prompt is too long; put the details in a file and point to it.",
        );
    }
    let provider = match text("provider") {
        Some("openai") => "openai",
        Some("claude") => "claude",
        Some("gemini") => "gemini",
        _ => return text_result(true, "Choose the provider: openai, claude or gemini."),
    };
    let folder = text("folder");
    if folder.is_some_and(|f| !std::path::Path::new(f).is_absolute()) {
        return text_result(true, "folder must be an absolute path.");
    }
    // Advertised as reasoning_effort: Claude Code does not show an MCP tool a parameter called effort.
    let effort = match text("reasoning_effort").or_else(|| text("effort")) {
        None => None,
        Some(e @ ("low" | "medium" | "high" | "xhigh" | "max")) => Some(e),
        Some(_) => return text_result(true, "effort is one of low, medium, high, xhigh or max."),
    };
    let request = json!({ "name": name, "prompt": prompt, "provider": provider, "folder": folder, "model": text("model"), "effort": effort });
    if !post(request.to_string().as_bytes()) {
        return text_result(true, "Reframe did not take the request: no Reframe window is attached to this thread's host (or it is not running), so nothing was opened. Try again once Reframe is open.");
    }
    let family = match provider {
        "claude" => "Claude",
        "gemini" => "Gemini",
        _ => "OpenAI",
    };
    text_result(
        false,
        &format!(
            "Reframe was asked to open the thread \"{name}\" on {family} in {}. It opens in a tile beside this one if \
             Reframe's window is open, and starts with your prompt. When it runs it is listed by ListAgents; message it \
             with SendMessage. Its replies come to you as messages.",
            folder.unwrap_or("this thread's folder")
        ),
    )
}

/// Part of the server's guide: messaging other threads. Without it threads told the user they could not reach a
/// running one (a Gemini thread has no SendMessage).
const MESSAGING: &str = "To give another Reframe thread a message (Claude, ChatGPT or Gemini, running or ended), use \
    message_thread with its name; an ended thread gets it when it next starts, or now with wake: true. A message \
    starting with \"[message from the Reframe thread ...]\" came that way: answer with message_thread to that name \
    when an answer is wanted.";

fn message_tool() -> Value {
    json!({
        "name": "message_thread",
        "description": "Send a message to another Reframe thread, by the name Reframe shows: a Claude, ChatGPT or \
            Gemini thread, running or ended (any provider; SendMessage reaches only Claude sessions). A running thread \
            gets it at once (a working Claude thread queues it, as when the user types while it works), unless the user \
            is in that thread's tile or writing in it, or it asks a permission or a question; a busy Gemini thread gets \
            it when idle. An ended thread is not started: it gets the message when it next runs (the user's Resume, a \
            task, a wake), unless wake is true, which resumes it now (its own conversation, as the user's Resume does). \
            The message is marked with your thread's name, so it can answer with message_thread too. If no thread has \
            that name, Reframe tells you in a message.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": { "type": "string", "description": "The name of the thread, as Reframe shows it." },
                "message": { "type": "string", "description": "What it should do or know: stand-alone, as it does not know why you write." },
                "wake": { "type": "boolean", "description": "Resume the thread now if it has ended. False by default: it gets the message when it next starts." },
            },
            "required": ["name", "message"],
        },
    })
}

fn wake_tool() -> Value {
    json!({
        "name": "wake_thread",
        "description": "The same as message_thread with wake: true (its older name): an ended thread is resumed to get \
            the message.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": { "type": "string", "description": "The name of the thread to wake, as Reframe shows it." },
                "message": { "type": "string", "description": "What it should do or know: stand-alone, as it does not know why you write." },
            },
            "required": ["name", "message"],
        },
    })
}

fn wake(arguments: &Value, resume: bool, post: &mut dyn FnMut(&[u8]) -> bool) -> Value {
    let text = |key: &str| {
        arguments
            .get(key)
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
    };
    let (Some(name), Some(message)) = (text("name"), text("message")) else {
        return text_result(true, "Give the name of the thread and a message.");
    };
    if message.chars().count() > MAX_PROMPT {
        return text_result(
            true,
            "The message is too long; put the details in a file and point to it.",
        );
    }
    if !post(
        json!({ "wake": name, "message": message, "resume": resume })
            .to_string()
            .as_bytes(),
    ) {
        return text_result(true, "Reframe's host did not take the request (is Reframe running?).");
    }
    text_result(
        false,
        &format!(
            "Reframe was asked to give the thread \"{name}\" your message. A running thread gets it now; {} If no \
             thread has that name, a message tells you.",
            if resume {
                "an ended one is resumed first, which takes a minute."
            } else {
                "an ended one is not started but gets it when it next runs (a message tells you)."
            }
        ),
    )
}

fn close_tool() -> Value {
    json!({
        "name": "close_thread",
        "description": "Shut down another Reframe thread (for example one you opened with open_thread) when its work is done. \
            action \"end\" (the default) stops its Claude session and closes its tile; the thread stays in Reframe's list, \
            ended, and message_thread with wake: true or the user's Resume brings it back. action \"archive\" ends it and then archives its \
            conversation to a verified ZIP, and action \"delete\" ends it and deletes it for good; both ask the user to confirm in \
            Reframe's window first, unless you opened the thread yourself with open_thread (then they just happen), and delete \
            is refused for any thread you did not open. Not for the thread you run in. Give several threads in names to close them all in one request: they are \
            closed one after the other and Reframe sends you one message with the outcome of all, so never send one request per thread. \
            Reframe shows the user no popup for threads you opened yourself.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": { "type": "string", "description": "The name of the thread to close, as Reframe shows it." },
                "names": { "type": "array", "items": { "type": "string" }, "description": "Several threads to close in one request (instead of name)." },
                "action": { "type": "string", "enum": ["end", "archive", "delete"], "description": "end (default), archive or delete (only a thread you opened)." },
            },
        },
    })
}

fn close(arguments: &Value, post: &mut dyn FnMut(&[u8]) -> bool) -> Value {
    let text = |key: &str| {
        arguments
            .get(key)
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
    };
    let mut names: Vec<String> = arguments
        .get("names")
        .and_then(Value::as_array)
        .map(|a| {
            a.iter()
                .filter_map(Value::as_str)
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .collect()
        })
        .unwrap_or_default();
    if let Some(name) = text("name") {
        names.insert(0, name.to_string());
    }
    names.dedup();
    if names.is_empty() {
        return text_result(true, "Give the name (or names) of the thread to close.");
    }
    let action = match text("action") {
        None | Some("end") => "end",
        Some("archive") => "archive",
        Some("delete") => "delete",
        Some(_) => return text_result(true, "action is end, archive or delete."),
    };
    let named = names.iter().map(|n| format!("\"{n}\"")).collect::<Vec<_>>().join(", ");
    if !post(json!({ "close": names, "action": action }).to_string().as_bytes()) {
        return text_result(true, "Reframe's host did not take the request (is Reframe running?).");
    }
    text_result(
        false,
        &format!("Reframe was asked to {action}: {named}. One message tells you the outcome of all of them."),
    )
}

fn text_result(error: bool, text: &str) -> Value {
    json!({ "content": [{ "type": "text", "text": text }], "isError": error })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rpc(method: &str, params: Value) -> Value {
        json!({ "jsonrpc": "2.0", "id": 7, "method": method, "params": params })
    }

    #[test]
    fn speaks_mcp() {
        let mut never = |_: &[u8]| panic!("nothing is posted");
        let init = answer(
            &rpc("initialize", json!({ "protocolVersion": "2025-03-26" })),
            None,
            &mut never,
        )
        .unwrap();
        assert_eq!(init["result"]["protocolVersion"], "2025-03-26");
        assert_eq!(init["id"], 7);
        let list = answer(&rpc("tools/list", json!({})), None, &mut never).unwrap();
        assert_eq!(list["result"]["tools"][0]["name"], "open_thread");
        assert_eq!(
            answer(&json!({ "method": "notifications/initialized" }), None, &mut never),
            None
        );
        assert_eq!(
            answer(&rpc("resources/list", json!({})), None, &mut never).unwrap()["error"]["code"],
            -32601
        );
    }

    #[test]
    fn posts_the_request_to_reframe() {
        let mut posted = Vec::new();
        let call = rpc(
            "tools/call",
            json!({ "name": "open_thread", "arguments": { "name": " Docs ", "prompt": "Review it", "provider": "openai", "folder": "D:\\P" } }),
        );
        let result = answer(&call, None, &mut |body| {
            posted.push(serde_json::from_slice::<Value>(body).unwrap());
            true
        })
        .unwrap();
        assert_eq!(result["result"]["isError"], false);
        assert_eq!(
            posted,
            [
                json!({ "name": "Docs", "prompt": "Review it", "provider": "openai", "folder": "D:\\P", "model": null, "effort": null })
            ]
        );
        let refused = answer(&call, None, &mut |_| false).unwrap();
        assert_eq!(refused["result"]["isError"], true);
    }

    #[test]
    fn closes_a_thread_by_name() {
        let mut posted = Vec::new();
        for arguments in [
            json!({ "name": " Docs " }),
            json!({ "name": "Docs", "action": "archive" }),
            json!({ "names": ["A", " B ", ""], "action": "delete" }),
        ] {
            let call = rpc("tools/call", json!({ "name": "close_thread", "arguments": arguments }));
            let result = answer(&call, None, &mut |body| {
                posted.push(serde_json::from_slice::<Value>(body).unwrap());
                true
            })
            .unwrap();
            assert_eq!(result["result"]["isError"], false);
        }
        assert_eq!(
            posted,
            [
                json!({ "close": ["Docs"], "action": "end" }),
                json!({ "close": ["Docs"], "action": "archive" }),
                json!({ "close": ["A", "B"], "action": "delete" })
            ]
        );
        let bad = rpc(
            "tools/call",
            json!({ "name": "close_thread", "arguments": { "name": "Docs", "action": "remove" } }),
        );
        let refused = answer(&bad, None, &mut |_| panic!("nothing is posted")).unwrap();
        assert_eq!(refused["result"]["isError"], true);
    }

    #[test]
    fn takes_the_reasoning_effort() {
        let list = answer(&rpc("tools/list", json!({})), None, &mut |_| true).unwrap();
        let properties = &list["result"]["tools"][0]["inputSchema"]["properties"];
        assert!(properties["reasoning_effort"].is_object());
        let call = |effort: &str| {
            rpc(
                "tools/call",
                json!({ "name": "open_thread", "arguments": { "name": "W", "prompt": "p", "provider": "openai", "reasoning_effort": effort } }),
            )
        };
        let mut posted = Vec::new();
        let ok = answer(&call("low"), None, &mut |body| {
            posted.push(serde_json::from_slice::<Value>(body).unwrap());
            true
        })
        .unwrap();
        assert_eq!(ok["result"]["isError"], false);
        assert_eq!(posted[0]["effort"], "low");
        let bad = answer(&call("bogus"), None, &mut |_| panic!("posted a bad effort")).unwrap();
        assert_eq!(bad["result"]["isError"], true);
    }

    #[test]
    fn wakes_a_thread_by_name() {
        let mut posted = Vec::new();
        let call = rpc(
            "tools/call",
            json!({ "name": "wake_thread", "arguments": { "name": " Navic ", "message": " Carry on " } }),
        );
        let result = answer(&call, None, &mut |body| {
            posted.push(serde_json::from_slice::<Value>(body).unwrap());
            true
        })
        .unwrap();
        assert_eq!(result["result"]["isError"], false);
        assert_eq!(
            posted,
            [json!({ "wake": "Navic", "message": "Carry on", "resume": true })]
        );
        let bad = rpc(
            "tools/call",
            json!({ "name": "wake_thread", "arguments": { "name": "Navic" } }),
        );
        assert_eq!(answer(&bad, None, &mut |_| true).unwrap()["result"]["isError"], true);
        assert_eq!(answer(&call, None, &mut |_| false).unwrap()["result"]["isError"], true);
    }

    #[test]
    fn messages_a_thread_waking_it_only_when_asked() {
        let mut posted = Vec::new();
        for arguments in [
            json!({ "name": "Navic", "message": "Hi" }),
            json!({ "name": "Navic", "message": "Hi", "wake": true }),
        ] {
            let call = rpc(
                "tools/call",
                json!({ "name": "message_thread", "arguments": arguments }),
            );
            let result = answer(&call, None, &mut |body| {
                posted.push(serde_json::from_slice::<Value>(body).unwrap());
                true
            })
            .unwrap();
            assert_eq!(result["result"]["isError"], false);
        }
        assert_eq!(
            posted,
            [
                json!({ "wake": "Navic", "message": "Hi", "resume": false }),
                json!({ "wake": "Navic", "message": "Hi", "resume": true }),
            ]
        );
    }

    #[test]
    fn has_the_task_tools_with_a_tasks_folder() {
        let mut never = |_: &[u8]| panic!("nothing is posted");
        assert_eq!(
            session_of("http://127.0.0.1:4100/hook/s%201/OpenThread").as_deref(),
            Some("s 1")
        );
        let dir = std::env::temp_dir().join(format!("reframe-thread-tasks-{}", std::process::id()));
        let tasks = Tasks {
            dir: dir.clone(),
            session_id: "s1".into(),
        };
        let init = answer(&rpc("initialize", json!({})), Some(&tasks), &mut never).unwrap();
        assert!(init["result"]["instructions"]
            .as_str()
            .unwrap()
            .contains("schedule_task"));
        assert!(init["result"]["instructions"]
            .as_str()
            .unwrap()
            .contains("message_thread"));
        // Messaging other threads is told without a tasks folder too.
        let bare = answer(&rpc("initialize", json!({})), None, &mut never).unwrap();
        assert_eq!(bare["result"]["instructions"], MESSAGING);
        let list = answer(&rpc("tools/list", json!({})), Some(&tasks), &mut never).unwrap();
        assert_eq!(list["result"]["tools"].as_array().unwrap().len(), 8);
        let call = rpc(
            "tools/call",
            json!({ "name": "schedule_task", "arguments": { "name": "Daily", "prompt": "p", "rrule": "FREQ=DAILY" } }),
        );
        assert_eq!(
            answer(&call, Some(&tasks), &mut never).unwrap()["result"]["isError"],
            false
        );
        assert!(dir.join("daily").join(tasks::FILE).exists());
        assert_eq!(answer(&call, None, &mut never).unwrap()["result"]["isError"], true);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn refuses_what_reframe_could_not_open() {
        let mut never = |_: &[u8]| panic!("nothing is posted");
        for arguments in [
            json!({ "prompt": "x", "provider": "openai" }),
            json!({ "name": "a", "prompt": " ", "provider": "openai" }),
            json!({ "name": "a", "prompt": "x" }),
            json!({ "name": "a", "prompt": "x", "provider": "mistral" }),
            json!({ "name": "a", "prompt": "x", "provider": "claude", "folder": "relative" }),
        ] {
            let call = rpc("tools/call", json!({ "name": "open_thread", "arguments": arguments }));
            assert_eq!(answer(&call, None, &mut never).unwrap()["result"]["isError"], true);
        }
    }
}
