//! reframe-host: reframe's PTY host in Rust, a drop-in for the Node host (src/ptyhost/index.ts). It owns every
//! claude process (in a ConPTY each) and a terminal mirror per session; windows are only views that get a snapshot,
//! then live output. It is a process of its own that outlives reframe, reached over a named pipe of the profile, and
//! takes the sessions' Claude Code hooks for whichever reframe is attached. With no reframe attached and no session
//! running it exits. The protocol is in docs/ptyhost-protocol.md.
//!
//! Started by reframe with REFRAME_PTYHOST_DIR (the profile's host folder: token, descriptors, log) and
//! REFRAME_PTYHOST_VERSION. A Windows (not console) program: it never shows or opens a console window.
#![cfg_attr(windows, windows_subsystem = "windows")]
#![forbid(unsafe_code)]

#[cfg(not(windows))]
compile_error!("reframe-host runs on Windows (ConPTY); elsewhere reframe uses its Node host");

mod files;
mod hooks;
mod host;
mod limit;
mod mirror;
mod process;
mod protocol;
mod services;
mod transcripts;

use std::path::PathBuf;
use std::time::Duration;

use serde_json::json;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use tokio::sync::mpsc::{self, UnboundedReceiver, UnboundedSender};

use files::Files;
use host::{Config, Flow, Host, Input, Outgoing};
use protocol::{pipe_name, LineDecoder};

fn main() {
    let Some(dir) = std::env::var_os("REFRAME_PTYHOST_DIR").map(PathBuf::from) else {
        std::process::exit(2);
    };
    let version = std::env::var("REFRAME_PTYHOST_VERSION").unwrap_or_else(|_| "unknown".into());
    let pid = std::process::id();
    let files = Files::new(&dir, pid);

    let panic_files = Files::new(&dir, pid);
    std::panic::set_hook(Box::new(move |info| {
        panic_files.log("panic", json!({ "error": info.to_string() }));
    }));

    let token = match files.token() {
        Ok(token) => token,
        Err(error) => {
            files.log("no token", json!({ "error": error.to_string() }));
            std::process::exit(1);
        }
    };
    let pipe = pipe_name(&dir.to_string_lossy(), pid);
    let runtime = match tokio::runtime::Builder::new_current_thread().enable_all().build() {
        Ok(runtime) => runtime,
        Err(error) => {
            files.log("no runtime", json!({ "error": error.to_string() }));
            std::process::exit(1);
        }
    };
    let code = runtime.block_on(run(
        files,
        Config {
            version,
            pipe,
            token,
            hooks_port: 0,
            hooks_token: files::random_hex(24),
            started_at: files::now_ms(),
        },
    ));
    std::process::exit(code);
}

async fn run(files: Files, mut config: Config) -> i32 {
    let (inputs, mut received) = mpsc::unbounded_channel::<Input>();

    let (hook_tx, mut hook_rx) = mpsc::unbounded_channel();
    config.hooks_port = match hooks::start(config.hooks_token.clone(), hook_tx).await {
        Ok(port) => port,
        Err(error) => {
            files.log("hook endpoint failed", json!({ "error": error.to_string() }));
            return 1;
        }
    };
    let forward = inputs.clone();
    tokio::spawn(async move {
        while let Some(message) = hook_rx.recv().await {
            let _ = forward.send(Input::Hook(message));
        }
    });

    let first = match ServerOptions::new().first_pipe_instance(true).create(&config.pipe) {
        Ok(server) => server,
        Err(error) => {
            files.log("pipe failed", json!({ "error": error.to_string() }));
            return 1;
        }
    };
    let (pipe, version, port) = (config.pipe.clone(), config.version.clone(), config.hooks_port);
    let mut host = Host::new(config, files, inputs.clone());
    host.write_descriptor();
    host.log(
        "host started",
        json!({ "version": version, "pipe": pipe, "hookPort": port, "conpty": if reframe_conpty::bundled() { "bundled" } else { "windows" } }),
    );
    tokio::spawn(listen(first, pipe, inputs.clone()));

    let ticks = inputs;
    tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(1));
        loop {
            interval.tick().await;
            if ticks.send(Input::Tick).is_err() {
                break;
            }
        }
    });

    while let Some(input) = received.recv().await {
        if host.handle(input) == Flow::Exit {
            break;
        }
    }
    host.remove_descriptor();
    host.log("host exits", json!({}));
    0
}

/// Accepts connections on the pipe; a new instance waits for the next one before a connection is handed on.
async fn listen(mut server: NamedPipeServer, pipe: String, inputs: UnboundedSender<Input>) {
    let mut next_conn = 0u64;
    loop {
        if server.connect().await.is_err() {
            match ServerOptions::new().create(&pipe) {
                Ok(fresh) => server = fresh,
                Err(_) => return,
            }
            continue;
        }
        let connected = server;
        server = match ServerOptions::new().create(&pipe) {
            Ok(fresh) => fresh,
            Err(_) => return,
        };
        next_conn += 1;
        let (out_tx, out_rx) = mpsc::unbounded_channel();
        if inputs
            .send(Input::Connected {
                conn: next_conn,
                out: out_tx,
            })
            .is_err()
        {
            return;
        }
        tokio::spawn(connection(next_conn, connected, out_rx, inputs.clone()));
    }
}

/// One connection: its lines go to the host, the host's messages to it, until either side ends it.
async fn connection(
    conn: u64,
    mut pipe: NamedPipeServer,
    mut outgoing: UnboundedReceiver<Outgoing>,
    inputs: UnboundedSender<Input>,
) {
    let mut decoder = LineDecoder::default();
    let mut buffer = vec![0u8; 64 * 1024];
    loop {
        tokio::select! {
            read = pipe.read(&mut buffer) => match read {
                Ok(0) | Err(_) => break,
                Ok(n) => {
                    for message in decoder.push(&buffer[..n]) {
                        let _ = inputs.send(Input::Line { conn, message });
                    }
                }
            },
            message = outgoing.recv() => match message {
                Some(Outgoing::Line(line)) => {
                    if pipe.write_all(line.as_bytes()).await.is_err() {
                        break;
                    }
                }
                Some(Outgoing::End(line)) => {
                    let _ = pipe.write_all(line.as_bytes()).await;
                    let _ = pipe.flush().await;
                    break;
                }
                None => break,
            },
        }
    }
    let _ = inputs.send(Input::Closed { conn });
}
