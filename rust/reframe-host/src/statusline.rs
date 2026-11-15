//! The status line relay: `reframe-host --status-line-relay <url> <token> <hex command>`, the status line command of
//! an Anthropic session whose user has a status line of their own (reframe-services' spawn_plan). Claude Code gives
//! it the status line input (JSON on standard input, with `rate_limits`: the quota reframe shows); it posts that to
//! the host's hook server (as curl does without a status line of the user's), then runs the user's command (hex-coded
//! UTF-8, through cmd.exe) with the same input, and what that prints is the status line. Its exit code is the user
//! command's. The post never holds the status line up for long: two seconds at most, and a failed one is ignored.

use std::io::{Read, Write};
use std::net::TcpStream;
use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};
use std::time::Duration;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const POST_TIMEOUT: Duration = Duration::from_secs(2);
const MAX_INPUT: u64 = 4 * 1024 * 1024;

pub fn relay(args: &[String]) -> i32 {
    let [url, token, hex] = args else {
        return 2;
    };
    let mut input = Vec::new();
    let _ = std::io::stdin().take(MAX_INPUT).read_to_end(&mut input);
    let _ = post(url, token, &input);
    let Some(command) = decode_hex(hex) else {
        return 2;
    };
    run(&command, &input)
}

/// `http://127.0.0.1:<port><path>` only: the host's own hook server.
fn split_url(url: &str) -> Option<(u16, &str)> {
    let rest = url.strip_prefix("http://127.0.0.1:")?;
    let slash = rest.find('/')?;
    Some((rest[..slash].parse().ok()?, &rest[slash..]))
}

/// Whether the hook server answered 200 (also the thread tools' post, threads.rs).
pub(crate) fn post(url: &str, token: &str, body: &[u8]) -> std::io::Result<bool> {
    let (port, path) = split_url(url).ok_or(std::io::ErrorKind::InvalidInput)?;
    let address = std::net::SocketAddr::from(([127, 0, 0, 1], port));
    let mut stream = TcpStream::connect_timeout(&address, POST_TIMEOUT)?;
    stream.set_read_timeout(Some(POST_TIMEOUT))?;
    stream.set_write_timeout(Some(POST_TIMEOUT))?;
    let head = format!(
        "POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nX-Reframe-Token: {token}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        body.len()
    );
    stream.write_all(head.as_bytes())?;
    stream.write_all(body)?;
    // The answer (200) only says it arrived.
    let mut answer = [0u8; 256];
    let n = stream.read(&mut answer).unwrap_or(0);
    Ok(answer[..n].starts_with(b"HTTP/1.1 200"))
}

fn decode_hex(hex: &str) -> Option<String> {
    if hex.len() % 2 != 0 {
        return None;
    }
    let bytes = (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(hex.get(i..i + 2)?, 16).ok())
        .collect::<Option<Vec<u8>>>()?;
    String::from_utf8(bytes).ok()
}

/// The user's command through cmd.exe, as typed (no quoting added), with the input and the relay's own output.
fn run(command: &str, input: &[u8]) -> i32 {
    let child = Command::new("cmd.exe")
        .raw_arg(format!("/d /s /c \"{command}\""))
        .stdin(Stdio::piped())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn();
    let Ok(mut child) = child else {
        return 1;
    };
    if let Some(mut stdin) = child.stdin.take() {
        let _ = stdin.write_all(input);
    }
    child.wait().ok().and_then(|s| s.code()).unwrap_or(1)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    #[test]
    fn decodes_the_command() {
        let hex: String = "\"C:\\a b\\x.exe\" go".bytes().map(|b| format!("{b:02x}")).collect();
        assert_eq!(decode_hex(&hex).as_deref(), Some("\"C:\\a b\\x.exe\" go"));
        assert_eq!(decode_hex("4"), None);
        assert_eq!(decode_hex("zz"), None);
    }

    #[test]
    fn takes_only_the_local_hook_server() {
        assert_eq!(
            split_url("http://127.0.0.1:4100/hook/a/StatusLine"),
            Some((4100, "/hook/a/StatusLine"))
        );
        assert_eq!(split_url("http://example.com:80/hook"), None);
    }

    #[test]
    fn posts_the_input_with_the_token() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut seen = Vec::new();
            let mut chunk = [0u8; 1024];
            while !String::from_utf8_lossy(&seen).ends_with("{\"a\":1}") {
                let n = stream.read(&mut chunk).unwrap();
                if n == 0 {
                    break;
                }
                seen.extend_from_slice(&chunk[..n]);
            }
            stream
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")
                .unwrap();
            String::from_utf8(seen).unwrap()
        });
        post(
            &format!("http://127.0.0.1:{port}/hook/s/StatusLine"),
            "tok",
            b"{\"a\":1}",
        )
        .unwrap();
        let request = server.join().unwrap();
        assert!(request.starts_with("POST /hook/s/StatusLine HTTP/1.1\r\n"));
        assert!(request.contains("X-Reframe-Token: tok\r\n"));
        assert!(request.contains("Content-Length: 7\r\n"));
    }

    #[test]
    fn runs_the_users_command_with_the_input() {
        let code = run("findstr /c:\"rate\" >NUL", b"{\"rate_limits\":{}}\r\n");
        assert_eq!(code, 0);
        assert_eq!(run("exit 3", b""), 3);
    }
}
