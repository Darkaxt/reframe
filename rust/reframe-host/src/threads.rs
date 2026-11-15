//! The thread tools: `reframe-host --thread-tools <url> <token>`, an MCP server (JSON-RPC over standard input and
//! output, one message per line) that the sessions reframe starts get through `--mcp-config` (reframe-services'
//! spawn_plan). Its one tool, `open_thread`, asks reframe to open another thread (a session of its own, in a tile the
//! user sees and can take over), on OpenAI or Claude, with a first prompt: it posts the request to the host's hook
//! server as the session's `OpenThread` event, and the attached reframe creates the thread as its New session dialog
//! would. The two threads then talk with SendMessage (tagged with each one's provider and model, msgtag.rs).

use std::io::{BufRead, Write};

use serde_json::{json, Value};

use crate::statusline;

/// Characters: reframe takes a first prompt of up to 20000 (NewSession.MaxPrompt), with the line saying who opened it.
const MAX_PROMPT: usize = 19_000;

pub fn run(args: &[String]) -> i32 {
    let [url, token] = args else {
        return 2;
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
        if let Some(answer) = answer(&message, &mut |body| {
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
fn answer(message: &Value, post: &mut dyn FnMut(&[u8]) -> bool) -> Option<Value> {
    let id = message.get("id")?.clone();
    let method = message.get("method").and_then(Value::as_str).unwrap_or("");
    let result = match method {
        "initialize" => json!({
            "protocolVersion": message["params"]["protocolVersion"].as_str().unwrap_or("2025-06-18"),
            "capabilities": { "tools": {} },
            "serverInfo": { "name": "reframe", "version": env!("CARGO_PKG_VERSION") },
        }),
        "ping" => json!({}),
        "tools/list" => json!({ "tools": [tool()] }),
        "tools/call" if message["params"]["name"] == "open_thread" => call(&message["params"]["arguments"], post),
        "tools/call" => text_result(true, "Unknown tool."),
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
            sees and can take over, running on the provider you choose, OpenAI or Claude. It starts with your prompt, told which \
            thread opened it. Once it runs, talk to it with SendMessage to its name (messages between threads carry \
            each side's provider and model). Use it to hand work to an OpenAI model, or to get a second opinion.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": { "type": "string", "description": "The new thread's name, which SendMessage reaches it by." },
                "prompt": { "type": "string", "description": "Its first prompt: the task, with what it needs to know." },
                "provider": { "type": "string", "enum": ["openai", "claude"], "description": "Which models the new thread runs on: openai or claude. Always chosen, never assumed." },
                "folder": { "type": "string", "description": "An absolute folder to work in; this thread's folder by default." },
                "model": { "type": "string", "description": "A model; the user's default for the provider if left out." },
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
        _ => return text_result(true, "Choose the provider: openai or claude."),
    };
    let folder = text("folder");
    if folder.is_some_and(|f| !std::path::Path::new(f).is_absolute()) {
        return text_result(true, "folder must be an absolute path.");
    }
    let request =
        json!({ "name": name, "prompt": prompt, "provider": provider, "folder": folder, "model": text("model") });
    if !post(request.to_string().as_bytes()) {
        return text_result(true, "Reframe's host did not take the request (is Reframe running?).");
    }
    let family = if provider == "claude" { "Claude" } else { "OpenAI" };
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
            &mut never,
        )
        .unwrap();
        assert_eq!(init["result"]["protocolVersion"], "2025-03-26");
        assert_eq!(init["id"], 7);
        let list = answer(&rpc("tools/list", json!({})), &mut never).unwrap();
        assert_eq!(list["result"]["tools"][0]["name"], "open_thread");
        assert_eq!(
            answer(&json!({ "method": "notifications/initialized" }), &mut never),
            None
        );
        assert_eq!(
            answer(&rpc("resources/list", json!({})), &mut never).unwrap()["error"]["code"],
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
        let result = answer(&call, &mut |body| {
            posted.push(serde_json::from_slice::<Value>(body).unwrap());
            true
        })
        .unwrap();
        assert_eq!(result["result"]["isError"], false);
        assert_eq!(
            posted,
            [json!({ "name": "Docs", "prompt": "Review it", "provider": "openai", "folder": "D:\\P", "model": null })]
        );
        let refused = answer(&call, &mut |_| false).unwrap();
        assert_eq!(refused["result"]["isError"], true);
    }

    #[test]
    fn refuses_what_reframe_could_not_open() {
        let mut never = |_: &[u8]| panic!("nothing is posted");
        for arguments in [
            json!({ "prompt": "x", "provider": "openai" }),
            json!({ "name": "a", "prompt": " ", "provider": "openai" }),
            json!({ "name": "a", "prompt": "x" }),
            json!({ "name": "a", "prompt": "x", "provider": "gemini" }),
            json!({ "name": "a", "prompt": "x", "provider": "claude", "folder": "relative" }),
        ] {
            let call = rpc("tools/call", json!({ "name": "open_thread", "arguments": arguments }));
            assert_eq!(answer(&call, &mut never).unwrap()["result"]["isError"], true);
        }
    }
}
