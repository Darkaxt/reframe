//! The `quota`, `clients` and `launch` services (docs/ptyhost-protocol.md, "Services"): what reframe-services reads,
//! asked by reframe when it needs it (the host keeps no timers for them). Each request runs on a thread of its own,
//! at most MAX_IN_FLIGHT at a time (more are answered `busy`): the Coordinator can take seconds, a client's hash too.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use reframe_services::adoption::{adopted_client, Hashes};
use reframe_services::{launch, quota};
use serde_json::{json, Value};

use crate::transcripts::{SvcError, Work};

/// The services and the version of each the welcome advertises.
pub const SERVICES: [(&str, u32); 3] = [("quota", 1), ("clients", 1), ("launch", 1)];
const MAX_IN_FLIGHT: usize = 8;

pub fn serves(service: &str) -> bool {
    SERVICES.iter().any(|(name, _)| *name == service)
}

pub struct Services {
    hashes: Arc<Hashes>,
    in_flight: Arc<Mutex<usize>>,
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
            in_flight: Arc::new(Mutex::new(0)),
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
            _ => {
                return Err(SvcError {
                    code: "unknown-method",
                    message: format!("{service}.{method}"),
                })
            }
        };
        {
            let mut in_flight = self.in_flight.lock().unwrap_or_else(PoisonError::into_inner);
            if *in_flight >= MAX_IN_FLIGHT {
                return Err(SvcError {
                    code: "busy",
                    message: format!("too many {service} requests at once"),
                });
            }
            *in_flight += 1;
        }
        let in_flight = self.in_flight.clone();
        let started = std::thread::Builder::new().name(service.into()).spawn(move || {
            let outcome = catch_unwind(AssertUnwindSafe(work)).unwrap_or_else(|panic| {
                Err(SvcError {
                    code: "io",
                    message: crate::host::panic_text(&panic),
                })
            });
            *in_flight.lock().unwrap_or_else(PoisonError::into_inner) -= 1;
            answer(outcome);
        });
        if let Err(error) = started {
            *self.in_flight.lock().unwrap_or_else(PoisonError::into_inner) -= 1;
            return Err(SvcError {
                code: "io",
                message: error.to_string(),
            });
        }
        Ok(())
    }
}
