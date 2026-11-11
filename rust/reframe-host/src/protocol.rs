//! The named-pipe protocol, as `src/ptyhost/protocol.ts` defines it (see docs/ptyhost-protocol.md): one JSON message
//! per line, a `hello` with the profile's token and the protocol version first.

use serde::Deserialize;
use serde_json::{Map, Value};
use sha2::{Digest, Sha256};

/// Must equal PROTOCOL_VERSION in protocol.ts: a reframe talks only to hosts with its own version.
pub const PROTOCOL_VERSION: u32 = 1;

/// What this host reports itself as, in the welcome and its descriptor (the Node host says "node").
pub const HOST_KIND: &str = "rust";

/// A request of the attached reframe (anything after its hello).
#[derive(Debug, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "kebab-case", rename_all_fields = "camelCase")]
pub enum Request {
    Spawn(Box<SpawnRequest>),
    Write {
        session_id: String,
        data: String,
    },
    Dispose {
        session_id: String,
        instance: u64,
    },
    ViewOpen {
        view_id: u64,
        session_id: String,
    },
    ViewMessage {
        view_id: u64,
        message: ViewMessage,
    },
    ViewClose {
        view_id: u64,
    },
    Shutdown,
    /// A request this host does not know: ignored, as the Node host ignores it.
    #[serde(other)]
    Unknown,
}

#[derive(Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SpawnRequest {
    pub session_id: String,
    pub instance: u64,
    pub file: String,
    pub args: Args,
    #[serde(default)]
    pub cwd: String,
    #[serde(default)]
    pub env: Map<String, Value>,
    pub cols: u16,
    pub rows: u16,
    #[serde(default)]
    pub meta: Option<Value>,
}

/// A list of arguments, or (Windows, for cmd.exe, which parses its own) the rest of the command line as it is.
#[derive(Debug, Deserialize, PartialEq)]
#[serde(untagged)]
pub enum Args {
    List(Vec<String>),
    Line(String),
}

/// What a terminal view sends: keyboard input, or its new size.
#[derive(Debug, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum ViewMessage {
    Input {
        data: String,
    },
    Resize {
        cols: f64,
        rows: f64,
    },
    #[serde(other)]
    Unknown,
}

/// The answer to a connection's first message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HelloCheck {
    Ok,
    /// Not a hello, or not the profile's token.
    Token,
    /// The right token, another protocol version.
    Protocol,
}

impl HelloCheck {
    pub fn reason(self) -> &'static str {
        match self {
            HelloCheck::Ok => "ok",
            HelloCheck::Token => "token",
            HelloCheck::Protocol => "protocol",
        }
    }
}

/// As checkHello in protocol.ts: welcome only with the profile's token and the same protocol version.
pub fn check_hello(hello: &Value, token: &str) -> HelloCheck {
    let given = match (
        hello.get("type").and_then(Value::as_str),
        hello.get("token").and_then(Value::as_str),
    ) {
        (Some("hello"), Some(given)) => given,
        _ => return HelloCheck::Token,
    };
    if !same_token(given, token) {
        return HelloCheck::Token;
    }
    // JavaScript compares numbers: 1 and 1.0 are the same version.
    match hello.get("protocol").and_then(Value::as_f64) {
        Some(p) if p == f64::from(PROTOCOL_VERSION) => HelloCheck::Ok,
        _ => HelloCheck::Protocol,
    }
}

/// Compares two tokens in time that depends only on their length.
pub fn same_token(a: &str, b: &str) -> bool {
    let (a, b) = (a.as_bytes(), b.as_bytes());
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

/// As pipeName in protocol.ts: one pipe per profile and host process.
pub fn pipe_name(profile_dir: &str, pid: u32) -> String {
    let digest = Sha256::digest(profile_dir.to_lowercase().as_bytes());
    let hex: String = digest.iter().map(|b| format!("{b:02x}")).collect();
    format!(r"\\.\pipe\reframe-ptyhost-{}-{pid}", &hex[..16])
}

/// Splits a byte stream into JSON lines; a line that is not JSON is dropped (as LineDecoder in protocol.ts does).
#[derive(Default)]
pub struct LineDecoder {
    buffer: Vec<u8>,
}

impl LineDecoder {
    pub fn push(&mut self, chunk: &[u8]) -> Vec<Value> {
        self.buffer.extend_from_slice(chunk);
        let mut out = Vec::new();
        let mut start = 0;
        while let Some(end) = self.buffer[start..].iter().position(|&b| b == b'\n') {
            let line = &self.buffer[start..start + end];
            start += end + 1;
            if line.is_empty() {
                continue;
            }
            if let Ok(value) = serde_json::from_slice::<Value>(line) {
                out.push(value);
            }
        }
        self.buffer.drain(..start);
        out
    }

    /// Bytes of an unfinished line.
    #[cfg(test)]
    pub fn pending(&self) -> usize {
        self.buffer.len()
    }
}

/// One message as a line.
pub fn encode(message: &Value) -> String {
    let mut line = message.to_string();
    line.push('\n');
    line
}

/// The command line node-pty builds (argsToCommandLine in its windowsPtyAgent): a string of arguments is appended
/// as it is; a list is quoted the way node-pty quotes it.
pub fn command_line(file: &str, args: &Args) -> String {
    match args {
        Args::Line(line) if line.is_empty() => quote_args(&[file]),
        Args::Line(line) => format!("{} {line}", quote_args(&[file])),
        Args::List(list) => {
            let mut argv: Vec<&str> = vec![file];
            argv.extend(list.iter().map(String::as_str));
            quote_args(&argv)
        }
    }
}

fn quote_args(argv: &[&str]) -> String {
    let mut result = String::new();
    for (index, arg) in argv.iter().enumerate() {
        if index > 0 {
            result.push(' ');
        }
        let chars: Vec<char> = arg.chars().collect();
        let first_quote = chars.first() == Some(&'"');
        let last_quote = chars.last() == Some(&'"');
        let lopsided = first_quote != last_quote;
        let unquoted = !first_quote && !last_quote;
        let quote =
            arg.is_empty() || ((arg.contains(' ') || arg.contains('\t')) && chars.len() > 1 && (lopsided || unquoted));
        if quote {
            result.push('"');
        }
        let mut backslashes = 0usize;
        for &c in &chars {
            match c {
                '\\' => backslashes += 1,
                '"' => {
                    result.push_str(&"\\".repeat(backslashes * 2 + 1));
                    result.push('"');
                    backslashes = 0;
                }
                _ => {
                    result.push_str(&"\\".repeat(backslashes));
                    backslashes = 0;
                    result.push(c);
                }
            }
        }
        if quote {
            result.push_str(&"\\".repeat(backslashes * 2));
            result.push('"');
        } else {
            result.push_str(&"\\".repeat(backslashes));
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use proptest::prelude::*;
    use serde_json::json;

    #[test]
    fn hello_needs_the_token_and_the_version() {
        let token = "a".repeat(64);
        assert_eq!(
            check_hello(&json!({"type":"hello","token":token,"protocol":1}), &token),
            HelloCheck::Ok
        );
        assert_eq!(
            check_hello(&json!({"type":"hello","token":token,"protocol":1.0}), &token),
            HelloCheck::Ok
        );
        assert_eq!(
            check_hello(&json!({"type":"hello","token":"b","protocol":1}), &token),
            HelloCheck::Token
        );
        assert_eq!(
            check_hello(&json!({"type":"hello","token":token,"protocol":2}), &token),
            HelloCheck::Protocol
        );
        assert_eq!(
            check_hello(&json!({"type":"hello","token":token}), &token),
            HelloCheck::Protocol
        );
        assert_eq!(
            check_hello(&json!({"type":"spawn","token":token,"protocol":1}), &token),
            HelloCheck::Token
        );
        assert_eq!(check_hello(&json!(null), &token), HelloCheck::Token);
        assert_eq!(
            check_hello(&json!({"type":"hello","token":7,"protocol":1}), &token),
            HelloCheck::Token
        );
    }

    #[test]
    fn pipe_name_matches_the_node_host() {
        // protocol.ts: sha256 of the lower-cased folder, first 16 hex digits, then the pid.
        let name = pipe_name(r"C:\Users\Me\AppData\Roaming\reframe\ptyhost", 42);
        let digest = Sha256::digest(r"c:\users\me\appdata\roaming\reframe\ptyhost".as_bytes());
        let hex: String = digest.iter().map(|b| format!("{b:02x}")).collect();
        assert_eq!(name, format!(r"\\.\pipe\reframe-ptyhost-{}-42", &hex[..16]));
    }

    #[test]
    fn decoder_splits_lines_and_drops_what_is_not_json() {
        let mut decoder = LineDecoder::default();
        assert!(decoder.push(br#"{"a":1}"#).is_empty());
        let out = decoder.push(b"\n\nnot json\n{\"b\":2}\n{\"c\"");
        assert_eq!(out, vec![json!({"a":1}), json!({"b":2})]);
        assert_eq!(decoder.pending(), 4);
        assert_eq!(decoder.push(b":3}\n"), vec![json!({"c":3})]);
        assert_eq!(decoder.pending(), 0);
    }

    #[test]
    fn requests_parse_as_the_client_sends_them() {
        let spawn: Request = serde_json::from_value(json!({
            "type":"spawn","sessionId":"s","instance":3,"file":"cmd.exe","args":"/c echo hi","cwd":"C:\\",
            "env":{"B":"2","A":"1"},"cols":80,"rows":24,"meta":{"launched":{"claudeSessionId":"x"}}
        }))
        .unwrap();
        let Request::Spawn(spawn) = spawn else {
            panic!("not a spawn")
        };
        assert_eq!(spawn.args, Args::Line("/c echo hi".into()));
        assert_eq!(
            spawn.env.keys().collect::<Vec<_>>(),
            vec!["B", "A"],
            "the order is kept"
        );
        let message: Request = serde_json::from_value(
            json!({"type":"view-message","viewId":2,"message":{"type":"resize","cols":100,"rows":30}}),
        )
        .unwrap();
        assert_eq!(
            message,
            Request::ViewMessage {
                view_id: 2,
                message: ViewMessage::Resize {
                    cols: 100.0,
                    rows: 30.0
                }
            }
        );
        assert_eq!(
            serde_json::from_value::<Request>(json!({"type":"shutdown"})).unwrap(),
            Request::Shutdown
        );
        assert_eq!(
            serde_json::from_value::<Request>(json!({"type":"what"})).unwrap(),
            Request::Unknown
        );
        assert_eq!(
            serde_json::from_value::<Request>(json!({"type":"dispose","sessionId":"s","instance":4})).unwrap(),
            Request::Dispose {
                session_id: "s".into(),
                instance: 4
            }
        );
    }

    #[test]
    fn command_lines_are_quoted_as_node_pty_quotes_them() {
        let list = |a: &[&str]| Args::List(a.iter().map(|s| s.to_string()).collect());
        assert_eq!(
            command_line("cmd.exe", &Args::Line("/d /s /c \"x y\"".into())),
            "cmd.exe /d /s /c \"x y\""
        );
        assert_eq!(
            command_line("C:\\a b\\c.exe", &Args::Line(String::new())),
            "\"C:\\a b\\c.exe\""
        );
        assert_eq!(
            command_line("c.exe", &list(&["a b", "", "x\"y", "\"q r\"", "tail\\"])),
            "c.exe \"a b\" \"\" x\\\"y \\\"q r\\\" tail\\"
        );
        assert_eq!(command_line("c.exe", &list(&["a b\\"])), "c.exe \"a b\\\\\"");
    }

    proptest! {
        #[test]
        fn decoder_finds_every_message_however_the_stream_is_cut(
            texts in proptest::collection::vec(".*", 0..8),
            cuts in proptest::collection::vec(0usize..400, 0..6),
        ) {
            let messages: Vec<Value> = texts.iter().enumerate().map(|(i, t)| json!({"type":"write","n":i,"data":t})).collect();
            let stream: Vec<u8> = messages.iter().flat_map(|m| encode(m).into_bytes()).collect();
            let mut points: Vec<usize> = cuts.into_iter().map(|c| c.min(stream.len())).collect();
            points.sort_unstable();
            let mut decoder = LineDecoder::default();
            let mut got = Vec::new();
            let mut at = 0;
            for p in points.into_iter().chain(std::iter::once(stream.len())) {
                got.extend(decoder.push(&stream[at..p]));
                at = p;
            }
            prop_assert_eq!(got, messages);
            prop_assert_eq!(decoder.pending(), 0);
        }

        #[test]
        fn decoder_never_panics_on_garbage(bytes in proptest::collection::vec(any::<u8>(), 0..512)) {
            let mut decoder = LineDecoder::default();
            let _ = decoder.push(&bytes);
        }

        #[test]
        fn requests_never_panic_on_any_json(text in "\\{.{0,64}\\}") {
            if let Ok(value) = serde_json::from_str::<Value>(&text) {
                let _ = serde_json::from_value::<Request>(value);
            }
        }
    }
}
