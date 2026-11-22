//! The local HTTP endpoint for Claude Code's HTTP hooks and status line (src/main/hooks/server.ts):
//! `POST /hook/<reframe session id>/<event>` with the host's token in `X-Reframe-Token`.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use serde_json::{json, Map, Value};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc::UnboundedSender;

use crate::cron;
use crate::protocol::same_token;

/// Events with a hook (HOOK_EVENTS in settings.ts) and the status line's own.
/// SessionStart: agy's (relayed by `reframe-host --agy-hook`, agyhook.rs), naming its conversation.
pub const HOOK_EVENTS: [&str; 4] = ["UserPromptSubmit", "Stop", "Notification", "SessionStart"];
pub const STATUS_LINE_EVENT: &str = "StatusLine";
/// A session's thread tools asking reframe to open another thread (threads.rs).
pub const OPEN_THREAD_EVENT: &str = "OpenThread";
/// Claude Code's PreToolUse hook for CronCreate, answered here (cron.rs).
pub const PRE_TOOL_USE_EVENT: &str = "PreToolUse";
/// A cron job blocked in a thread that is not its own, for reframe to deliver to its own (cron.rs): not a route.
pub const HAND_OFF_EVENT: &str = "HandOff";
const TOKEN_HEADER: &str = "x-reframe-token";
const MAX_BODY: usize = 1024 * 1024;
const MAX_HEAD: usize = 64 * 1024;

/// A hook of one of the host's sessions: `{ sessionId, event, payload }`.
#[derive(Debug, Clone, PartialEq)]
pub struct HookMessage {
    pub session_id: String,
    pub event: String,
    pub payload: Value,
}

impl HookMessage {
    pub fn to_json(&self) -> Value {
        json!({ "sessionId": self.session_id, "event": self.event, "payload": self.payload })
    }
}

/// Listens on 127.0.0.1 (a free port); hooks go to `sink`.
/// `attached`: whether a reframe is attached now. An `OpenThread` asked for while none is gets 503, so the
/// open_thread tool says it was not taken instead of that it was asked (the host does not keep it for later).
pub async fn start(
    token: String,
    sink: UnboundedSender<HookMessage>,
    attached: Arc<AtomicBool>,
) -> std::io::Result<u16> {
    let listener = TcpListener::bind(("127.0.0.1", 0)).await?;
    let port = listener.local_addr()?.port();
    tokio::spawn(async move {
        loop {
            let Ok((stream, _)) = listener.accept().await else {
                continue;
            };
            let (token, sink, attached) = (token.clone(), sink.clone(), attached.clone());
            tokio::spawn(async move {
                if let Some(message) = serve(stream, &token, &attached).await {
                    let _ = sink.send(message);
                }
            });
        }
    });
    Ok(port)
}

/// Answers one request; returns the hook it carried, if it was one.
async fn serve(mut stream: TcpStream, token: &str, attached: &AtomicBool) -> Option<HookMessage> {
    let mut buffer = Vec::new();
    let head_end = loop {
        if let Some(at) = find(&buffer, b"\r\n\r\n") {
            break at;
        }
        if buffer.len() > MAX_HEAD {
            return None;
        }
        let mut chunk = [0u8; 8192];
        let n = stream.read(&mut chunk).await.ok()?;
        if n == 0 {
            return None;
        }
        buffer.extend_from_slice(&chunk[..n]);
    };
    let head = String::from_utf8_lossy(&buffer[..head_end]).into_owned();
    let mut body = buffer.split_off(head_end + 4);
    let request = Head::parse(&head)?;
    let route = route(&request, token);
    let (session_id, event) = match route {
        Route::Hook { session_id, event } => (session_id, event),
        Route::Status(status) => {
            reply(&mut stream, status).await;
            return None;
        }
    };
    // The body: Content-Length, or chunked; too large ends the connection unanswered (as the Node host does).
    let body = if request.chunked {
        read_chunked(&mut stream, body).await?
    } else {
        let length = request.content_length.unwrap_or(0);
        if length > MAX_BODY {
            return None;
        }
        while body.len() < length {
            let mut chunk = [0u8; 8192];
            let n = stream.read(&mut chunk).await.ok()?;
            if n == 0 {
                break;
            }
            body.extend_from_slice(&chunk[..n]);
        }
        body.truncate(length);
        body
    };
    if event == OPEN_THREAD_EVENT && !attached.load(Ordering::SeqCst) {
        reply(&mut stream, 503).await;
        return None;
    }
    // An empty or broken body still tells that the event happened.
    let payload = match serde_json::from_slice::<Value>(&body) {
        Ok(value @ (Value::Object(_) | Value::Array(_))) => value,
        _ => Value::Object(Map::new()),
    };
    // A cron job: tagged with its thread, or blocked in another thread and handed to its own (held for reframe
    // while none is attached, as hooks are).
    match cron::answer(&session_id, &event, &payload) {
        Some(cron::Answer::Reply(answer)) => {
            reply_with(&mut stream, 200, &answer.to_string()).await;
            None
        }
        Some(cron::Answer::HandOff { reply, owner, prompt }) => {
            reply_with(&mut stream, 200, &reply.to_string()).await;
            Some(HookMessage {
                session_id,
                event: HAND_OFF_EVENT.into(),
                payload: json!({ "owner": owner, "prompt": prompt }),
            })
        }
        None => {
            reply(&mut stream, 200).await;
            Some(HookMessage {
                session_id,
                event,
                payload,
            })
        }
    }
}

async fn read_chunked(stream: &mut TcpStream, mut raw: Vec<u8>) -> Option<Vec<u8>> {
    let mut body = Vec::new();
    loop {
        // Enough of the stream to hold the next chunk's size line.
        let line_end = loop {
            if let Some(at) = find(&raw, b"\r\n") {
                break at;
            }
            if raw.len() > MAX_HEAD {
                return None;
            }
            read_more(stream, &mut raw).await?;
        };
        let size_text = String::from_utf8_lossy(&raw[..line_end]);
        let size = usize::from_str_radix(size_text.split(';').next()?.trim(), 16).ok()?;
        raw.drain(..line_end + 2);
        if size == 0 {
            return Some(body);
        }
        if body.len() + size > MAX_BODY {
            return None;
        }
        while raw.len() < size + 2 {
            read_more(stream, &mut raw).await?;
        }
        body.extend_from_slice(&raw[..size]);
        raw.drain(..size + 2);
    }
}

async fn read_more(stream: &mut TcpStream, raw: &mut Vec<u8>) -> Option<()> {
    let mut chunk = [0u8; 8192];
    let n = stream.read(&mut chunk).await.ok()?;
    if n == 0 {
        return None;
    }
    raw.extend_from_slice(&chunk[..n]);
    Some(())
}

async fn reply(stream: &mut TcpStream, status: u16) {
    reply_with(stream, status, "{}").await;
}

async fn reply_with(stream: &mut TcpStream, status: u16, body: &str) {
    let reason = match status {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        503 => "Service Unavailable",
        _ => "Not Found",
    };
    let length = body.len();
    let response = format!(
        "HTTP/1.1 {status} {reason}\r\ncontent-type: application/json\r\ncontent-length: {length}\r\nconnection: close\r\n\r\n{body}"
    );
    let _ = stream.write_all(response.as_bytes()).await;
    let _ = stream.shutdown().await;
}

fn find(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    haystack.windows(needle.len()).position(|w| w == needle)
}

/// The parts of a request head the endpoint looks at.
#[derive(Debug, Default, PartialEq)]
pub struct Head {
    pub method: String,
    pub target: String,
    pub token: Option<String>,
    pub content_length: Option<usize>,
    pub chunked: bool,
}

impl Head {
    pub fn parse(head: &str) -> Option<Self> {
        let mut lines = head.split("\r\n");
        let mut first = lines.next()?.split(' ');
        let mut parsed = Head {
            method: first.next()?.to_string(),
            target: first.next()?.to_string(),
            ..Head::default()
        };
        for line in lines {
            let Some((name, value)) = line.split_once(':') else {
                continue;
            };
            let value = value.trim();
            match name.trim().to_ascii_lowercase().as_str() {
                TOKEN_HEADER => parsed.token = Some(value.to_string()),
                "content-length" => parsed.content_length = Some(value.parse().ok()?),
                "transfer-encoding" => parsed.chunked = value.to_ascii_lowercase().contains("chunked"),
                _ => {}
            }
        }
        Some(parsed)
    }
}

#[derive(Debug, PartialEq)]
pub enum Route {
    Hook {
        session_id: String,
        event: String,
    },
    /// Answered with this status and nothing else.
    Status(u16),
}

/// What server.ts answers, in its order: 404 for anything but POST /hook/<id>/<letters>, 403 without the token,
/// 404 for an unknown event, 400 for an id that does not decode.
pub fn route(head: &Head, token: &str) -> Route {
    let Some(rest) = head.target.strip_prefix("/hook/") else {
        return Route::Status(404);
    };
    let Some((id, event)) = rest.split_once('/') else {
        return Route::Status(404);
    };
    if head.method != "POST" || id.is_empty() || event.is_empty() || !event.bytes().all(|b| b.is_ascii_alphabetic()) {
        return Route::Status(404);
    }
    if !head.token.as_deref().is_some_and(|given| same_token(given, token)) {
        return Route::Status(403);
    }
    if event != STATUS_LINE_EVENT
        && event != OPEN_THREAD_EVENT
        && event != PRE_TOOL_USE_EVENT
        && !HOOK_EVENTS.contains(&event)
    {
        return Route::Status(404);
    }
    match decode_uri_component(id) {
        Some(session_id) => Route::Hook {
            session_id,
            event: event.to_string(),
        },
        None => Route::Status(400),
    }
}

/// JavaScript's decodeURIComponent: %XX escapes to UTF-8; None where it would throw.
pub fn decode_uri_component(text: &str) -> Option<String> {
    let bytes = text.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' {
            let hex = text.get(i + 1..i + 3)?;
            out.push(u8::from_str_radix(hex, 16).ok()?);
            i += 3;
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    String::from_utf8(out).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn head(method: &str, target: &str, token: Option<&str>) -> Head {
        Head {
            method: method.into(),
            target: target.into(),
            token: token.map(Into::into),
            ..Head::default()
        }
    }

    #[test]
    fn routes_as_the_node_endpoint() {
        let t = "secret";
        assert_eq!(
            route(&head("POST", "/hook/a%20b/Stop", Some(t)), t),
            Route::Hook {
                session_id: "a b".into(),
                event: "Stop".into()
            }
        );
        assert_eq!(
            route(&head("POST", "/hook/s/StatusLine", Some(t)), t),
            Route::Hook {
                session_id: "s".into(),
                event: "StatusLine".into()
            }
        );
        assert_eq!(
            route(&head("POST", "/hook/s/OpenThread", Some(t)), t),
            Route::Hook {
                session_id: "s".into(),
                event: "OpenThread".into()
            }
        );
        assert_eq!(route(&head("GET", "/hook/s/Stop", Some(t)), t), Route::Status(404));
        assert_eq!(route(&head("POST", "/hook/s/Stop?x=1", Some(t)), t), Route::Status(404));
        assert_eq!(route(&head("POST", "/hook/s/x/Stop", Some(t)), t), Route::Status(404));
        assert_eq!(route(&head("POST", "/hook/s/Stop", None), t), Route::Status(403));
        assert_eq!(
            route(&head("POST", "/hook/s/Stop", Some("wrong")), t),
            Route::Status(403)
        );
        assert_eq!(route(&head("POST", "/hook/s/Other", Some(t)), t), Route::Status(404));
        assert_eq!(
            route(&head("POST", "/hook/%E0%A4%A/Stop", Some(t)), t),
            Route::Status(400)
        );
    }

    #[test]
    fn decodes_as_javascript_does() {
        assert_eq!(decode_uri_component("a%2Fb%C3%A9+c").as_deref(), Some("a/bé+c"));
        assert_eq!(decode_uri_component("%"), None);
        assert_eq!(decode_uri_component("%zz"), None);
        assert_eq!(decode_uri_component("%C3"), None);
    }

    #[test]
    fn parses_a_head() {
        let parsed = Head::parse(
            "POST /hook/s/Stop HTTP/1.1\r\nHost: x\r\nX-Reframe-Token: abc\r\nContent-Length: 12\r\nTransfer-Encoding: chunked",
        )
        .unwrap();
        assert_eq!(parsed.method, "POST");
        assert_eq!(parsed.target, "/hook/s/Stop");
        assert_eq!(parsed.token.as_deref(), Some("abc"));
        assert_eq!(parsed.content_length, Some(12));
        assert!(parsed.chunked);
    }

    #[tokio::test]
    async fn serves_hooks_over_http() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let attached = Arc::new(AtomicBool::new(false));
        let port = start("tok".into(), tx, attached.clone()).await.unwrap();
        let send = |request: String| async move {
            let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
            stream.write_all(request.as_bytes()).await.unwrap();
            let mut answer = String::new();
            stream.read_to_string(&mut answer).await.unwrap();
            answer
        };
        let body = r#"{"session_id":"c","hook_event_name":"Stop"}"#;
        let answer = send(format!(
            "POST /hook/s1/Stop HTTP/1.1\r\nX-Reframe-Token: tok\r\nContent-Length: {}\r\n\r\n{body}",
            body.len()
        ))
        .await;
        assert!(answer.starts_with("HTTP/1.1 200"), "{answer}");
        let message = rx.recv().await.unwrap();
        assert_eq!(
            message.to_json(),
            json!({"sessionId":"s1","event":"Stop","payload":{"session_id":"c","hook_event_name":"Stop"}})
        );
        let answer = send("POST /hook/s1/Notification HTTP/1.1\r\nX-Reframe-Token: tok\r\nTransfer-Encoding: chunked\r\n\r\n4\r\nnull\r\n0\r\n\r\n".into()).await;
        assert!(answer.starts_with("HTTP/1.1 200"), "{answer}");
        assert_eq!(rx.recv().await.unwrap().payload, json!({}));
        let answer = send("POST /hook/s1/Stop HTTP/1.1\r\nContent-Length: 0\r\n\r\n".into()).await;
        assert!(answer.starts_with("HTTP/1.1 403"), "{answer}");
        // A thread asked for while no reframe is attached is refused (it would be dropped), and taken once one is.
        let open = r#"{"name":"n","prompt":"p","provider":"claude"}"#;
        let request = format!(
            "POST /hook/s1/OpenThread HTTP/1.1\r\nX-Reframe-Token: tok\r\nContent-Length: {}\r\n\r\n{open}",
            open.len()
        );
        let answer = send(request.clone()).await;
        assert!(answer.starts_with("HTTP/1.1 503"), "{answer}");
        attached.store(true, Ordering::SeqCst);
        let answer = send(request).await;
        assert!(answer.starts_with("HTTP/1.1 200"), "{answer}");
        assert_eq!(rx.recv().await.unwrap().event, OPEN_THREAD_EVENT);
        // A cron job is tagged with its thread; fired in another thread, it is blocked there and handed to its own.
        let post = |id: &str, event: &str, body: &str| {
            format!(
                "POST /hook/{id}/{event} HTTP/1.1\r\nX-Reframe-Token: tok\r\nContent-Length: {}\r\n\r\n{body}",
                body.len()
            )
        };
        let create = r#"{"tool_name":"CronCreate","tool_input":{"cron":"* * * * *","prompt":"Check"}}"#;
        let answer = send(post("s1", "PreToolUse", create)).await;
        assert!(answer.ends_with(r#"\nCheck"}}}"#), "{answer}");
        let fired = json!({ "prompt": format!("{}s1]\nCheck", cron::TAG_START) }).to_string();
        let answer = send(post("s2", "UserPromptSubmit", &fired)).await;
        assert!(answer.contains(r#""decision":"block""#), "{answer}");
        let handed = rx.recv().await.unwrap();
        assert_eq!(
            (handed.session_id.as_str(), handed.event.as_str()),
            ("s2", HAND_OFF_EVENT)
        );
        assert_eq!(handed.payload["owner"], json!("s1"));
        let answer = send(post("s1", "UserPromptSubmit", &fired)).await;
        assert!(answer.ends_with("\r\n\r\n{}"), "{answer}");
        assert_eq!(rx.recv().await.unwrap().event, "UserPromptSubmit");
    }
}
