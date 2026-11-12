//! TranscriptIndex: past sessions, newest first, with search and paging. A project folder's transcripts are listed
//! once and again only after `invalidate` names that folder (or all): looking at every transcript's time each time is
//! what a long history with thousands of them cannot afford.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::UNIX_EPOCH;

use serde::Serialize;

use crate::folders::{folder_workspaces, workspace_key, FolderWorkspace};
use crate::info::{read_transcript_info, TranscriptInfo};
use crate::js;

/// Project folders whose transcripts changed, or all of them when that is not known.
#[derive(Debug, Clone, PartialEq)]
pub enum Changes {
    All,
    Projects(HashSet<String>),
}

impl Changes {
    pub fn none() -> Self {
        Changes::Projects(HashSet::new())
    }

    pub fn add(&mut self, other: Changes) {
        match (self, other) {
            (Changes::All, _) => {}
            (this, Changes::All) => *this = Changes::All,
            (Changes::Projects(mine), Changes::Projects(theirs)) => mine.extend(theirs),
        }
    }
}

#[derive(Debug, Default, Clone)]
pub struct ListOptions {
    pub query: Option<String>,
    pub limit: f64,
    pub exclude: HashSet<String>,
    pub folder: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Listing {
    pub items: Vec<TranscriptInfo>,
    pub has_more: bool,
    pub folders: Vec<FolderWorkspace>,
}

#[derive(Debug, Clone)]
struct TranscriptFile {
    file: PathBuf,
    session_id: String,
    modified_at: f64,
}

#[derive(Default)]
struct State {
    /// What was read from each transcript, at which modification time.
    cache: HashMap<PathBuf, (f64, Option<TranscriptInfo>)>,
    /// Each project folder's transcripts, as last listed, in the order the folders were first seen.
    listed: Vec<(String, Vec<TranscriptFile>)>,
}

/// One Claude Code folder's transcripts. Calls are serialized: one listing at a time, as the TypeScript shares one.
pub struct TranscriptIndex {
    home: PathBuf,
    stale: Mutex<Changes>,
    state: Mutex<State>,
}

impl TranscriptIndex {
    pub fn new(home: impl Into<PathBuf>) -> Self {
        Self {
            home: home.into(),
            stale: Mutex::new(Changes::All),
            state: Mutex::new(State::default()),
        }
    }

    pub fn home(&self) -> &Path {
        &self.home
    }

    /// Project folders whose transcripts changed: they are listed again next time.
    pub fn invalidate(&self, changes: Changes) {
        lock(&self.stale).add(changes);
    }

    pub fn list(&self, options: &ListOptions) -> Listing {
        let mut state = lock(&self.state);
        let files = self.files(&mut state);
        let query = options
            .query
            .as_deref()
            .map(|q| js::trim(q).to_lowercase())
            .filter(|q| !q.is_empty());
        let folder = options.folder.as_deref().filter(|f| !f.is_empty()).map(workspace_key);
        let mut items = Vec::new();
        let mut chats: Vec<(String, String)> = Vec::new();
        let mut has_more = false;
        for file in files.iter().filter(|f| !options.exclude.contains(&f.session_id)) {
            let Some(info) = info(&mut state, file) else {
                continue;
            };
            let cwd = info.cwd.as_deref().filter(|c| !c.is_empty());
            if let Some(cwd) = cwd {
                chats.push((cwd.to_string(), info.session_id.clone()));
            }
            if let Some(folder) = &folder {
                if cwd.is_none_or(|c| workspace_key(c) != *folder) {
                    continue;
                }
            }
            if let Some(query) = &query {
                let matches = |v: &Option<String>| v.as_deref().is_some_and(|v| v.to_lowercase().contains(query));
                if !matches(&info.title) && !matches(&info.cwd) {
                    continue;
                }
            }
            if items.len() as f64 >= options.limit {
                has_more = true;
            } else {
                items.push(info);
            }
        }
        let folders = folder_workspaces(chats.iter().map(|(c, i)| (c.as_str(), i.as_str())));
        Listing {
            items,
            has_more,
            folders,
        }
    }

    /// One past session by id.
    pub fn find(&self, session_id: &str) -> Option<TranscriptInfo> {
        let mut state = lock(&self.state);
        let file = self
            .files(&mut state)
            .into_iter()
            .find(|f| f.session_id == session_id)?;
        info(&mut state, &file)
    }

    /// When each session's transcript was last written (without reading them), newest first.
    pub fn modified_times(&self) -> Vec<(String, f64)> {
        let mut state = lock(&self.state);
        self.files(&mut state)
            .into_iter()
            .map(|f| (f.session_id, f.modified_at))
            .collect()
    }

    /// The transcripts, newest first: the project folders that changed are listed again.
    fn files(&self, state: &mut State) -> Vec<TranscriptFile> {
        let projects = self.home.join("projects");
        let stale = std::mem::replace(&mut *lock(&self.stale), Changes::none());
        let dirs: Vec<String> = match fs::read_dir(&projects) {
            Ok(entries) => entries
                .filter_map(Result::ok)
                .map(|e| e.file_name().to_string_lossy().into_owned())
                .collect(),
            Err(_) => Vec::new(),
        };
        let present: HashSet<&str> = dirs.iter().map(String::as_str).collect();
        state.listed.retain(|(dir, _)| present.contains(dir.as_str()));
        for dir in &dirs {
            let known = state.listed.iter().position(|(d, _)| d == dir);
            if let (Changes::Projects(stale), Some(_)) = (&stale, known) {
                if !stale.contains(dir) {
                    continue;
                }
            }
            let files = list_project(&projects, dir);
            match known {
                Some(index) => state.listed[index].1 = files,
                None => state.listed.push((dir.clone(), files)),
            }
        }
        let mut files: Vec<TranscriptFile> = state.listed.iter().flat_map(|(_, f)| f.iter().cloned()).collect();
        files.sort_by(|a, b| b.modified_at.total_cmp(&a.modified_at));
        // What was read from transcripts that are gone is forgotten.
        let present: HashSet<&Path> = files.iter().map(|f| f.file.as_path()).collect();
        state.cache.retain(|file, _| present.contains(file.as_path()));
        files
    }
}

fn info(state: &mut State, file: &TranscriptFile) -> Option<TranscriptInfo> {
    if let Some((modified_at, info)) = state.cache.get(&file.file) {
        if *modified_at == file.modified_at {
            return info.clone();
        }
    }
    // Unreadable right now: tried again next time.
    let info = read_transcript_info(&file.file)
        .ok()
        .flatten()
        .map(|(cwd, title)| TranscriptInfo {
            session_id: file.session_id.clone(),
            modified_at: file.modified_at,
            cwd,
            title,
        });
    state.cache.insert(file.file.clone(), (file.modified_at, info.clone()));
    info
}

/// The transcripts in one project folder, with their times.
fn list_project(projects: &Path, dir: &str) -> Vec<TranscriptFile> {
    let Ok(entries) = fs::read_dir(projects.join(dir)) else {
        return Vec::new();
    };
    entries
        .filter_map(Result::ok)
        .filter_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            let session_id = name.strip_suffix(".jsonl")?.to_string();
            let file = entry.path();
            // removed in the meantime: left out
            let modified_at = fs::metadata(&file).ok()?.modified().ok().map(mtime_ms)?;
            Some(TranscriptFile {
                file,
                session_id,
                modified_at,
            })
        })
        .collect()
}

/// Node's `mtimeMs`: seconds × 1000 + nanoseconds / 10⁶.
fn mtime_ms(time: std::time::SystemTime) -> f64 {
    match time.duration_since(UNIX_EPOCH) {
        Ok(d) => d.as_secs() as f64 * 1e3 + f64::from(d.subsec_nanos()) / 1e6,
        Err(before) => {
            let d = before.duration();
            -(d.as_secs() as f64 * 1e3 + f64::from(d.subsec_nanos()) / 1e6)
        }
    }
}

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;
    use std::time::{Duration, SystemTime};

    fn transcript(dir: &TestDir, project: &str, id: &str, cwd: &str, prompt: &str, age_s: u64) {
        let line = serde_json::json!({ "type": "user", "cwd": cwd, "message": { "content": prompt } });
        let file = dir.write(&format!("projects/{project}/{id}.jsonl"), line.to_string() + "\n");
        let time = SystemTime::now() - Duration::from_secs(age_s);
        fs::File::options()
            .write(true)
            .open(file)
            .unwrap()
            .set_modified(time)
            .unwrap();
    }

    fn options(limit: f64) -> ListOptions {
        ListOptions {
            limit,
            ..ListOptions::default()
        }
    }

    #[test]
    fn lists_newest_first_with_paging_filters_and_search() {
        let dir = TestDir::new();
        transcript(&dir, "p1", "a", r"C:\work\Alpha", "fix the Straße bug", 30);
        transcript(&dir, "p1", "b", r"C:\work\Alpha", "second", 20);
        transcript(&dir, "p2", "c", r"D:\other\Beta", "third", 10);
        let index = TranscriptIndex::new(dir.path());
        let all = index.list(&options(2.0));
        assert_eq!(
            all.items.iter().map(|i| i.session_id.as_str()).collect::<Vec<_>>(),
            ["c", "b"]
        );
        assert!(all.has_more);
        assert_eq!(
            all.folders.iter().map(|f| f.name.as_str()).collect::<Vec<_>>(),
            ["Alpha", "Beta"]
        );

        let mut excluded = options(10.0);
        excluded.exclude.insert("c".into());
        excluded.folder = Some(r"c:\WORK\alpha\".into());
        let listed = index.list(&excluded);
        assert_eq!(listed.items.len(), 2);
        assert_eq!(listed.folders.len(), 1);

        let mut query = options(10.0);
        query.query = Some("  STRASSE ".into());
        assert!(index.list(&query).items.is_empty());
        query.query = Some(" STRAßE ".into());
        assert_eq!(index.list(&query).items[0].session_id, "a");
        query.query = Some("beta".into());
        assert_eq!(index.list(&query).items[0].session_id, "c");
        assert_eq!(index.find("b").unwrap().title.as_deref(), Some("second"));
        assert_eq!(index.modified_times().len(), 3);
    }

    #[test]
    fn a_folder_is_listed_again_only_once_invalidated() {
        let dir = TestDir::new();
        transcript(&dir, "p1", "a", r"C:\a", "one", 30);
        let index = TranscriptIndex::new(dir.path());
        assert_eq!(index.list(&options(10.0)).items.len(), 1);
        transcript(&dir, "p1", "b", r"C:\a", "two", 20);
        assert_eq!(index.list(&options(10.0)).items.len(), 1);
        index.invalidate(Changes::Projects(["other".to_string()].into()));
        assert_eq!(index.list(&options(10.0)).items.len(), 1);
        index.invalidate(Changes::Projects(["p1".to_string()].into()));
        assert_eq!(index.list(&options(10.0)).items.len(), 2);
        // A new project folder is listed without being named.
        transcript(&dir, "p2", "c", r"C:\c", "three", 10);
        assert_eq!(index.list(&options(10.0)).items.len(), 3);
    }
}
