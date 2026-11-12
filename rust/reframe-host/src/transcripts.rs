//! The `transcripts` service (docs/ptyhost-protocol.md, "Services"): reframe's past-session list, session titles and
//! transcript watching, served from the host so they outlive a reframe and its restarts. The reading is
//! reframe-transcripts; this is the requests, the threads they run on and the folder watching.
//!
//! Reads run one thread per request, at most MAX_IN_FLIGHT at a time, MAX_WAITING more queued (beyond, `busy`). A watched folder is
//! watched by the system (ReadDirectoryChangesW through notify), never polled: `<home>` itself without its subfolders,
//! to see `projects` appear or go, and `<home>/projects` with them.

use std::collections::{HashMap, HashSet};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Component, Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};

use notify::event::ModifyKind;
use notify::{EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use reframe_transcripts::{Changes, ListOptions, TranscriptIndex, TranscriptTitles};
use serde_json::{json, Value};
use tokio::sync::mpsc::UnboundedSender;

use crate::host::Input;
use crate::limit::Limit;

pub const SERVICE: &str = "transcripts";
/// The version of the service the welcome advertises (`services.transcripts`).
pub const VERSION: u32 = 1;
const MAX_IN_FLIGHT: usize = 8;
/// Queued behind those: reframe's start asks for a burst of them.
const MAX_WAITING: usize = 64;

/// A failed request: its `error` in `svc-result`.
#[derive(Debug, PartialEq)]
pub struct SvcError {
    pub code: &'static str,
    pub message: String,
}

impl SvcError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    pub fn to_json(&self) -> Value {
        json!({ "code": self.code, "message": self.message })
    }
}

/// What the watching tells the host.
pub enum WatchEvent {
    /// Transcripts of these project folders of `home` changed.
    Changed { home: String, changes: Changes },
    /// `home/projects` appeared or went: watched again (if it is there), and everything counts as changed.
    Projects { home: String },
}

struct Home {
    index: Arc<TranscriptIndex>,
    titles: Arc<Mutex<TranscriptTitles>>,
    watch: Option<Watching>,
}

struct Watching {
    _home: RecommendedWatcher,
    projects: Option<RecommendedWatcher>,
}

pub struct Transcripts {
    homes: HashMap<String, Home>,
    limit: Limit,
}

/// A request's work, run off the host's task; it gets its answer to the host itself.
pub type Work = Box<dyn FnOnce() -> Result<Value, SvcError> + Send>;

impl Transcripts {
    pub fn new() -> Self {
        Self {
            homes: HashMap::new(),
            limit: Limit::new("transcripts", MAX_IN_FLIGHT, MAX_WAITING),
        }
    }

    fn home(&mut self, home: &str) -> &mut Home {
        self.homes.entry(home.to_string()).or_insert_with(|| Home {
            index: Arc::new(TranscriptIndex::new(home)),
            titles: Arc::new(Mutex::new(TranscriptTitles::default())),
            watch: None,
        })
    }

    /// Handles one request: answered now (`Ok(Some)`/`Err`), or `Ok(None)` when its work was started and will answer
    /// through `answer`.
    pub fn request(
        &mut self,
        method: &str,
        params: &Value,
        inputs: &UnboundedSender<Input>,
        answer: impl FnOnce(Result<Value, SvcError>) + Send + 'static,
    ) -> Result<Option<Value>, SvcError> {
        let home = string(params, "home")?.ok_or_else(|| SvcError::new("bad-params", "home is required"))?;
        if home.is_empty() {
            return Err(SvcError::new("bad-params", "home is required"));
        }
        let work: Work = match method {
            "list" => {
                let options = list_options(params)?;
                let index = self.home(&home).index.clone();
                Box::new(move || Ok(serde_json::to_value(index.list(&options)).unwrap_or(Value::Null)))
            }
            "info" => {
                let session_id = session_id(params)?;
                let index = self.home(&home).index.clone();
                Box::new(move || Ok(serde_json::to_value(index.find(&session_id)).unwrap_or(Value::Null)))
            }
            "times" => {
                let index = self.home(&home).index.clone();
                Box::new(move || {
                    let times: serde_json::Map<String, Value> = index
                        .modified_times()
                        .into_iter()
                        .map(|(id, at)| (id, json!(at)))
                        .collect();
                    Ok(Value::Object(times))
                })
            }
            "title" => {
                let session_id = session_id(params)?;
                let titles = self.home(&home).titles.clone();
                let path = PathBuf::from(&home);
                Box::new(move || {
                    let mut titles = titles.lock().unwrap_or_else(PoisonError::into_inner);
                    Ok(json!(titles.read(&path, &session_id)))
                })
            }
            "invalidate" => {
                let changes = match params.get("projects") {
                    Some(Value::String(all)) if all == "all" => Changes::All,
                    Some(Value::Array(list)) => {
                        Changes::Projects(list.iter().filter_map(Value::as_str).map(str::to_string).collect())
                    }
                    _ => return Err(SvcError::new("bad-params", "projects must be a list or \"all\"")),
                };
                self.home(&home).index.invalidate(changes);
                return Ok(Some(Value::Null));
            }
            "watch" => {
                self.watch(&home, inputs)?;
                return Ok(Some(Value::Null));
            }
            other => return Err(SvcError::new("unknown-method", format!("transcripts.{other}"))),
        };
        self.limit.run("transcript", move || {
            let outcome = catch_unwind(AssertUnwindSafe(work))
                .unwrap_or_else(|panic| Err(SvcError::new("io", crate::host::panic_text(&panic))));
            answer(outcome);
        })?;
        Ok(None)
    }

    /// Starts watching a folder (again: nothing more is done). `io` when the folder is not there.
    fn watch(&mut self, home: &str, inputs: &UnboundedSender<Input>) -> Result<(), SvcError> {
        if self.home(home).watch.is_some() {
            return Ok(());
        }
        let tx = inputs.clone();
        let name = home.to_string();
        let mut watcher = notify::recommended_watcher(move |event: notify::Result<notify::Event>| {
            // Only the folder appearing, going or being renamed; not its time changing as its contents change.
            let touches_projects = match &event {
                Ok(event) => {
                    !matches!(
                        event.kind,
                        EventKind::Access(_) | EventKind::Modify(ModifyKind::Data(_) | ModifyKind::Metadata(_))
                    ) && event
                        .paths
                        .iter()
                        .any(|p| p.file_name().is_some_and(|n| n.eq_ignore_ascii_case("projects")))
                }
                Err(_) => true,
            };
            if touches_projects {
                let _ = tx.send(Input::Watch(WatchEvent::Projects { home: name.clone() }));
            }
        })
        .map_err(|e| SvcError::new("io", e.to_string()))?;
        watcher
            .watch(Path::new(home), RecursiveMode::NonRecursive)
            .map_err(|e| SvcError::new("io", e.to_string()))?;
        let projects = watch_projects(home, inputs);
        self.home(home).watch = Some(Watching {
            _home: watcher,
            projects,
        });
        Ok(())
    }

    /// What the watching told: the index learns of it now, reframe after the host's batching.
    pub fn watched(&mut self, event: WatchEvent, inputs: &UnboundedSender<Input>) -> Option<(String, Changes)> {
        let (home, changes) = match event {
            WatchEvent::Changed { home, changes } => (home, changes),
            WatchEvent::Projects { home } => {
                let projects = watch_projects(&home, inputs);
                if let Some(watching) = self.homes.get_mut(&home).and_then(|h| h.watch.as_mut()) {
                    watching.projects = projects;
                }
                (home, Changes::All)
            }
        };
        self.homes.get(&home)?.index.invalidate(changes.clone());
        Some((home, changes))
    }
}

/// Watches `<home>/projects` with its subfolders, if it is there.
fn watch_projects(home: &str, inputs: &UnboundedSender<Input>) -> Option<RecommendedWatcher> {
    let dir = Path::new(home).join("projects");
    if !dir.is_dir() {
        return None;
    }
    let tx = inputs.clone();
    let (name, root) = (home.to_string(), dir.clone());
    let mut watcher = notify::recommended_watcher(move |event: notify::Result<notify::Event>| {
        let changes = match event {
            Ok(event) if matches!(event.kind, EventKind::Access(_)) => return,
            Ok(event) if !event.need_rescan() => projects_of(&root, &event.paths),
            _ => Changes::All,
        };
        let _ = tx.send(Input::Watch(WatchEvent::Changed {
            home: name.clone(),
            changes,
        }));
    })
    .ok()?;
    watcher.watch(&dir, RecursiveMode::Recursive).ok()?;
    Some(watcher)
}

/// The project folders (first components under `root`) of changed paths; all, for a path that is not in one.
fn projects_of(root: &Path, paths: &[PathBuf]) -> Changes {
    let mut projects = HashSet::new();
    for path in paths {
        match path.strip_prefix(root).ok().and_then(|p| p.components().next()) {
            Some(Component::Normal(name)) => {
                projects.insert(name.to_string_lossy().into_owned());
            }
            _ => return Changes::All,
        }
    }
    if projects.is_empty() {
        Changes::All
    } else {
        Changes::Projects(projects)
    }
}

/// `transcripts.changed` data.
pub fn changed_event(home: &str, changes: &Changes) -> Value {
    let projects = match changes {
        Changes::All => json!("all"),
        Changes::Projects(set) => {
            let mut list: Vec<&String> = set.iter().collect();
            list.sort();
            json!(list)
        }
    };
    json!({ "type": "svc-event", "service": SERVICE, "event": "changed", "data": { "home": home, "projects": projects } })
}

fn string(params: &Value, key: &str) -> Result<Option<String>, SvcError> {
    match params.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) => Ok(Some(s.clone())),
        Some(_) => Err(SvcError::new("bad-params", format!("{key} must be a string"))),
    }
}

fn session_id(params: &Value) -> Result<String, SvcError> {
    string(params, "sessionId")?
        .filter(|s| !s.is_empty())
        .ok_or_else(|| SvcError::new("bad-params", "sessionId is required"))
}

fn list_options(params: &Value) -> Result<ListOptions, SvcError> {
    let limit = params
        .get("limit")
        .and_then(Value::as_f64)
        .ok_or_else(|| SvcError::new("bad-params", "limit must be a number"))?;
    let exclude = match params.get("exclude") {
        None | Some(Value::Null) => HashSet::new(),
        Some(Value::Array(list)) => list.iter().filter_map(Value::as_str).map(str::to_string).collect(),
        Some(_) => return Err(SvcError::new("bad-params", "exclude must be a list")),
    };
    Ok(ListOptions {
        query: string(params, "query")?,
        limit,
        exclude,
        folder: string(params, "folder")?,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn changed_paths_name_their_project_folders() {
        let root = Path::new(r"C:\h\projects");
        let one = projects_of(root, &[root.join("p1").join("a.jsonl"), root.join("p2")]);
        assert_eq!(one, Changes::Projects(["p1".to_string(), "p2".to_string()].into()));
        assert_eq!(projects_of(root, &[root.to_path_buf()]), Changes::All);
        assert_eq!(projects_of(root, &[]), Changes::All);
    }

    #[test]
    fn bad_params_are_refused() {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        let mut service = Transcripts::new();
        let refused = |r: Result<Option<Value>, SvcError>| r.err().map(|e| e.code);
        assert_eq!(
            refused(service.request("list", &json!({}), &tx, |_| {})),
            Some("bad-params")
        );
        assert_eq!(
            refused(service.request("list", &json!({ "home": "h" }), &tx, |_| {})),
            Some("bad-params")
        );
        assert_eq!(
            refused(service.request("title", &json!({ "home": "h" }), &tx, |_| {})),
            Some("bad-params")
        );
        assert_eq!(
            refused(service.request("nope", &json!({ "home": "h" }), &tx, |_| {})),
            Some("unknown-method")
        );
        let missing = json!({ "home": r"C:\no\such\folder\anywhere" });
        assert_eq!(refused(service.request("watch", &missing, &tx, |_| {})), Some("io"));
    }
}
