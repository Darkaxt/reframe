//! The host's state and what it does with each thing that happens: one task owns every session, view and
//! connection, so they change one at a time, as on the Node host's single thread (src/ptyhost/index.ts).

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde_json::{json, Value};
use tokio::sync::mpsc::UnboundedSender;

use crate::files::Files;
use crate::hooks::{HookMessage, STATUS_LINE_EVENT};
use crate::mirror::Mirror;
use crate::monitor::{self, Monitor};
use crate::process::{self, Process, ProcessEvent};
use crate::protocol::{
    check_hello, encode, HelloCheck, Request, SpawnRequest, ViewMessage, HOST_KIND, PROTOCOL_VERSION,
};
use crate::services::{self, Services};
use crate::transcripts::{self, SvcError, Transcripts, WatchEvent};
use reframe_sessions::registry::Entry;
use reframe_sessions::{
    filetime_ms, has_tool_shells, update_json, Lookup, Status, Tracker, Update, BACKGROUND_RECHECK_MS,
};
use reframe_transcripts::Changes;

/// The session status service (docs/ptyhost-protocol.md, "Services").
const SESSIONS: &str = "sessions";
/// Version 2 (0.10.1-claudegpt.28): `background` in a status (idle, with background work still running).
const SESSIONS_VERSION: u32 = 2;
/// Read-only clients beside the primary (docs/ptyhost-protocol.md, "Observers").
const OBSERVER: &str = "observer";
pub const OBSERVER_VERSION: u32 = 1;
/// What an observer may ask: reads only.
const OBSERVER_METHODS: &[&str] = &[
    "sessions.list",
    "monitor.sample",
    "observer.primary",
    "transcripts.list",
    "transcripts.info",
    "transcripts.times",
    "transcripts.title",
    "overview.summary",
    "overview.threads",
    "overview.breakdown",
    "overview.timeline",
    "overview.status",
];

fn now_ms() -> f64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0.0, |d| d.as_millis() as f64)
}

/// Whether claude (process `pid`) has tool shells running (reframe_sessions::has_tool_shells): one toolhelp
/// snapshot of the system's processes and the creation time of claude's children, a few milliseconds.
fn tool_shells(pid: u32) -> bool {
    let parent = reframe_conpty::process_start(pid)
        .and_then(|s| s.parse().ok())
        .map(filetime_ms);
    let children = reframe_conpty::child_processes(pid);
    has_tool_shells(
        children.iter().map(|c| (c.exe.as_str(), c.start.map(filetime_ms))),
        parent,
        now_ms(),
    )
}

/// How often sessions idle with background work are looked at again: REFRAME_PTYHOST_BACKGROUND_MS (for tests),
/// else BACKGROUND_RECHECK_MS.
fn background_recheck() -> Duration {
    let ms = std::env::var("REFRAME_PTYHOST_BACKGROUND_MS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(BACKGROUND_RECHECK_MS);
    Duration::from_millis(ms)
}

/// Without a reframe and a running session for this long, the host goes.
pub const IDLE_EXIT: Duration = Duration::from_secs(10);
/// Hook events kept while no reframe is attached (the next one gets them).
const MAX_HELD_HOOKS: usize = 200;
/// How long transcript changes are collected before they are told (a busy session writes all the time).
const CHANGES_BATCH: Duration = Duration::from_secs(1);

/// Something that happened, for the host to handle.
pub enum Input {
    Connected {
        conn: u64,
        out: UnboundedSender<Outgoing>,
    },
    Line {
        conn: u64,
        message: Value,
    },
    Closed {
        conn: u64,
    },
    Process(ProcessEvent),
    Hook(HookMessage),
    Tick,
    /// A service request's work finished (see transcripts.rs).
    SvcDone {
        conn: u64,
        id: Value,
        outcome: Result<Value, SvcError>,
    },
    /// What a folder watch saw.
    Watch(WatchEvent),
    /// Transcript changes collected for a moment are told.
    FlushChanges,
    /// What a read of a session's registry entry found (see `read_registries`).
    Registry {
        serial: u64,
        entry: Option<Entry>,
        found: Option<u32>,
    },
}

/// What a connection's task writes.
pub enum Outgoing {
    Line(String),
    /// Writes the line, then closes the connection.
    End(String),
}

/// Whether the host goes on.
#[derive(Debug, PartialEq, Eq)]
pub enum Flow {
    Continue,
    Exit,
}

enum Lifecycle {
    Running(Process),
    Exited { code: u32 },
}

struct Session {
    id: String,
    instance: u64,
    /// Which start of the session id this is (events of an earlier one are not this one's).
    serial: u64,
    pid: u32,
    lifecycle: Lifecycle,
    mirror: Mirror,
    /// Views attached (by connection and view id), in the order they came.
    views: Vec<ViewKey>,
    meta: Option<Value>,
    /// Claude Code's session id as its hooks last gave it (also for a session not followed by `sessions`).
    hook_claude_id: Option<String>,
    /// Its status, for the `sessions` service (none for a session reframe follows itself).
    tracker: Option<Tracker>,
    /// Its registry entry is being read; no second read starts meanwhile.
    reading: bool,
}

impl Session {
    fn running(&self) -> Option<&Process> {
        match &self.lifecycle {
            Lifecycle::Running(process) => Some(process),
            Lifecycle::Exited { .. } => None,
        }
    }

    fn exit_code(&self) -> Option<u32> {
        match self.lifecycle {
            Lifecycle::Running(_) => None,
            Lifecycle::Exited { code } => Some(code),
        }
    }
}

enum ViewState {
    /// Opened before its session's process started (one in WSL starts once its distribution answered).
    Waiting,
    /// Got its snapshot; gets live output.
    Live,
}

/// A view is its connection's: each numbers its own.
type ViewKey = (u64, u64);

struct View {
    session_id: String,
    state: ViewState,
}

enum ConnectionState {
    /// Its first message must be a hello.
    Greeting,
    Welcomed,
    /// Refused or replaced: what it still sends is ignored.
    Done,
}

struct Connection {
    out: UnboundedSender<Outgoing>,
    state: ConnectionState,
    /// Welcomed with `role: "observer"`: reads only, replaces nobody.
    observer: bool,
}

pub struct Config {
    pub version: String,
    pub pipe: String,
    pub token: String,
    pub hooks_port: u16,
    pub hooks_token: String,
    pub started_at: u64,
}

pub struct Host {
    config: Config,
    files: Files,
    /// Starts processes; their events come back through `inputs`.
    inputs: UnboundedSender<Input>,
    sessions: Vec<Session>,
    views: BTreeMap<ViewKey, View>,
    connections: HashMap<u64, Connection>,
    /// The connection of the attached reframe.
    client: Option<u64>,
    held: VecDeque<HookMessage>,
    idle_since: Instant,
    next_serial: u64,
    transcripts: Transcripts,
    services: Services,
    /// What the last `monitor.sample` read (its rates need the one before).
    monitor: Arc<Mutex<Monitor>>,
    /// Transcript changes not told yet, per Claude Code folder; told once a batch is over, dropped (not queued)
    /// while no reframe is attached: the next one lists afresh.
    changes: Vec<(String, Changes)>,
    flush_scheduled: bool,
    /// Bumped by every change of a session's status data (`sessions`): one counter for every session.
    status_revision: u64,
    /// When sessions idle with background work were last looked at, and how often that is done.
    background_checked: Instant,
    background_every: Duration,
}

impl Host {
    pub fn new(config: Config, files: Files, inputs: UnboundedSender<Input>) -> Self {
        Self {
            config,
            files,
            inputs,
            sessions: Vec::new(),
            views: BTreeMap::new(),
            connections: HashMap::new(),
            client: None,
            held: VecDeque::new(),
            idle_since: Instant::now(),
            next_serial: 0,
            transcripts: Transcripts::new(),
            services: Services::new(),
            monitor: Arc::default(),
            changes: Vec::new(),
            flush_scheduled: false,
            status_revision: 0,
            background_checked: Instant::now(),
            background_every: background_recheck(),
        }
    }

    pub fn log(&self, message: &str, details: Value) {
        self.files.log(message, details);
    }

    /// Handles one input. A panic in it is logged and reported, and the host goes on (ending it would end every
    /// session); a panic in a session's terminal ends that session (see `with_mirror`).
    pub fn handle(&mut self, input: Input) -> Flow {
        match catch_unwind(AssertUnwindSafe(|| self.dispatch(input))) {
            Ok(flow) => flow,
            Err(panic) => {
                let message = panic_text(&panic);
                self.log("uncaught exception", json!({ "error": message }));
                self.emit(json!({ "type": "error", "message": message }));
                Flow::Continue
            }
        }
    }

    fn dispatch(&mut self, input: Input) -> Flow {
        match input {
            Input::Connected { conn, out } => {
                self.connections.insert(
                    conn,
                    Connection {
                        out,
                        state: ConnectionState::Greeting,
                        observer: false,
                    },
                );
            }
            Input::Line { conn, message } => return self.line(conn, message),
            Input::Closed { conn } => self.closed(conn),
            Input::Process(ProcessEvent::Data { serial, data }) => self.output(serial, &data),
            Input::Process(ProcessEvent::Exited { serial, code }) => self.exited(serial, code),
            Input::Hook(message) => self.hook(message),
            Input::Tick => return self.tick(Instant::now()),
            Input::SvcDone { conn, id, outcome } => self.svc_answer(conn, id, outcome),
            Input::Watch(event) => self.watched(event),
            Input::FlushChanges => self.flush_changes(),
            Input::Registry { serial, entry, found } => self.registry_read(serial, entry, found),
        }
        Flow::Continue
    }

    pub fn write_descriptor(&self) {
        let pids: Vec<u32> = self
            .sessions
            .iter()
            .filter(|s| s.running().is_some())
            .map(|s| s.pid)
            .collect();
        if let Err(error) =
            self.files
                .write_descriptor(&self.config.pipe, &self.config.version, self.config.started_at, &pids)
        {
            self.log("descriptor not written", json!({ "error": error.to_string() }));
        }
    }

    pub fn remove_descriptor(&self) {
        self.files.remove_descriptor();
    }

    fn send(&self, message: Value) {
        if let Some(connection) = self.client.and_then(|c| self.connections.get(&c)) {
            let _ = connection.out.send(Outgoing::Line(encode(&message)));
        }
    }

    fn emit(&self, event: Value) {
        self.send(event);
    }

    fn send_to(&self, conn: u64, message: &Value) {
        if let Some(connection) = self.connections.get(&conn) {
            let _ = connection.out.send(Outgoing::Line(encode(message)));
        }
    }

    fn observers(&self) -> impl Iterator<Item = u64> + '_ {
        self.connections
            .iter()
            .filter(|(_, c)| c.observer && matches!(c.state, ConnectionState::Welcomed))
            .map(|(&id, _)| id)
    }

    /// To the primary and every observer.
    fn broadcast(&self, message: Value) {
        for conn in self.observers() {
            self.send_to(conn, &message);
        }
        self.send(message);
    }

    fn send_view(&self, (conn, view_id): ViewKey, message: Value) {
        self.send_to(conn, &json!({ "type": "view", "viewId": view_id, "message": message }));
    }

    // Connections.

    fn line(&mut self, conn: u64, message: Value) -> Flow {
        let Some(connection) = self.connections.get(&conn) else {
            return Flow::Continue;
        };
        match connection.state {
            ConnectionState::Done => Flow::Continue,
            ConnectionState::Welcomed if connection.observer => {
                match serde_json::from_value::<Request>(message) {
                    Ok(request) => self.observer_request(conn, request),
                    Err(error) => self.log("request not understood", json!({ "error": error.to_string() })),
                }
                Flow::Continue
            }
            ConnectionState::Welcomed if self.client == Some(conn) => {
                match serde_json::from_value::<Request>(message) {
                    Ok(request) => self.request(request),
                    Err(error) => {
                        self.log("request not understood", json!({ "error": error.to_string() }));
                        Flow::Continue
                    }
                }
            }
            ConnectionState::Welcomed => Flow::Continue,
            ConnectionState::Greeting => {
                self.hello(conn, &message);
                Flow::Continue
            }
        }
    }

    fn hello(&mut self, conn: u64, message: &Value) {
        let answer = check_hello(message, &self.config.token);
        if answer != HelloCheck::Ok {
            self.log("connection refused", json!({ "reason": answer.reason() }));
            if let Some(connection) = self.connections.get_mut(&conn) {
                connection.state = ConnectionState::Done;
                let refused = json!({ "type": "refused", "reason": answer.reason(), "protocol": PROTOCOL_VERSION });
                let _ = connection.out.send(Outgoing::End(encode(&refused)));
            }
            return;
        }
        let observer = message.get("role").and_then(Value::as_str) == Some("observer");
        if let Some(connection) = self.connections.get_mut(&conn) {
            connection.state = ConnectionState::Welcomed;
            connection.observer = observer;
        }
        if observer {
            // An observer replaces nobody and is told no hooks: it reads.
            self.log("observer attached", json!({ "sessions": self.sessions.len() }));
        } else {
            // One reframe per profile: a new connection is the reframe now (the old one quit or is about to).
            if let Some(old) = self.client.filter(|&c| c != conn) {
                if let Some(connection) = self.connections.get_mut(&old) {
                    connection.state = ConnectionState::Done;
                    let _ = connection
                        .out
                        .send(Outgoing::End(encode(&json!({ "type": "replaced" }))));
                }
                self.close_views_of(old);
            }
            self.client = Some(conn);
            self.log("reframe attached", json!({ "sessions": self.sessions.len() }));
        }
        let sessions: Vec<Value> = self
            .sessions
            .iter()
            .map(|s| {
                let mut entry =
                    json!({ "sessionId": s.id, "instance": s.instance, "pid": s.pid, "exitCode": s.exit_code() });
                if let Some(meta) = &s.meta {
                    entry["meta"] = meta.clone();
                }
                let meta_id = |path: &[&str]| {
                    let mut v = s.meta.as_ref()?;
                    for key in path {
                        v = v.get(key)?;
                    }
                    v.as_str().filter(|id| !id.is_empty()).map(str::to_string)
                };
                let claude_id = s
                    .tracker
                    .as_ref()
                    .and_then(|t| t.claude_session_id().map(str::to_string))
                    .or_else(|| s.hook_claude_id.clone())
                    .or_else(|| meta_id(&["launched", "claudeSessionId"]))
                    .or_else(|| meta_id(&["wsl", "claudeSessionId"]));
                if let Some(id) = claude_id {
                    entry["claudeSessionId"] = json!(id);
                }
                entry
            })
            .collect();
        let mut services = serde_json::Map::new();
        services.insert(transcripts::SERVICE.into(), json!(transcripts::VERSION));
        for (name, version) in services::SERVICES {
            services.insert(name.into(), json!(version));
        }
        services.insert(SESSIONS.into(), json!(SESSIONS_VERSION));
        services.insert(OBSERVER.into(), json!(OBSERVER_VERSION));
        services.insert(monitor::SERVICE.into(), json!(monitor::VERSION));
        self.send_to(
            conn,
            &json!({
                "type": "welcome",
                "protocol": PROTOCOL_VERSION,
                "version": self.config.version,
                "pid": std::process::id(),
                "hooks": { "port": self.config.hooks_port, "token": self.config.hooks_token },
                "sessions": sessions,
                "kind": HOST_KIND,
                "services": services,
                "role": if observer { "observer" } else { "primary" },
                "primary": self.client.is_some(),
            }),
        );
        if observer {
            return;
        }
        self.primary_changed();
        // What changed before is in what the new reframe lists now.
        self.changes.clear();
        for message in std::mem::take(&mut self.held) {
            self.send(json!({ "type": "hook", "message": message.to_json() }));
        }
    }

    fn closed(&mut self, conn: u64) {
        let observer = self.connections.remove(&conn).is_some_and(|c| c.observer);
        // Views belong to the connection that opened them.
        self.close_views_of(conn);
        if observer {
            self.log("observer detached", json!({ "sessions": self.sessions.len() }));
            return;
        }
        if self.client != Some(conn) {
            return;
        }
        self.client = None;
        self.idle_since = Instant::now();
        self.log("reframe detached", json!({ "sessions": self.sessions.len() }));
        self.primary_changed();
    }

    /// Tells the observers whether a primary is attached (after it changed).
    fn primary_changed(&self) {
        let event = json!({
            "type": "svc-event", "service": OBSERVER, "event": "primary", "data": { "primary": self.client.is_some() },
        });
        for conn in self.observers() {
            self.send_to(conn, &event);
        }
    }

    /// An observer's request: views (read-only) and the services it may ask; anything else is ignored.
    fn observer_request(&mut self, conn: u64, request: Request) {
        match request {
            Request::ViewOpen { view_id, session_id } => self.open_view((conn, view_id), session_id),
            Request::ViewClose { view_id } => self.close_view((conn, view_id)),
            Request::Svc {
                id,
                service,
                method,
                params,
            } => self.svc(conn, id, &service, &method, &params),
            _ => {}
        }
    }

    // Requests.

    fn request(&mut self, request: Request) -> Flow {
        match request {
            Request::Spawn(spawn) => self.spawn(*spawn),
            Request::Write { session_id, data } => {
                if let Some(process) = self.session(&session_id).and_then(Session::running) {
                    process.write(data);
                }
            }
            Request::Dispose { session_id, instance } => self.dispose(&session_id, Some(instance)),
            Request::ViewOpen { view_id, session_id } => {
                if let Some(conn) = self.client {
                    self.open_view((conn, view_id), session_id);
                }
            }
            Request::ViewMessage { view_id, message } => self.view_message(view_id, message),
            Request::ViewClose { view_id } => {
                if let Some(conn) = self.client {
                    self.close_view((conn, view_id));
                }
            }
            Request::Shutdown => {
                self.log("shutdown requested", json!({ "sessions": self.sessions.len() }));
                let ids: Vec<String> = self.sessions.iter().map(|s| s.id.clone()).collect();
                for id in ids {
                    self.dispose(&id, None);
                }
                return Flow::Exit;
            }
            Request::Svc {
                id,
                service,
                method,
                params,
            } => {
                if let Some(conn) = self.client {
                    self.svc(conn, id, &service, &method, &params);
                }
            }
            Request::Unknown => {}
        }
        Flow::Continue
    }

    // Services.

    fn svc(&mut self, conn: u64, id: Value, service: &str, method: &str, params: &Value) {
        let observer = self.client != Some(conn);
        if observer && !OBSERVER_METHODS.contains(&format!("{service}.{method}").as_str()) {
            let error = SvcError {
                code: "forbidden",
                message: format!("{service}.{method} is not for an observer"),
            };
            return self.svc_answer(conn, id, Err(error));
        }
        if service == OBSERVER {
            let outcome = match method {
                "primary" => Ok(json!({ "primary": self.client.is_some() })),
                _ => Err(SvcError {
                    code: "unknown-method",
                    message: format!("{service}.{method}"),
                }),
            };
            return self.svc_answer(conn, id, outcome);
        }
        if service == SESSIONS {
            let outcome = match method {
                "list" => Ok(json!({ "sessions": self.statuses(!observer), "revision": self.status_revision })),
                _ => Err(SvcError {
                    code: "unknown-method",
                    message: format!("{service}.{method}"),
                }),
            };
            return self.svc_answer(conn, id, outcome);
        }
        if service == monitor::SERVICE {
            if method != "sample" {
                let error = SvcError {
                    code: "unknown-method",
                    message: format!("{service}.{method}"),
                };
                return self.svc_answer(conn, id, Err(error));
            }
            let running = self
                .sessions
                .iter()
                .filter_map(|s| s.running().map(|p| (s.id.clone(), p.pty())))
                .collect();
            let inputs = self.inputs.clone();
            monitor::sample(&self.monitor, running, move |result| {
                let _ = inputs.send(Input::SvcDone {
                    conn,
                    id,
                    outcome: Ok(result),
                });
            });
            return;
        }
        if services::serves(service) {
            let inputs = self.inputs.clone();
            let answer_id = id.clone();
            let answer = move |outcome| {
                let _ = inputs.send(Input::SvcDone {
                    conn,
                    id: answer_id,
                    outcome,
                });
            };
            if let Err(error) = self.services.request(service, method, params, answer) {
                self.svc_answer(conn, id, Err(error));
            }
            return;
        }
        if service != transcripts::SERVICE {
            let error = SvcError {
                code: "unknown-method",
                message: format!("{service}.{method}"),
            };
            return self.svc_answer(conn, id, Err(error));
        }
        let inputs = self.inputs.clone();
        let answer_id = id.clone();
        let answer = move |outcome| {
            let _ = inputs.send(Input::SvcDone {
                conn,
                id: answer_id,
                outcome,
            });
        };
        match self.transcripts.request(method, params, &self.inputs, answer) {
            Ok(Some(result)) => self.svc_answer(conn, id, Ok(result)),
            Ok(None) => {}
            Err(error) => self.svc_answer(conn, id, Err(error)),
        }
    }

    /// An answer goes to the connection that asked, if it is still the attached one (or that observer).
    fn svc_answer(&self, conn: u64, id: Value, outcome: Result<Value, SvcError>) {
        let observer = self.observers().any(|c| c == conn);
        if self.client != Some(conn) && !observer {
            return;
        }
        self.send_to(
            conn,
            &match outcome {
                Ok(result) => json!({ "type": "svc-result", "id": id, "ok": true, "result": result }),
                Err(error) => json!({ "type": "svc-result", "id": id, "ok": false, "error": error.to_json() }),
            },
        );
    }

    fn watched(&mut self, event: WatchEvent) {
        let Some((home, changes)) = self.transcripts.watched(event, &self.inputs) else {
            return;
        };
        if self.client.is_none() {
            return;
        }
        match self.changes.iter_mut().find(|(h, _)| *h == home) {
            Some((_, pending)) => pending.add(changes),
            None => self.changes.push((home, changes)),
        }
        // Told a moment after the first change, however many follow.
        if !self.flush_scheduled {
            self.flush_scheduled = true;
            let inputs = self.inputs.clone();
            tokio::spawn(async move {
                tokio::time::sleep(CHANGES_BATCH).await;
                let _ = inputs.send(Input::FlushChanges);
            });
        }
    }

    fn flush_changes(&mut self) {
        self.flush_scheduled = false;
        for (home, changes) in std::mem::take(&mut self.changes) {
            self.send(transcripts::changed_event(&home, &changes));
        }
    }

    fn session(&self, id: &str) -> Option<&Session> {
        self.sessions.iter().find(|s| s.id == id)
    }

    fn spawn(&mut self, request: SpawnRequest) {
        let (session_id, instance) = (request.session_id.clone(), request.instance);
        self.dispose(&session_id, None);
        self.next_serial += 1;
        let serial = self.next_serial;
        let inputs = self.inputs.clone();
        let started = process::start(&request, serial, move |event| {
            let _ = inputs.send(Input::Process(event));
        });
        let process = match started {
            Ok(process) => process,
            Err(message) => {
                self.broadcast(json!({ "type": "spawn-failed", "sessionId": session_id, "instance": instance, "message": message }));
                return;
            }
        };
        let pid = process.pid();
        let mut tracker = Lookup::from_meta(request.meta.as_ref())
            .map(|lookup| Tracker::new(lookup, pid, now_ms(), reframe_conpty::process_start(pid)));
        if let Some(tracker) = tracker.as_mut() {
            self.status_revision += 1;
            tracker.revision = self.status_revision;
        }
        self.sessions.push(Session {
            id: session_id.clone(),
            instance,
            serial,
            pid,
            lifecycle: Lifecycle::Running(process),
            mirror: Mirror::new(request.cols, request.rows),
            views: Vec::new(),
            meta: request.meta,
            hook_claude_id: None,
            tracker,
            reading: false,
        });
        self.write_descriptor();
        self.broadcast(json!({ "type": "spawned", "sessionId": session_id, "instance": instance, "pid": pid }));
        let waiting: Vec<ViewKey> = self
            .views
            .iter()
            .filter(|(_, v)| v.session_id == session_id && matches!(v.state, ViewState::Waiting))
            .map(|(&id, _)| id)
            .collect();
        for view_id in waiting {
            self.attach(view_id);
        }
    }

    /// Runs `f` on a session's terminal; should it panic, the session is ended (its process killed, its exit
    /// reported as usual) and the host goes on.
    fn with_mirror<T>(&mut self, index: usize, f: impl FnOnce(&mut Mirror) -> T) -> Option<T> {
        let session = &mut self.sessions[index];
        match catch_unwind(AssertUnwindSafe(|| f(&mut session.mirror))) {
            Ok(value) => Some(value),
            Err(panic) => {
                let (cols, rows) = catch_unwind(AssertUnwindSafe(|| session.mirror.size())).unwrap_or((80, 24));
                session.mirror = Mirror::new(cols, rows);
                if let Some(process) = session.running() {
                    process.kill();
                }
                let (id, message) = (session.id.clone(), panic_text(&panic));
                self.log(
                    "session terminal failed; session ended",
                    json!({ "sessionId": id, "error": message }),
                );
                None
            }
        }
    }

    fn output(&mut self, serial: u64, data: &str) {
        let Some(index) = self.sessions.iter().position(|s| s.serial == serial) else {
            return;
        };
        if self.with_mirror(index, |mirror| mirror.write(data)).is_none() {
            return;
        }
        for &key in &self.sessions[index].views {
            if matches!(
                self.views.get(&key),
                Some(View {
                    state: ViewState::Live,
                    ..
                })
            ) {
                self.send_view(key, json!({ "type": "data", "data": data }));
            }
        }
    }

    fn exited(&mut self, serial: u64, code: u32) {
        let Some(session) = self.sessions.iter_mut().find(|s| s.serial == serial) else {
            return;
        };
        session.lifecycle = Lifecycle::Exited { code };
        let update = session.tracker.as_mut().and_then(Tracker::exited);
        let (id, instance, pid, views) = (session.id.clone(), session.instance, session.pid, session.views.clone());
        for key in views {
            self.send_view(key, json!({ "type": "exit", "exitCode": code }));
        }
        self.broadcast(json!({ "type": "exit", "sessionId": id, "instance": instance, "pid": pid, "exitCode": code }));
        if let Some(update) = update {
            self.status_changed(serial, update);
        }
        self.write_descriptor();
    }

    // Views.

    fn open_view(&mut self, key: ViewKey, session_id: String) {
        self.close_view(key);
        let exists = self.session(&session_id).is_some();
        self.views.insert(
            key,
            View {
                session_id,
                state: ViewState::Waiting,
            },
        );
        if exists {
            self.attach(key);
        }
    }

    /// Gives a view its session's snapshot; from then on it gets live output (and the exit, if that came).
    fn attach(&mut self, view_id: ViewKey) {
        let Some(session_id) = self.views.get(&view_id).map(|v| v.session_id.clone()) else {
            return;
        };
        let Some(index) = self.sessions.iter().position(|s| s.id == session_id) else {
            return;
        };
        self.sessions[index].views.push(view_id);
        let Some((data, (cols, rows))) = self.with_mirror(index, |mirror| (mirror.snapshot(), mirror.size())) else {
            return;
        };
        self.send_view(
            view_id,
            json!({ "type": "snapshot", "data": data, "cols": cols, "rows": rows }),
        );
        if let Some(view) = self.views.get_mut(&view_id) {
            view.state = ViewState::Live;
        }
        if let Some(code) = self.sessions[index].exit_code() {
            self.send_view(view_id, json!({ "type": "exit", "exitCode": code }));
        }
    }

    /// Input and resizes: only the primary's views (an observer's are read-only).
    fn view_message(&mut self, view_id: u64, message: ViewMessage) {
        let Some(conn) = self.client else {
            return;
        };
        let view_id = (conn, view_id);
        let Some(session_id) = self.views.get(&view_id).map(|v| v.session_id.clone()) else {
            return;
        };
        let Some(index) = self.sessions.iter().position(|s| s.id == session_id) else {
            return;
        };
        let session = &self.sessions[index];
        let Some(process) = session.running().filter(|_| session.views.contains(&view_id)) else {
            return;
        };
        match message {
            ViewMessage::Input { data } => process.write(data),
            ViewMessage::Resize { cols, rows } if cols > 1.0 && rows > 1.0 => {
                let (cols, rows) = (
                    cols.min(f64::from(u16::MAX)) as u16,
                    rows.min(f64::from(u16::MAX)) as u16,
                );
                process.resize(cols, rows);
                self.with_mirror(index, |mirror| mirror.resize(cols, rows));
            }
            ViewMessage::Resize { .. } | ViewMessage::Unknown => {}
        }
    }

    fn close_view(&mut self, view_id: ViewKey) {
        let Some(view) = self.views.remove(&view_id) else {
            return;
        };
        if let Some(session) = self.sessions.iter_mut().find(|s| s.id == view.session_id) {
            session.views.retain(|&v| v != view_id);
        }
    }

    fn close_views_of(&mut self, conn: u64) {
        let ids: Vec<ViewKey> = self.views.keys().copied().filter(|&(c, _)| c == conn).collect();
        for key in ids {
            self.close_view(key);
        }
    }

    /// Forgets a session; with an instance, only if it is still that one (a newer start must survive).
    fn dispose(&mut self, session_id: &str, instance: Option<u64>) {
        let Some(index) = self
            .sessions
            .iter()
            .position(|s| s.id == session_id && instance.is_none_or(|i| s.instance == i))
        else {
            return;
        };
        let session = self.sessions.remove(index);
        for &(conn, view_id) in &session.views {
            self.send_to(conn, &json!({ "type": "view-closed", "viewId": view_id }));
            self.views.remove(&(conn, view_id));
        }
        if let Some(process) = session.running() {
            process.kill();
        }
        self.write_descriptor();
    }

    // Hooks and time.

    fn hook(&mut self, message: HookMessage) {
        let claude_id = message
            .payload
            .get("session_id")
            .and_then(Value::as_str)
            .filter(|id| !id.is_empty());
        let tracked = self
            .sessions
            .iter_mut()
            .find(|s| s.id == message.session_id)
            .and_then(|s| {
                if let Some(id) = claude_id {
                    s.hook_claude_id = Some(id.to_string());
                }
                let tracker = s.tracker.as_mut()?;
                Some((
                    s.serial,
                    tracker.hook_with(&message.event, &message.payload, tool_shells)?,
                ))
            });
        if let Some((serial, update)) = tracked {
            self.status_changed(serial, update);
        }
        if self.client.is_some() {
            self.send(json!({ "type": "hook", "message": message.to_json() }));
        } else if message.event != STATUS_LINE_EVENT {
            self.held.push_back(message);
            if self.held.len() > MAX_HELD_HOOKS {
                self.held.pop_front();
            }
        }
    }

    // The `sessions` service: each session's status, from its hooks and its registry entry.

    /// A change of a session's status data: a new revision, told to the attached reframe and the observers; without
    /// a reframe, what it announced waits for the next.
    fn status_changed(&mut self, serial: u64, update: Update) {
        let attached = self.client.is_some();
        let Some(session) = self.sessions.iter_mut().find(|s| s.serial == serial) else {
            return;
        };
        self.status_revision += 1;
        let revision = self.status_revision;
        if let Some(tracker) = session.tracker.as_mut() {
            tracker.revision = revision;
            if let (false, Some(notify)) = (attached, update.notify) {
                tracker.held = Some(notify);
            }
        }
        let data = update_json(&session.id, session.instance, &update, revision);
        let busy = self
            .sessions
            .iter()
            .any(|s| s.tracker.as_ref().is_some_and(|t| t.status() == Status::Busy));
        self.services.set_busy(busy);
        self.broadcast(json!({ "type": "svc-event", "service": SESSIONS, "event": "status", "data": data }));
    }

    /// `take`: the primary asks, and gets what was announced while none was attached (an observer does not).
    fn statuses(&mut self, take: bool) -> Vec<Value> {
        self.sessions
            .iter_mut()
            .filter_map(|s| Some(s.tracker.as_mut()?.snapshot_with(&s.id, s.instance, take)))
            .collect()
    }

    /// Reads the registry entry of every running session followed here (once a second, as SessionManager polls),
    /// each off the host's task, one at a time per session.
    fn read_registries(&mut self) {
        for session in &mut self.sessions {
            if session.reading || session.running().is_none() {
                continue;
            }
            let Some(read) = session.tracker.as_ref().and_then(Tracker::read) else {
                continue;
            };
            session.reading = true;
            let (inputs, serial) = (self.inputs.clone(), session.serial);
            let started = std::thread::Builder::new().name("registry".into()).spawn(move || {
                let (entry, found) = catch_unwind(AssertUnwindSafe(|| {
                    read.run(reframe_conpty::process_alive, reframe_conpty::process_start)
                }))
                .unwrap_or((None, None));
                let _ = inputs.send(Input::Registry { serial, entry, found });
            });
            if started.is_err() {
                session.reading = false;
            }
        }
    }

    fn registry_read(&mut self, serial: u64, entry: Option<Entry>, found: Option<u32>) {
        let Some(session) = self.sessions.iter_mut().find(|s| s.serial == serial) else {
            return;
        };
        session.reading = false;
        if let Some(update) = session.tracker.as_mut().and_then(|t| t.registry(entry, found)) {
            self.status_changed(serial, update);
        }
    }

    /// Looks again at the sessions idle with background work (only those): one whose tool shells are all gone is
    /// done.
    fn recheck_background(&mut self, now: Instant) {
        if now.duration_since(self.background_checked) < self.background_every {
            return;
        }
        self.background_checked = now;
        let mut updates = Vec::new();
        for session in &mut self.sessions {
            let Some(tracker) = session.tracker.as_mut() else {
                continue;
            };
            if let Some(pid) = tracker.background_pid() {
                if let Some(update) = tracker.recheck(tool_shells(pid)) {
                    updates.push((session.serial, update));
                }
            }
        }
        for (serial, update) in updates {
            self.status_changed(serial, update);
        }
    }

    pub fn tick(&mut self, now: Instant) -> Flow {
        self.read_registries();
        self.recheck_background(now);
        let running = self.sessions.iter().any(|s| s.running().is_some());
        if self.client.is_some() || running {
            self.idle_since = now;
        } else if now.duration_since(self.idle_since) > IDLE_EXIT {
            return Flow::Exit;
        }
        Flow::Continue
    }
}

pub fn panic_text(panic: &Box<dyn std::any::Any + Send>) -> String {
    panic
        .downcast_ref::<&str>()
        .map(|s| (*s).to_string())
        .or_else(|| panic.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "panic".to_string())
}
