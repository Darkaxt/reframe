//! The `monitor` service's warnings: what a session started that seems to run away, read from the session's job (so
//! nothing has to be wrapped or gated to be seen). Three rules:
//! - `cpu`: a process the session started (not its own) uses at least `CPU_CORES` cores for `CPU_FOR` without a
//!   break;
//! - `scan`: a search tool (grep, rg, find, ...) given a whole disk (`/`, `C:\`, `/c`, `/mnt/c`) runs past
//!   `SCAN_FOR`;
//! - `leftover`: processes still run `LEFTOVER_AFTER` after the session ended.
//!
//! A warning is raised once and cleared when what it is about is gone. Only a warned process (and what it started),
//! or an ended session's leftovers, can be ended through `monitor.kill`.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use reframe_conpty::{ProcessUsage, Pty};
use serde_json::{json, Value};

/// How often the host checks.
pub const CHECK_EVERY: Duration = Duration::from_secs(15);
const CPU_CORES: f64 = 0.8;
const CPU_FOR: Duration = Duration::from_secs(10 * 60);
const SCAN_FOR: Duration = Duration::from_secs(2 * 60);
const LEFTOVER_AFTER: Duration = Duration::from_secs(60);
/// Search tools whose whole-disk runs are warned of (executable names without ".exe").
const SEARCHERS: &[&str] = &["grep", "egrep", "fgrep", "rg", "find", "fd", "findstr", "where"];
const COMMAND_LINE_MAX: usize = 2000;

/// FILETIME of 1970-01-01, in 100 ns units since 1601.
const UNIX_EPOCH_FILETIME: u64 = 116_444_736_000_000_000;

/// A watched session: its id and its process id (a session id is used again when it is resumed).
type SessionKey = (String, u32);

struct Watched {
    pty: Arc<Pty>,
    ended: Option<Instant>,
}

/// What the last check saw of a process.
struct Seen {
    cpu: u64,
    at: Instant,
    /// Since when it has used at least `CPU_CORES` (None while below).
    high_since: Option<Instant>,
    command_line: Option<String>,
}

struct Warning {
    session: SessionKey,
    /// The process it is about (id and creation time); None for an ended session's leftovers.
    target: Option<(u32, u64)>,
    value: Value,
}

#[derive(Default)]
pub struct Warnings {
    sessions: HashMap<SessionKey, Watched>,
    processes: HashMap<(u32, u64), Seen>,
    active: BTreeMap<String, Warning>,
}

/// What a check found: warnings raised, ids of those cleared.
#[derive(Default, Debug)]
pub struct Found {
    pub raised: Vec<Value>,
    pub cleared: Vec<String>,
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_millis() as u64)
}

fn filetime_ms(filetime: u64) -> u64 {
    filetime.saturating_sub(UNIX_EPOCH_FILETIME) / 10_000
}

fn minutes(duration: Duration) -> u64 {
    duration.as_secs() / 60
}

/// Whether a search tool's command line names a whole disk: "/", "\", "C:\", "C:/", "C:", and for a Unix tool
/// "/c", "/c/", "/mnt/c" (for findstr and where, "/s" is a switch).
pub fn names_whole_disk(exe: &str, command_line: &str) -> bool {
    let unix = !["findstr", "where"].iter().any(|w| stem(exe).eq_ignore_ascii_case(w));
    words(command_line).iter().skip(1).any(|word| {
        let w = word.trim_end_matches(['/', '\\']);
        let drive = |s: &str| s.len() == 1 && s.as_bytes()[0].is_ascii_alphabetic();
        (w.is_empty() && !word.is_empty())
            || (w.len() == 2 && w.ends_with(':') && drive(&w[..1]))
            || (unix && (w.strip_prefix('/').is_some_and(drive) || w.strip_prefix("/mnt/").is_some_and(drive)))
    })
}

/// A command line's words, quotes taken off (as Windows programs split them, near enough).
fn words(command_line: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut word = String::new();
    let (mut quoted, mut any) = (false, false);
    for c in command_line.chars() {
        match c {
            '"' => {
                quoted = !quoted;
                any = true;
            }
            c if c.is_whitespace() && !quoted => {
                if any || !word.is_empty() {
                    words.push(std::mem::take(&mut word));
                }
                any = false;
            }
            c => word.push(c),
        }
    }
    if any || !word.is_empty() {
        words.push(word);
    }
    words
}

fn stem(exe: &str) -> &str {
    exe.strip_suffix(".exe")
        .or_else(|| exe.strip_suffix(".EXE"))
        .unwrap_or(exe)
}

fn is_searcher(exe: &str) -> bool {
    SEARCHERS.iter().any(|s| s.eq_ignore_ascii_case(stem(exe)))
}

fn shorten(mut text: String) -> String {
    if text.len() > COMMAND_LINE_MAX {
        let mut end = COMMAND_LINE_MAX;
        while !text.is_char_boundary(end) {
            end -= 1;
        }
        text.truncate(end);
        text.push('\u{2026}');
    }
    text
}

impl Warnings {
    /// A check of the running sessions (id and pseudoconsole), and of those that ended since they were seen.
    pub fn check(&mut self, running: &[(String, Arc<Pty>)]) -> Found {
        let now = Instant::now();
        let mut keys = HashSet::new();
        for (id, pty) in running {
            let key = (id.clone(), pty.pid());
            keys.insert(key.clone());
            self.sessions.entry(key).or_insert_with(|| Watched {
                pty: pty.clone(),
                ended: None,
            });
        }
        let mut observed = Vec::new();
        for (key, watched) in &mut self.sessions {
            if watched.ended.is_none() && !keys.contains(key) {
                watched.ended = Some(now);
            }
            let members = watched.pty.usage().map(|u| u.processes).unwrap_or_default();
            observed.push((key.clone(), watched.ended, members));
        }
        let found = self.observe(&observed, now, now_ms(), &mut reframe_conpty::command_line);
        // An ended session is let go once nothing it started runs.
        self.sessions
            .retain(|key, w| w.ended.is_none() || observed.iter().any(|(k, _, m)| k == key && !m.is_empty()));
        found
    }

    /// The rules over what was read: per session, when it ended (if it did) and the processes of its job now.
    fn observe(
        &mut self,
        sessions: &[(SessionKey, Option<Instant>, Vec<ProcessUsage>)],
        now: Instant,
        now_ms: u64,
        command_line: &mut dyn FnMut(u32) -> Option<String>,
    ) -> Found {
        let mut found = Found::default();
        let mut alive = HashSet::new();
        let mut leftovers = HashSet::new();
        let mut seen = HashMap::new();
        let ms_at = |at: Instant| now_ms.saturating_sub(now.saturating_duration_since(at).as_millis() as u64);
        for ((id, root), ended, members) in sessions {
            let session = (id.clone(), *root);
            for member in members.iter().filter(|m| m.pid != *root) {
                let key = (member.pid, member.start);
                alive.insert((session.clone(), key));
                let before = self.processes.remove(&key);
                let cores = before.as_ref().map_or(0.0, |b| {
                    let wall = now.saturating_duration_since(b.at).as_secs_f64();
                    if wall > 0.0 {
                        member.cpu.saturating_sub(b.cpu) as f64 / 1e7 / wall
                    } else {
                        0.0
                    }
                });
                let high_since = if cores >= CPU_CORES {
                    before.as_ref().and_then(|b| b.high_since).or(Some(now))
                } else {
                    None
                };
                let age = Duration::from_millis(now_ms.saturating_sub(filetime_ms(member.start)));
                let mut line = before.and_then(|b| b.command_line);
                if line.is_none() && (is_searcher(&member.exe) || high_since.is_some()) {
                    line = command_line(member.pid).map(shorten);
                }
                let base = |kind: &str, since: u64, message: String| {
                    json!({
                        "id": format!("{id}:{}:{}:{kind}", member.pid, member.start),
                        "sessionId": id,
                        "kind": kind,
                        "pid": member.pid,
                        "name": member.exe,
                        "commandLine": line.clone().unwrap_or_default(),
                        "cores": (cores * 10.0).round() / 10.0,
                        "startedAt": filetime_ms(member.start),
                        "since": since,
                        "at": now_ms,
                        "message": message,
                    })
                };
                if let Some(since) = high_since.filter(|&s| now.saturating_duration_since(s) >= CPU_FOR) {
                    let message = format!(
                        "{} (pid {}) has used {:.1} cores for {} minutes",
                        member.exe,
                        member.pid,
                        cores,
                        minutes(now.saturating_duration_since(since))
                    );
                    self.raise(&mut found, &session, Some(key), base("cpu", ms_at(since), message));
                }
                if is_searcher(&member.exe)
                    && age >= SCAN_FOR
                    && line.as_deref().is_some_and(|l| names_whole_disk(&member.exe, l))
                {
                    let message = format!(
                        "{} (pid {}) has searched a whole disk for {} minutes",
                        member.exe,
                        member.pid,
                        minutes(age)
                    );
                    self.raise(
                        &mut found,
                        &session,
                        Some(key),
                        base("scan", filetime_ms(member.start), message),
                    );
                }
                seen.insert(
                    key,
                    Seen {
                        cpu: member.cpu,
                        at: now,
                        high_since,
                        command_line: line,
                    },
                );
            }
            let left: Vec<&ProcessUsage> = members.iter().filter(|m| m.pid != *root).collect();
            if let Some(ended) = ended.filter(|&e| now.saturating_duration_since(e) >= LEFTOVER_AFTER) {
                if !left.is_empty() {
                    leftovers.insert(session.clone());
                    let mut names: Vec<&str> = left.iter().map(|m| m.exe.as_str()).collect();
                    names.sort_unstable();
                    names.dedup();
                    let message = format!(
                        "{} process{} still running {} minutes after the session ended: {}",
                        left.len(),
                        if left.len() == 1 { "" } else { "es" },
                        minutes(now.saturating_duration_since(ended)),
                        names.join(", ")
                    );
                    let value = json!({
                        "id": format!("{id}:{root}:leftover"),
                        "sessionId": id,
                        "kind": "leftover",
                        "pid": left[0].pid,
                        "name": left[0].exe,
                        "processes": left.len(),
                        "since": ms_at(ended),
                        "at": now_ms,
                        "message": message,
                    });
                    self.raise(&mut found, &session, None, value);
                }
            }
        }
        self.processes = seen;
        let gone: Vec<String> = self
            .active
            .iter()
            .filter(|(_, w)| match w.target {
                Some(key) => !alive.contains(&(w.session.clone(), key)),
                None => !leftovers.contains(&w.session),
            })
            .map(|(id, _)| id.clone())
            .collect();
        for id in gone {
            self.active.remove(&id);
            found.cleared.push(id);
        }
        found
    }

    fn raise(&mut self, found: &mut Found, session: &SessionKey, target: Option<(u32, u64)>, value: Value) {
        let id = value["id"].as_str().unwrap_or_default().to_owned();
        if self.active.contains_key(&id) {
            return;
        }
        found.raised.push(value.clone());
        self.active.insert(
            id,
            Warning {
                session: session.clone(),
                target,
                value,
            },
        );
    }

    /// The warnings now, oldest first.
    pub fn list(&self) -> Vec<Value> {
        let mut list: Vec<Value> = self.active.values().map(|w| w.value.clone()).collect();
        list.sort_by_key(|v| v["at"].as_u64().unwrap_or(0));
        list
    }

    /// Ends what warning `id` is about: its process and what that started, or an ended session's leftovers. How
    /// many processes were ended; Err if there is no such warning or its session is gone.
    pub fn kill(&mut self, id: &str) -> Result<usize, String> {
        let warning = self.active.get(id).ok_or_else(|| format!("no warning {id}"))?;
        let watched = self
            .sessions
            .get(&warning.session)
            .ok_or_else(|| format!("the session of {id} is no longer watched"))?;
        let members: Vec<(u32, u64)> = watched
            .pty
            .usage()
            .map(|u| u.processes.iter().map(|m| (m.pid, m.start)).collect())
            .unwrap_or_default();
        let root = warning.session.1;
        let targets = match warning.target {
            Some(target) if members.contains(&target) => tree(target, &members),
            Some(_) => Vec::new(),
            None if watched.ended.is_some() => members.into_iter().filter(|&(pid, _)| pid != root).collect(),
            None => return Err(format!("the session of {id} runs")),
        };
        let killed = targets
            .iter()
            .filter(|&&(pid, start)| pid != root && reframe_conpty::terminate(pid, start))
            .count();
        self.active.remove(id);
        Ok(killed)
    }
}

/// A process and its descendants among a job's members (by parent id, a child never older than its parent).
fn tree(target: (u32, u64), members: &[(u32, u64)]) -> Vec<(u32, u64)> {
    let mut tree = vec![target];
    let mut next = 0;
    while next < tree.len() {
        let (pid, start) = tree[next];
        next += 1;
        for child in reframe_conpty::child_processes(pid) {
            let Some(child_start) = child.start else { continue };
            let key = (child.pid, child_start);
            if child_start >= start && members.contains(&key) && !tree.contains(&key) {
                tree.push(key);
            }
        }
    }
    tree
}

#[cfg(test)]
mod tests {
    use super::*;

    fn process(pid: u32, exe: &str, cpu: u64, start: u64) -> ProcessUsage {
        ProcessUsage {
            pid,
            exe: exe.into(),
            cpu,
            memory: 0,
            start,
        }
    }

    /// FILETIME of `ms` after the epoch.
    fn ft(ms: u64) -> u64 {
        ms * 10_000 + UNIX_EPOCH_FILETIME
    }

    #[test]
    fn whole_disk_command_lines() {
        for line in [
            r#"grep -r foo /"#,
            r#""C:\Program Files\Git\usr\bin\grep.exe" -rn foo /c/"#,
            r#"rg foo C:\"#,
            r#"rg foo "C:/""#,
            r#"find /mnt/c -name x"#,
        ] {
            assert!(names_whole_disk("grep.exe", line), "{line}");
        }
        assert!(names_whole_disk("where.exe", "where /r c: x"));
        assert!(names_whole_disk("findstr.exe", r#"findstr /s x \"#));
        assert!(!names_whole_disk("findstr.exe", "findstr /s /c:x *.rs"));
        for line in [
            "grep -r foo .",
            "rg foo C:\\Users\\me\\project",
            "find /c/Users/me -name x",
            "/",
            "grep -rn foo /mnt/c/work",
        ] {
            assert!(!names_whole_disk("grep.exe", line), "{line}");
        }
        assert!(is_searcher("grep.exe") && is_searcher("RG.EXE") && is_searcher("find"));
        assert!(!is_searcher("node.exe") && !is_searcher("cmd.exe"));
    }

    #[test]
    fn a_process_busy_for_ten_minutes() {
        let mut warnings = Warnings::default();
        let t0 = Instant::now();
        let session = || ("s1".to_string(), 100);
        let mut line = |_: u32| Some("node busy.js".to_string());
        // One core (1e7 per second) every check, 15 s apart; the root (pid 100) is never warned of.
        let mut raised = Vec::new();
        for step in 0..=41u64 {
            let at = t0 + Duration::from_secs(step * 15);
            let cpu = step * 15 * 10_000_000;
            let members = vec![
                process(100, "claude.exe", cpu, ft(1)),
                process(7, "node.exe", cpu, ft(2)),
            ];
            let found = warnings.observe(&[(session(), None, members)], at, 1_000_000 + step * 15_000, &mut line);
            raised.extend(found.raised.into_iter().map(|v| (step, v)));
        }
        // High from the second check (step 1); ten minutes later is step 41.
        assert_eq!(raised.len(), 1);
        let (step, value) = &raised[0];
        assert_eq!(*step, 41);
        assert_eq!(value["kind"], "cpu");
        assert_eq!(value["pid"], 7);
        assert_eq!(value["commandLine"], "node busy.js");
        assert_eq!(warnings.list().len(), 1);
        // The process ends: the warning is cleared.
        let found = warnings.observe(
            &[(session(), None, vec![])],
            t0 + Duration::from_secs(42 * 15),
            0,
            &mut line,
        );
        assert_eq!(found.cleared.len(), 1);
        assert!(warnings.list().is_empty());
    }

    #[test]
    fn a_break_starts_the_count_again() {
        let mut warnings = Warnings::default();
        let t0 = Instant::now();
        let mut cpu = 0;
        let mut line = |_: u32| None;
        for step in 0..=60u64 {
            // Idle for one check at step 20.
            if step != 20 {
                cpu += 15 * 10_000_000;
            }
            let members = vec![process(7, "node.exe", cpu, ft(2))];
            let at = t0 + Duration::from_secs(step * 15);
            let found = warnings.observe(&[(("s".into(), 1), None, members)], at, 0, &mut line);
            // High again from step 21: ten minutes later would be step 61.
            assert!(found.raised.is_empty(), "step {step}");
        }
    }

    #[test]
    fn a_whole_disk_search_past_two_minutes() {
        let mut warnings = Warnings::default();
        let t0 = Instant::now();
        let now = 10_000_000;
        let mut asked = 0;
        let mut line = |_: u32| {
            asked += 1;
            Some("grep -r needle /".to_string())
        };
        let young = vec![process(8, "grep.exe", 0, ft(now - 60_000))];
        let found = warnings.observe(&[(("s".into(), 1), None, young)], t0, now, &mut line);
        assert!(found.raised.is_empty());
        let old = vec![process(8, "grep.exe", 0, ft(now - 60_000))];
        let found = warnings.observe(
            &[(("s".into(), 1), None, old)],
            t0 + Duration::from_secs(90),
            now + 90_000,
            &mut line,
        );
        assert_eq!(found.raised.len(), 1);
        assert_eq!(found.raised[0]["kind"], "scan");
        // The command line is read once.
        assert_eq!(asked, 1);
    }

    #[test]
    fn leftovers_of_an_ended_session() {
        let mut warnings = Warnings::default();
        let t0 = Instant::now();
        let ended = Some(t0);
        let mut line = |_: u32| None;
        let left = || vec![process(9, "node.exe", 0, ft(5)), process(10, "bash.exe", 0, ft(6))];
        let key = || ("s".to_string(), 1);
        let found = warnings.observe(&[(key(), ended, left())], t0 + Duration::from_secs(30), 0, &mut line);
        assert!(found.raised.is_empty());
        let found = warnings.observe(&[(key(), ended, left())], t0 + Duration::from_secs(60), 0, &mut line);
        assert_eq!(found.raised.len(), 1);
        assert_eq!(found.raised[0]["kind"], "leftover");
        assert_eq!(found.raised[0]["processes"], 2);
        assert_eq!(found.raised[0]["id"], "s:1:leftover");
        let found = warnings.observe(&[(key(), ended, vec![])], t0 + Duration::from_secs(75), 0, &mut line);
        assert_eq!(found.cleared, vec!["s:1:leftover".to_string()]);
    }

    #[test]
    fn only_a_warning_can_be_killed() {
        assert!(Warnings::default().kill("s:1:leftover").is_err());
    }
}
