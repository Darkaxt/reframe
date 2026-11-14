//! A session's process in its pseudoconsole, with the threads that move its output and input: what it writes goes
//! to the host as text (UTF-8, decoded across reads as node-pty does), and its exit is reported after its output.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::mpsc;
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use reframe_conpty::{Pty, Spawn};
use serde_json::Value;

use crate::protocol::{command_line, SpawnRequest};

/// What a session's threads tell the host; `serial` names the start (a session id can be started again).
pub enum ProcessEvent {
    Data { serial: u64, data: String },
    Exited { serial: u64, code: u32 },
}

/// A running process: its pseudoconsole and the queue to its input.
pub struct Process {
    pty: Arc<Pty>,
    input: mpsc::Sender<String>,
}

impl Process {
    pub fn pid(&self) -> u32 {
        self.pty.pid()
    }

    /// Its pseudoconsole, for what it uses (`monitor`).
    pub fn pty(&self) -> Arc<Pty> {
        self.pty.clone()
    }

    pub fn write(&self, data: String) {
        // The writer thread is gone only once the process is; then the input has nowhere to go.
        let _ = self.input.send(data);
    }

    pub fn resize(&self, cols: u16, rows: u16) {
        let _ = self.pty.resize(cols, rows);
    }

    /// Ends the process and its pseudoconsole; its exit is still reported.
    pub fn kill(&self) {
        self.pty.kill();
    }
}

/// How long the output may go on after the process ended before the pseudoconsole is closed to end it.
const OUTPUT_GRACE: Duration = Duration::from_secs(1);

/// Starts a session's process; `events` gets its output and exit. The error is what node-pty would say.
pub fn start(
    request: &SpawnRequest,
    serial: u64,
    events: impl Fn(ProcessEvent) + Send + Sync + 'static,
) -> Result<Process, String> {
    // As node-pty: a relative program is looked up in the host's own Path; it must be a file.
    let program = Path::new(&request.file);
    let found = if program.is_relative() {
        search_path(program)
    } else {
        Some(program.to_path_buf())
    };
    match found {
        Some(path) if path.is_file() => {}
        other => {
            return Err(format!(
                "Error: File not found: {}",
                other.map(|p| p.display().to_string()).unwrap_or_default()
            ))
        }
    }
    let env: Vec<String> = request
        .env
        .iter()
        .map(|(key, value)| match value {
            Value::String(text) => format!("{key}={text}"),
            other => format!("{key}={other}"),
        })
        .collect();
    let line = command_line(&request.file, &request.args);
    let (pty, mut output, mut input) = reframe_conpty::spawn(&Spawn {
        command_line: &line,
        cwd: &request.cwd,
        env: &env,
        cols: request.cols,
        rows: request.rows,
    })
    .map_err(|error| format!("Error: {error}"))?;
    let pty = Arc::new(pty);
    let events = Arc::new(events);
    let (eof_tx, eof_rx) = mpsc::channel::<()>();

    let reader_events = Arc::clone(&events);
    thread::Builder::new()
        .name(format!("pty-out-{serial}"))
        .spawn(move || {
            let mut text = Utf8Stream::default();
            let mut buffer = vec![0u8; 64 * 1024];
            loop {
                match output.read(&mut buffer) {
                    Ok(0) | Err(_) => break,
                    Ok(n) => {
                        let data = text.push(&buffer[..n]);
                        if !data.is_empty() {
                            reader_events(ProcessEvent::Data { serial, data });
                        }
                    }
                }
            }
            let rest = text.finish();
            if !rest.is_empty() {
                reader_events(ProcessEvent::Data { serial, data: rest });
            }
            let _ = eof_tx.send(());
        })
        .map_err(|e| format!("Error: {e}"))?;

    let (input_tx, input_rx) = mpsc::channel::<String>();
    thread::Builder::new()
        .name(format!("pty-in-{serial}"))
        .spawn(move || {
            for data in input_rx {
                if input.write_all(data.as_bytes()).is_err() {
                    break;
                }
            }
        })
        .map_err(|e| format!("Error: {e}"))?;

    let waiter = Arc::clone(&pty);
    thread::Builder::new()
        .name(format!("pty-wait-{serial}"))
        .spawn(move || {
            let code = waiter.wait().unwrap_or(1);
            // Windows' own ConPTY keeps the output open until it is closed; the bundled one ends it with the process.
            if eof_rx.recv_timeout(OUTPUT_GRACE).is_err() {
                waiter.close();
                let _ = eof_rx.recv_timeout(OUTPUT_GRACE);
            }
            events(ProcessEvent::Exited { serial, code });
        })
        .map_err(|e| format!("Error: {e}"))?;

    Ok(Process { pty, input: input_tx })
}

fn search_path(program: &Path) -> Option<PathBuf> {
    // A relative program found where the host runs is "not found", as in node-pty (its lookup returns nothing).
    if program.is_file() {
        return None;
    }
    let path = std::env::var_os("Path").or_else(|| std::env::var_os("PATH"))?;
    std::env::split_paths(&path)
        .map(|dir| dir.join(program))
        .find(|candidate| candidate.is_file())
}

/// Decodes UTF-8 that arrives in pieces: a character cut between two reads is kept for the next; bytes that are
/// not UTF-8 become U+FFFD.
#[derive(Default)]
pub struct Utf8Stream {
    pending: Vec<u8>,
}

impl Utf8Stream {
    pub fn push(&mut self, bytes: &[u8]) -> String {
        self.pending.extend_from_slice(bytes);
        let mut out = String::new();
        loop {
            match std::str::from_utf8(&self.pending) {
                Ok(text) => {
                    out.push_str(text);
                    self.pending.clear();
                    return out;
                }
                Err(error) => {
                    let valid = error.valid_up_to();
                    out.push_str(&String::from_utf8_lossy(&self.pending[..valid]));
                    match error.error_len() {
                        Some(bad) => {
                            out.push('\u{FFFD}');
                            self.pending.drain(..valid + bad);
                        }
                        None => {
                            self.pending.drain(..valid);
                            return out;
                        }
                    }
                }
            }
        }
    }

    /// What is left at the end (an unfinished character).
    pub fn finish(&mut self) -> String {
        let rest = String::from_utf8_lossy(&self.pending).into_owned();
        self.pending.clear();
        rest
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use proptest::prelude::*;

    #[test]
    fn characters_cut_between_reads() {
        let mut stream = Utf8Stream::default();
        let bytes = "aé日🙂".as_bytes();
        let mut out = String::new();
        for byte in bytes {
            out.push_str(&stream.push(std::slice::from_ref(byte)));
        }
        assert_eq!(out, "aé日🙂");
        assert_eq!(stream.push(b"\xff x"), "\u{FFFD} x");
        assert_eq!(stream.push(b"\xe6\x97"), "");
        assert_eq!(stream.finish(), "\u{FFFD}");
    }

    proptest! {
        #[test]
        fn any_cut_gives_the_text_back(text in ".{0,40}", cut in 0usize..200) {
            let bytes = text.as_bytes();
            let cut = cut.min(bytes.len());
            let mut stream = Utf8Stream::default();
            let mut out = stream.push(&bytes[..cut]);
            out.push_str(&stream.push(&bytes[cut..]));
            out.push_str(&stream.finish());
            prop_assert_eq!(out, text);
        }
    }
}
