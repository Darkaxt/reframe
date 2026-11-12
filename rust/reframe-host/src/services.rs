//! The `quota`, `clients`, `launch` and `state` services (docs/ptyhost-protocol.md, "Services"): what
//! reframe-services reads (and, for `state`, writes), asked by reframe when it needs it (the host keeps no timers for
//! them). Each request runs on a thread of its own, at most MAX_IN_FLIGHT at a time and MAX_WAITING more queued
//! (beyond, `busy`): the Coordinator can take seconds, a client's hash too. `state` has a queue of its own, so that
//! saving never waits for those.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use reframe_services::adoption::{adopted_client, Hashes};
use reframe_services::state::{StateError, StateFiles};
use reframe_services::{launch, quota};
use serde_json::{json, Value};

use crate::limit::Limit;
use crate::transcripts::{SvcError, Work};

/// The services and the version of each the welcome advertises.
pub const SERVICES: [(&str, u32); 4] = [("quota", 1), ("clients", 1), ("launch", 1), ("state", 1)];
const MAX_IN_FLIGHT: usize = 8;
const MAX_WAITING: usize = 64;

pub fn serves(service: &str) -> bool {
    SERVICES.iter().any(|(name, _)| *name == service)
}

pub struct Services {
    hashes: Arc<Hashes>,
    state: Arc<StateFiles>,
    reads: Limit,
    saves: Limit,
}

fn bad(message: impl Into<String>) -> SvcError {
    SvcError {
        code: "bad-params",
        message: message.into(),
    }
}

fn text(params: &Value, key: &str) -> Result<String, SvcError> {
    match params.get(key) {
        Some(Value::String(s)) if !s.is_empty() => Ok(s.clone()),
        _ => Err(bad(format!("{key} is required"))),
    }
}

impl Services {
    pub fn new() -> Self {
        Self {
            hashes: Arc::new(Hashes::default()),
            state: Arc::new(StateFiles::default()),
            reads: Limit::new("services", MAX_IN_FLIGHT, MAX_WAITING),
            // One at a time, in the order asked (StateFiles takes them one at a time anyway).
            saves: Limit::new("state", 1, MAX_WAITING),
        }
    }

    /// Starts a request's work, which answers through `answer`.
    pub fn request(
        &self,
        service: &str,
        method: &str,
        params: &Value,
        answer: impl FnOnce(Result<Value, SvcError>) + Send + 'static,
    ) -> Result<(), SvcError> {
        let hashes = self.hashes.clone();
        let work: Work = match (service, method) {
            ("quota", "codex") => {
                let local_app_data = match params.get("localAppData") {
                    Some(Value::String(s)) => s.clone(),
                    Some(Value::Null) | None => String::new(),
                    _ => return Err(bad("localAppData must be a string")),
                };
                let timeout = match params.get("timeoutMs") {
                    None | Some(Value::Null) => quota::TIMEOUT,
                    Some(v) => Duration::from_millis(v.as_u64().ok_or_else(|| bad("timeoutMs must be a number"))?),
                };
                Box::new(move || {
                    Ok(match quota::coordinator_executable(&local_app_data) {
                        None => json!({ "coordinator": false, "usage": null }),
                        Some(exe) => json!({ "coordinator": true, "usage": quota::read_codex_usage(&exe, timeout) }),
                    })
                })
            }
            ("quota", "statusLine") => {
                let home = text(params, "claudeHome")?;
                Box::new(move || Ok(json!(quota::user_has_status_line(&home))))
            }
            ("clients", "adopted") => {
                let record = text(params, "record")?;
                let provider = text(params, "provider")?;
                Box::new(move || Ok(adopted_client(&hashes, &record, &provider).to_json()))
            }
            ("launch", "evaluate") => {
                let file = text(params, "file")?;
                Box::new(move || Ok(launch::evaluate(&hashes, &file)))
            }
            ("state", "read") => {
                let file = text(params, "file")?;
                let state = self.state.clone();
                Box::new(move || {
                    let (revision, content) = state.read(Path::new(&file)).map_err(state_error)?;
                    Ok(json!({ "revision": revision, "content": content }))
                })
            }
            ("state", "write") => {
                let file = text(params, "file")?;
                let content = match params.get("content") {
                    Some(Value::String(s)) => s.clone(),
                    _ => return Err(bad("content must be a string")),
                };
                let revision = params
                    .get("revision")
                    .and_then(Value::as_u64)
                    .ok_or_else(|| bad("revision must be a number"))?;
                let backup = match params.get("backup") {
                    Some(Value::Bool(b)) => *b,
                    _ => return Err(bad("backup must be true or false")),
                };
                let state = self.state.clone();
                Box::new(move || {
                    let revision = state
                        .write(Path::new(&file), &content, revision, backup)
                        .map_err(state_error)?;
                    Ok(json!({ "revision": revision }))
                })
            }
            _ => {
                return Err(SvcError {
                    code: "unknown-method",
                    message: format!("{service}.{method}"),
                })
            }
        };
        let limit = if service == "state" { &self.saves } else { &self.reads };
        limit.run(service, move || {
            let outcome = catch_unwind(AssertUnwindSafe(work)).unwrap_or_else(|panic| {
                Err(SvcError {
                    code: "io",
                    message: crate::host::panic_text(&panic),
                })
            });
            answer(outcome);
        })?;
        Ok(())
    }
}

fn state_error(error: StateError) -> SvcError {
    match error {
        StateError::Conflict(message) => SvcError {
            code: "conflict",
            message,
        },
        StateError::Io(message) => SvcError { code: "io", message },
    }
}
