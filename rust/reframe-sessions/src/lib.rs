//! The session status engine of the PTY host's `sessions` service (docs/ptyhost-protocol.md, "Services"), as
//! reframe's `SessionManager` (src/main/sessions.ts) runs it in-process: a session's status from Claude Code's hooks
//! (prompt submitted, response finished, needs input) and its session registry entry (src/main/claude/registry.ts),
//! and what to announce (a notification *intent*, never text). What depends on reframe (whether a session was ended
//! by it, how an early exit counts, whether a notification is shown) stays there.
#![forbid(unsafe_code)]

pub mod registry;

use std::path::PathBuf;

use registry::{find_entry, looks_like_claude, read_entry, Entry};
use serde_json::{json, Value};

/// What a session is doing, as `SessionStatus` names it (the host's part of it: `error` is reframe's).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    Starting,
    Idle,
    Busy,
    Waiting,
    Ended,
}

impl Status {
    pub fn as_str(self) -> &'static str {
        match self {
            Status::Starting => "starting",
            Status::Idle => "idle",
            Status::Busy => "busy",
            Status::Waiting => "waiting",
            Status::Ended => "ended",
        }
    }

    fn from_registry(status: &str) -> Option<Self> {
        match status {
            "idle" => Some(Status::Idle),
            "busy" => Some(Status::Busy),
            "waiting" => Some(Status::Waiting),
            _ => None,
        }
    }
}

/// What reframe may announce (`NotifyKind`): Claude is done, or needs the user.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Notify {
    Done,
    Waiting,
}

impl Notify {
    pub fn as_str(self) -> &'static str {
        match self {
            Notify::Done => "done",
            Notify::Waiting => "waiting",
        }
    }
}

/// The executables of the shells Claude Code runs its tools in (Bash and PowerShell tools, and what they start).
pub const TOOL_SHELLS: &[&str] = &["bash.exe", "sh.exe", "pwsh.exe", "powershell.exe", "cmd.exe"];
/// A shell younger than this is not counted: status line and hook commands come and go within it.
pub const TOOL_SHELL_MIN_AGE_MS: f64 = 5_000.0;
/// How often a session idle with background work is looked at again (only those sessions).
pub const BACKGROUND_RECHECK_MS: u64 = 20_000;

/// A FILETIME (100 ns since 1601) as ms since 1970.
pub fn filetime_ms(filetime: u64) -> f64 {
    filetime as f64 / 10_000.0 - 11_644_473_600_000.0
}

/// Whether a claude has tool shells running: direct children (`exe`, started at ms since 1970) named as in
/// `TOOL_SHELLS` (any case), at least `TOOL_SHELL_MIN_AGE_MS` old, and not older than claude (`parent_start`: a
/// child of an earlier process under the same id). A child whose start is unknown is not counted.
pub fn has_tool_shells<'a>(
    children: impl IntoIterator<Item = (&'a str, Option<f64>)>,
    parent_start: Option<f64>,
    now_ms: f64,
) -> bool {
    children.into_iter().any(|(exe, start)| {
        TOOL_SHELLS.iter().any(|shell| exe.eq_ignore_ascii_case(shell))
            && start.is_some_and(|start| {
                now_ms - start >= TOOL_SHELL_MIN_AGE_MS && parent_start.is_none_or(|parent| start >= parent)
            })
    })
}

/// Where a session's registry entry is, from what reframe put with it (its spawn's `meta`).
#[derive(Clone, Debug, PartialEq)]
pub enum Lookup {
    /// Claude's own process: `<claudeHome>/sessions/<pid>.json`.
    Direct { home: PathBuf },
    /// Run through cmd.exe: claude's entry, found by its session id once, then by its process id.
    Launched { home: PathBuf, claude_session_id: String },
    /// In WSL (no hooks): the entry of the Linux process, found by the session id over the distribution's share.
    Wsl {
        home: PathBuf,
        share: PathBuf,
        distro: String,
        claude_session_id: String,
    },
}

impl Lookup {
    /// Claude Code's session id the spawn gave (through cmd.exe and in WSL); a direct start's comes from its entry.
    pub fn claude_session_id(&self) -> Option<&str> {
        match self {
            Lookup::Direct { .. } => None,
            Lookup::Launched { claude_session_id, .. } | Lookup::Wsl { claude_session_id, .. } => {
                Some(claude_session_id)
            }
        }
    }

    /// `meta.wsl` with its `share`, or `meta.claudeHome` (with `meta.launched` through cmd.exe); else the session is
    /// not followed here (reframe before 0.10.1-claudegpt.22 started it, and follows it itself).
    pub fn from_meta(meta: Option<&Value>) -> Option<Self> {
        let meta = meta?;
        let text = |v: Option<&Value>| v.and_then(Value::as_str).filter(|s| !s.is_empty()).map(str::to_string);
        if let Some(wsl) = meta.get("wsl") {
            return Some(Lookup::Wsl {
                home: text(wsl.get("home"))?.into(),
                share: text(wsl.get("share"))?.into(),
                distro: text(wsl.get("distro"))?,
                claude_session_id: text(wsl.get("claudeSessionId"))?,
            });
        }
        let home: PathBuf = text(meta.get("claudeHome"))?.into();
        Some(
            match text(meta.get("launched").and_then(|l| l.get("claudeSessionId"))) {
                Some(claude_session_id) => Lookup::Launched {
                    home,
                    claude_session_id,
                },
                None => Lookup::Direct { home },
            },
        )
    }
}

/// One read of a session's registry entry, made off the host's task (a WSL share can take seconds).
#[derive(Clone, Debug)]
pub struct Read {
    lookup: Lookup,
    pid: u32,
    found: Option<u32>,
}

impl Read {
    /// The entry (None: none yet), and the process id it was found under (for the next read).
    pub fn run(
        &self,
        alive: impl Fn(u32) -> bool,
        start_of: impl Fn(u32) -> Option<String>,
    ) -> (Option<Entry>, Option<u32>) {
        match &self.lookup {
            Lookup::Direct { home } => (read_entry(home, self.pid), None),
            Lookup::Launched {
                home,
                claude_session_id,
            } => {
                if let Some(known) = self.found.and_then(|pid| read_entry(home, pid)) {
                    return (Some(known), self.found);
                }
                let found = find_entry(home, claude_session_id, |e| registry::is_running(e, &alive, &start_of));
                let pid = found.as_ref().and_then(Entry::pid);
                (found, pid)
            }
            Lookup::Wsl {
                home,
                share,
                distro,
                claude_session_id,
            } => {
                if let Some(known) = self.found.and_then(|pid| read_entry(home, pid)) {
                    return (Some(known), self.found);
                }
                let is_claude = |e: &Entry| {
                    e.pid().is_some_and(|pid| {
                        let file = share.join(distro).join("proc").join(pid.to_string()).join("cmdline");
                        looks_like_claude(&String::from_utf8_lossy(&std::fs::read(file).unwrap_or_default()))
                    })
                };
                let found = find_entry(home, claude_session_id, is_claude);
                let pid = found.as_ref().and_then(Entry::pid);
                (found, pid)
            }
        }
    }
}

/// A change to tell reframe: the status, the registry entry (as last read) and, at times, what to announce.
#[derive(Clone, Debug, PartialEq)]
pub struct Update {
    pub status: Status,
    pub registry: Option<Entry>,
    pub notify: Option<Notify>,
    /// Claude Code's session id as last known: the registry entry's, a hook's, or the spawn's.
    pub claude_session_id: Option<String>,
    /// Idle, with tool shells (background work) still running in claude: `done` waits until they are gone.
    pub background: bool,
}

/// The status of one session (one start of it), as SessionManager keeps it in its Runtime.
#[derive(Debug)]
pub struct Tracker {
    lookup: Lookup,
    pid: u32,
    /// The claude's own process id once its entry was found (through cmd.exe: claude's; in WSL: the Linux one).
    found: Option<u32>,
    status: Status,
    registry_status: Option<String>,
    last: Option<Entry>,
    /// When the process was started (ms since 1970), and its start as the system tells it.
    started_at: f64,
    proc_start: Option<String>,
    /// An announcement made while no reframe was attached: the next one gets it with the statuses.
    pub held: Option<Notify>,
    claude_session_id: Option<String>,
    /// Idle after a Stop while claude had tool shells running (see `Update::background`).
    background: bool,
    /// The host's status revision of its last change (the host keeps the counter, one for every session).
    pub revision: u64,
}

impl Tracker {
    pub fn new(lookup: Lookup, pid: u32, started_at: f64, proc_start: Option<String>) -> Self {
        Self {
            pid,
            found: None,
            status: Status::Starting,
            registry_status: None,
            last: None,
            started_at,
            claude_session_id: lookup.claude_session_id().map(str::to_string),
            lookup,
            proc_start,
            held: None,
            background: false,
            revision: 0,
        }
    }

    pub fn claude_session_id(&self) -> Option<&str> {
        self.claude_session_id.as_deref()
    }

    pub fn status(&self) -> Status {
        self.status
    }

    /// SessionManager.isRunning: starting, idle, busy or waiting.
    pub fn running(&self) -> bool {
        self.status != Status::Ended
    }

    fn update(&self, notify: Option<Notify>) -> Update {
        Update {
            status: self.status,
            registry: self.last.clone(),
            notify,
            claude_session_id: self.claude_session_id.clone(),
            background: self.background,
        }
    }

    /// Any status but idle ends background work's wait (a new turn, a question, the end).
    fn set(&mut self, status: Status) -> bool {
        let changed = self.status != status || (self.background && status != Status::Idle);
        if status != Status::Idle {
            self.background = false;
        }
        self.status = status;
        changed
    }

    /// Claude's own Windows process, whose children are its tool shells: none in WSL, or before a launched one's
    /// entry was found.
    pub fn claude_pid(&self) -> Option<u32> {
        match self.lookup {
            Lookup::Direct { .. } => Some(self.pid),
            Lookup::Launched { .. } => self.found,
            Lookup::Wsl { .. } => None,
        }
    }

    /// Claude's process, while the session is idle waiting for background work to end (for `recheck`).
    pub fn background_pid(&self) -> Option<u32> {
        self.background.then(|| self.claude_pid()).flatten()
    }

    /// A look at a session idle with background work: once no tool shell is left (and no new turn started), it is
    /// plain idle and `done` is announced.
    pub fn recheck(&mut self, shells: bool) -> Option<Update> {
        if !self.background || shells || self.status != Status::Idle {
            return None;
        }
        self.background = false;
        Some(self.update(Some(Notify::Done)))
    }

    /// A hook of the session, without looking for tool shells (as if there were none).
    pub fn hook(&mut self, event: &str, payload: &Value) -> Option<Update> {
        self.hook_with(event, payload, |_| false)
    }

    /// A hook of the session (SessionManager.onHook); the status line is no state change. `shells` tells whether
    /// claude (that process id) has tool shells running; it is asked only at a Stop.
    pub fn hook_with(&mut self, event: &str, payload: &Value, shells: impl FnOnce(u32) -> bool) -> Option<Update> {
        if !self.running() {
            return None;
        }
        // Every hook says which Claude Code session it is (it changes on /clear).
        let id = payload
            .get("session_id")
            .and_then(Value::as_str)
            .filter(|id| !id.is_empty());
        let id_changed = id.is_some_and(|id| self.claude_session_id.as_deref() != Some(id));
        if id_changed {
            self.claude_session_id = id.map(str::to_string);
        }
        let update = match event {
            "UserPromptSubmit" => self.set(Status::Busy).then(|| self.update(None)),
            "Stop" => {
                self.set(Status::Idle);
                // Background work (a shell or a monitor left running) can start a turn of its own: not done yet.
                self.background = self.claude_pid().is_some_and(shells);
                Some(self.update((!self.background).then_some(Notify::Done)))
            }
            // idle_prompt repeats "waiting for input" a minute after Stop; that was already announced.
            "Notification" if payload.get("notification_type").and_then(Value::as_str) != Some("idle_prompt") => {
                self.set(Status::Waiting);
                Some(self.update(Some(Notify::Waiting)))
            }
            _ => None,
        };
        update.or_else(|| id_changed.then(|| self.update(None)))
    }

    /// The next read of the registry, unless the session is over.
    pub fn read(&self) -> Option<Read> {
        self.running().then(|| Read {
            lookup: self.lookup.clone(),
            pid: self.pid,
            found: self.found,
        })
    }

    /// What a read found (SessionManager.followRegistry, after its stale check).
    pub fn registry(&mut self, entry: Option<Entry>, found: Option<u32>) -> Option<Update> {
        if !self.running() {
            return None;
        }
        if !matches!(self.lookup, Lookup::Direct { .. }) {
            self.found = found;
        }
        let entry = entry?;
        if self.is_stale(&entry) {
            return None;
        }
        let entry_changed = self.last.as_ref() != Some(&entry);
        if !entry.session_id.is_empty() {
            self.claude_session_id = Some(entry.session_id.clone());
        }
        self.last = Some(entry.clone());
        let wsl = matches!(self.lookup, Lookup::Wsl { .. });
        let mut notify = None;
        let mut changed = false;
        // Follow the registry only when it changes, so it does not undo a fresher hook-based status.
        let status = entry.status.as_deref().and_then(Status::from_registry);
        if let Some(status) = status.filter(|_| entry.status != self.registry_status) {
            let before = self.registry_status.replace(status.as_str().to_string());
            changed = self.set(status);
            // Without hooks (WSL), the registry is also what tells that Claude is done or needs the user.
            if wsl && status == Status::Waiting {
                notify = Some(Notify::Waiting);
            }
            if wsl && status == Status::Idle && before.as_deref() == Some("busy") {
                notify = Some(Notify::Done);
            }
        } else if self.status == Status::Starting {
            changed = self.set(status.unwrap_or(Status::Idle));
        }
        (entry_changed || changed || notify.is_some()).then(|| self.update(notify))
    }

    /// A registry entry under the session's process id that another process left behind (SessionManager.isStale).
    fn is_stale(&self, entry: &Entry) -> bool {
        if !matches!(self.lookup, Lookup::Direct { .. }) {
            return false;
        }
        match (&entry.proc_start, &self.proc_start) {
            (Some(theirs), Some(ours)) => theirs != ours,
            _ => entry.started_at.is_some_and(|at| at < self.started_at - 2_000.0),
        }
    }

    /// The process exited: nothing changes it any more (how that counts is reframe's).
    pub fn exited(&mut self) -> Option<Update> {
        self.set(Status::Ended).then(|| self.update(None))
    }

    /// The session as `sessions.list` gives it; an announcement held for the next reframe goes with it (once).
    pub fn snapshot(&mut self, session_id: &str, instance: u64) -> Value {
        self.snapshot_with(session_id, instance, true)
    }

    /// As `snapshot`; `take`: false for an observer, which leaves what is held for the primary.
    pub fn snapshot_with(&mut self, session_id: &str, instance: u64, take: bool) -> Value {
        let notify = if take { self.held.take() } else { None };
        update_json(session_id, instance, &self.update(notify), self.revision)
    }
}

/// `{ sessionId, instance, status, registry?, notify?, claudeSessionId?, revision }`: a `sessions.status` event's
/// data, and a `list` item.
pub fn update_json(session_id: &str, instance: u64, update: &Update, revision: u64) -> Value {
    let mut value = json!({
        "sessionId": session_id, "instance": instance, "status": update.status.as_str(), "revision": revision,
    });
    if let Some(id) = &update.claude_session_id {
        value["claudeSessionId"] = json!(id);
    }
    if let Some(entry) = &update.registry {
        value["registry"] = entry.to_json();
    }
    if let Some(notify) = update.notify {
        value["notify"] = json!(notify.as_str());
    }
    if update.background {
        value["background"] = json!(true);
    }
    value
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(status: Option<&str>, session_id: &str) -> Entry {
        Entry {
            pid: 7.0,
            session_id: session_id.into(),
            cwd: None,
            name: None,
            name_source: None,
            status: status.map(str::to_string),
            started_at: None,
            proc_start: None,
        }
    }

    fn direct() -> Tracker {
        Tracker::new(Lookup::Direct { home: "h".into() }, 7, 10_000.0, None)
    }

    #[test]
    fn follows_a_turn() {
        let mut t = direct();
        let u = t.registry(Some(entry(Some("idle"), "a")), None).unwrap();
        assert_eq!((u.status, u.notify), (Status::Idle, None));
        assert_eq!(t.hook("UserPromptSubmit", &json!({})).unwrap().status, Status::Busy);
        assert_eq!(t.hook("UserPromptSubmit", &json!({})), None);
        let u = t.hook("Stop", &json!({})).unwrap();
        assert_eq!((u.status, u.notify), (Status::Idle, Some(Notify::Done)));
        assert_eq!(
            t.hook("Notification", &json!({ "notification_type": "idle_prompt" })),
            None
        );
        let u = t
            .hook("Notification", &json!({ "notification_type": "permission_prompt" }))
            .unwrap();
        assert_eq!((u.status, u.notify), (Status::Waiting, Some(Notify::Waiting)));
        // The registry saying idle again (unchanged) does not undo the hook.
        assert_eq!(t.registry(Some(entry(Some("idle"), "a")), None), None);
        assert_eq!(t.exited().unwrap().status, Status::Ended);
        assert_eq!(t.hook("Stop", &json!({})), None);
    }

    #[test]
    fn wsl_announces_from_the_registry() {
        let lookup = Lookup::Wsl {
            home: "h".into(),
            share: "s".into(),
            distro: "D".into(),
            claude_session_id: "a".into(),
        };
        let mut t = Tracker::new(lookup, 7, 0.0, None);
        assert_eq!(
            t.registry(Some(entry(Some("idle"), "a")), Some(9)).unwrap().notify,
            None
        );
        assert_eq!(
            t.registry(Some(entry(Some("busy"), "a")), Some(9)).unwrap().notify,
            None
        );
        assert_eq!(
            t.registry(Some(entry(Some("waiting"), "a")), Some(9)).unwrap().notify,
            Some(Notify::Waiting)
        );
        assert_eq!(
            t.registry(Some(entry(Some("idle"), "a")), Some(9)).unwrap().notify,
            None
        );
        t.registry(Some(entry(Some("busy"), "a")), Some(9));
        assert_eq!(
            t.registry(Some(entry(Some("idle"), "a")), Some(9)).unwrap().notify,
            Some(Notify::Done)
        );
        assert_eq!(t.read().unwrap().found, Some(9));
    }

    #[test]
    fn ignores_entries_left_behind() {
        let mut t = direct();
        let mut old = entry(Some("busy"), "a");
        old.started_at = Some(1_000.0);
        assert_eq!(t.registry(Some(old), None), None);
        let mut fresh = entry(None, "a");
        fresh.started_at = Some(9_000.0);
        assert_eq!(t.registry(Some(fresh), None).unwrap().status, Status::Idle);
        let mut t = Tracker::new(Lookup::Direct { home: "h".into() }, 7, 0.0, Some("5".into()));
        let mut other = entry(Some("busy"), "a");
        other.proc_start = Some("6".into());
        assert_eq!(t.registry(Some(other), None), None);
    }

    #[test]
    fn knows_the_claude_session_id() {
        let mut t = direct();
        assert_eq!(t.claude_session_id(), None);
        let u = t.hook("SessionStart", &json!({ "session_id": "c1" })).unwrap();
        assert_eq!(u.claude_session_id.as_deref(), Some("c1"));
        assert_eq!(t.hook("SessionStart", &json!({ "session_id": "c1" })), None);
        t.registry(Some(entry(Some("idle"), "c2")), None);
        assert_eq!(t.claude_session_id(), Some("c2"));
        let json = t.snapshot_with("s", 1, false);
        assert_eq!(
            (json["claudeSessionId"].as_str(), json["revision"].as_u64()),
            (Some("c2"), Some(0))
        );
    }

    #[test]
    fn waits_for_background_work() {
        let mut t = direct();
        t.registry(Some(entry(Some("busy"), "a")), None);
        t.hook("UserPromptSubmit", &json!({}));
        // Stop with a tool shell: idle with background work, nothing announced.
        let u = t.hook_with("Stop", &json!({}), |pid| pid == 7).unwrap();
        assert_eq!((u.status, u.notify, u.background), (Status::Idle, None, true));
        assert_eq!(update_json("s", 1, &u, 1)["background"], json!(true));
        assert_eq!(t.background_pid(), Some(7));
        // The registry going idle keeps it waiting.
        assert_eq!(
            t.registry(Some(entry(Some("idle"), "a")), None).map(|u| u.background),
            Some(true)
        );
        assert_eq!(t.recheck(true), None);
        // The last shell is gone: idle, and done.
        let u = t.recheck(false).unwrap();
        assert_eq!(
            (u.status, u.notify, u.background),
            (Status::Idle, Some(Notify::Done), false)
        );
        assert!(update_json("s", 1, &u, 2).get("background").is_none());
        assert_eq!(t.recheck(false), None);
        assert_eq!(t.background_pid(), None);
        // Stop without one: done at once.
        let u = t.hook_with("Stop", &json!({}), |_| false).unwrap();
        assert_eq!((u.notify, u.background), (Some(Notify::Done), false));
    }

    #[test]
    fn a_new_turn_ends_the_wait() {
        let mut t = direct();
        t.hook_with("Stop", &json!({}), |_| true);
        let u = t.hook("UserPromptSubmit", &json!({})).unwrap();
        assert_eq!((u.status, u.background), (Status::Busy, false));
        assert_eq!(t.recheck(false), None);
        // Claude starting a turn of its own shows in the registry.
        t.registry(Some(entry(Some("idle"), "a")), None);
        t.hook_with("Stop", &json!({}), |_| true);
        let u = t.registry(Some(entry(Some("busy"), "a")), None).unwrap();
        assert_eq!((u.status, u.background), (Status::Busy, false));
        // A question is announced as before.
        t.hook_with("Stop", &json!({}), |_| true);
        let u = t
            .hook("Notification", &json!({ "notification_type": "permission_prompt" }))
            .unwrap();
        assert_eq!(
            (u.status, u.notify, u.background),
            (Status::Waiting, Some(Notify::Waiting), false)
        );
    }

    #[test]
    fn counts_only_lasting_tool_shells() {
        let now = 100_000.0;
        assert!(has_tool_shells([("bash.exe", Some(now - 6_000.0))], Some(0.0), now));
        assert!(has_tool_shells([("PowerShell.EXE", Some(now - 60_000.0))], None, now));
        assert!(!has_tool_shells([("bash.exe", Some(now - 1_000.0))], None, now));
        assert!(!has_tool_shells([("node.exe", Some(0.0))], None, now));
        assert!(!has_tool_shells([("cmd.exe", None)], None, now));
        // A child of an earlier process with the same id.
        assert!(!has_tool_shells([("cmd.exe", Some(10.0))], Some(50.0), now));
        assert_eq!(filetime_ms(116_444_736_000_000_000), 0.0);
        // No shells are looked for in WSL.
        let lookup = Lookup::Wsl {
            home: "h".into(),
            share: "s".into(),
            distro: "D".into(),
            claude_session_id: "a".into(),
        };
        let mut t = Tracker::new(lookup, 7, 0.0, None);
        let u = t.hook_with("Stop", &json!({}), |_| panic!("not asked in WSL")).unwrap();
        assert_eq!(u.notify, Some(Notify::Done));
    }

    #[test]
    fn reads_the_spawn_meta() {
        assert_eq!(Lookup::from_meta(None), None);
        assert_eq!(
            Lookup::from_meta(Some(&json!({ "launched": { "claudeSessionId": "x" } }))),
            None
        );
        assert_eq!(
            Lookup::from_meta(Some(
                &json!({ "claudeHome": "C:\\h", "launched": { "claudeSessionId": "x" } })
            )),
            Some(Lookup::Launched {
                home: "C:\\h".into(),
                claude_session_id: "x".into()
            })
        );
        let wsl = json!({ "wsl": { "distro": "D", "home": "\\\\s\\D\\h", "claudeSessionId": "x" } });
        assert_eq!(Lookup::from_meta(Some(&wsl)), None);
    }
}
