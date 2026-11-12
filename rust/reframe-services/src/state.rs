//! The `state` service's files (docs/ptyhost-protocol.md, "Services"): reframe's state.json read and written as
//! src/main/state/store.ts writes it, the content opaque (reframe checks it against its schema). Each file read or
//! written gets a revision; a write names the revision it replaces and is refused (`Conflict`) when that is not the
//! file's current one, or the file changed on disk since (another writer, or reframe writing it itself). A write
//! goes to `<file>.<pid>.tmp`, is flushed to disk and renamed over the file; with `backup`, the file there first is
//! copied to `<file>.bak` (reframe asks for it when it knows that file to be a good one).

use std::collections::HashMap;
use std::fs;
use std::io::{ErrorKind, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Mutex, PoisonError};
use std::time::{Duration, SystemTime};

#[derive(Debug, PartialEq)]
pub enum StateError {
    /// The revision named is not the file's current one.
    Conflict(String),
    Io(String),
}

/// The file as last read or written: its size and modification time (none: not there).
type Seen = Option<(u64, SystemTime)>;

struct Record {
    revision: u64,
    seen: Seen,
}

#[derive(Default)]
struct Files {
    last: u64,
    records: HashMap<PathBuf, Record>,
}

/// The state files this host read or wrote, with their revisions. One read or write at a time.
#[derive(Default)]
pub struct StateFiles {
    files: Mutex<Files>,
}

fn seen(file: &Path) -> Result<Seen, StateError> {
    match fs::metadata(file) {
        Ok(meta) => Ok(Some((meta.len(), meta.modified().map_err(io)?))),
        Err(e) if e.kind() == ErrorKind::NotFound => Ok(None),
        Err(e) => Err(io(e)),
    }
}

fn io(error: std::io::Error) -> StateError {
    StateError::Io(error.to_string())
}

impl Files {
    fn next(&mut self, file: &Path, seen: Seen) -> u64 {
        self.last += 1;
        let revision = self.last;
        self.records.insert(file.to_path_buf(), Record { revision, seen });
        revision
    }
}

impl StateFiles {
    /// The file's current revision and content (none: not there). Read as Node's `readFileSync(file, 'utf8')` does.
    pub fn read(&self, file: &Path) -> Result<(u64, Option<String>), StateError> {
        let mut files = self.files.lock().unwrap_or_else(PoisonError::into_inner);
        let (content, now) = match fs::File::open(file) {
            Ok(mut handle) => {
                let mut bytes = Vec::new();
                handle.read_to_end(&mut bytes).map_err(io)?;
                // Size and time of what was read (a rename meanwhile replaces the file, not this one).
                let meta = handle.metadata().map_err(io)?;
                let now = Some((meta.len(), meta.modified().map_err(io)?));
                (Some(String::from_utf8_lossy(&bytes).into_owned()), now)
            }
            Err(e) if e.kind() == ErrorKind::NotFound => (None, None),
            Err(e) => return Err(io(e)),
        };
        let revision = match files.records.get(file) {
            Some(record) if record.seen == now => record.revision,
            _ => files.next(file, now),
        };
        Ok((revision, content))
    }

    /// Writes `content` over the file if `revision` is its current one; the new revision.
    pub fn write(&self, file: &Path, content: &str, revision: u64, backup: bool) -> Result<u64, StateError> {
        let mut files = self.files.lock().unwrap_or_else(PoisonError::into_inner);
        let now = seen(file)?;
        match files.records.get(file) {
            Some(record) if record.revision == revision && record.seen == now => {}
            Some(record) if record.revision == revision => {
                return Err(StateError::Conflict(format!(
                    "{} changed since revision {revision}",
                    file.display()
                )))
            }
            Some(record) => {
                return Err(StateError::Conflict(format!(
                    "{} is at revision {}, not {revision}",
                    file.display(),
                    record.revision
                )))
            }
            None => {
                return Err(StateError::Conflict(format!(
                    "{} has not been read here",
                    file.display()
                )))
            }
        }
        if let Some(dir) = file.parent() {
            fs::create_dir_all(dir).map_err(io)?;
        }
        let mut temp = file.as_os_str().to_owned();
        temp.push(format!(".{}.tmp", std::process::id()));
        let temp = PathBuf::from(temp);
        let written = (|| {
            let mut handle = fs::File::create(&temp)?;
            handle.write_all(content.as_bytes())?;
            handle.sync_all()?;
            drop(handle);
            if backup && now.is_some() {
                let mut bak = file.as_os_str().to_owned();
                bak.push(".bak");
                fs::copy(file, PathBuf::from(bak))?;
            }
            rename_with_retry(&temp, file)
        })();
        if let Err(e) = written {
            let _ = fs::remove_file(&temp);
            // What is on disk now is not known to be what was read: the next write must read it again.
            files.records.remove(file);
            return Err(io(e));
        }
        let after = seen(file)?;
        Ok(files.next(file, after))
    }
}

/// On Windows a scanner or indexer can hold the target open for a moment (as store.ts's renameWithRetry).
fn rename_with_retry(from: &Path, to: &Path) -> std::io::Result<()> {
    let mut attempt: u64 = 0;
    loop {
        match fs::rename(from, to) {
            Ok(()) => return Ok(()),
            // Access denied, or a sharing violation (ERROR_SHARING_VIOLATION, 32).
            Err(e) if attempt < 4 && (e.kind() == ErrorKind::PermissionDenied || e.raw_os_error() == Some(32)) => {
                attempt += 1;
                std::thread::sleep(Duration::from_millis(20 * attempt));
            }
            Err(e) => return Err(e),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;

    /// A state.json as reframe's TypeScript writes it (JSON.stringify(state, null, 2): no newline at the end).
    const FROM_TS: &str = "{\n  \"version\": 3,\n  \"windows\": [\n    {\n      \"id\": \"w1\",\n      \"title\": \"Straße ✓\"\n    }\n  ],\n  \"sessions\": {}\n}";

    #[test]
    fn reads_a_file_the_typescript_wrote_and_writes_it_back_byte_for_byte() {
        let dir = TestDir::new();
        let file = dir.write("state.json", FROM_TS);
        let files = StateFiles::default();
        let (revision, content) = files.read(&file).unwrap();
        assert_eq!(content.as_deref(), Some(FROM_TS));
        let next = files.write(&file, FROM_TS, revision, true).unwrap();
        assert!(next > revision);
        assert_eq!(fs::read(&file).unwrap(), FROM_TS.as_bytes());
        assert_eq!(fs::read(dir.path().join("state.json.bak")).unwrap(), FROM_TS.as_bytes());
        // Nothing is left beside it.
        let mut names: Vec<_> = fs::read_dir(dir.path())
            .unwrap()
            .map(|e| e.unwrap().file_name())
            .collect();
        names.sort();
        assert_eq!(names, ["state.json", "state.json.bak"]);
    }

    #[test]
    fn a_missing_file_is_read_as_none_and_written_without_a_backup() {
        let dir = TestDir::new();
        let file = dir.path().join("sub").join("state.json");
        let files = StateFiles::default();
        let (revision, content) = files.read(&file).unwrap();
        assert_eq!(content, None);
        // Unchanged: the same revision.
        assert_eq!(files.read(&file).unwrap().0, revision);
        files.write(&file, "{}", revision, true).unwrap();
        assert_eq!(fs::read_to_string(&file).unwrap(), "{}");
        assert!(!dir.path().join("sub").join("state.json.bak").exists());
    }

    #[test]
    fn the_backup_is_the_previous_file_only_when_asked() {
        let dir = TestDir::new();
        let file = dir.write("state.json", "one");
        let files = StateFiles::default();
        let r1 = files.read(&file).unwrap().0;
        let r2 = files.write(&file, "two", r1, false).unwrap();
        assert!(!dir.path().join("state.json.bak").exists());
        files.write(&file, "three", r2, true).unwrap();
        assert_eq!(fs::read_to_string(dir.path().join("state.json.bak")).unwrap(), "two");
        assert_eq!(fs::read_to_string(&file).unwrap(), "three");
    }

    #[test]
    fn a_stale_revision_or_a_file_changed_meanwhile_is_a_conflict() {
        let dir = TestDir::new();
        let file = dir.write("state.json", "one");
        let files = StateFiles::default();
        // Never read here.
        assert!(matches!(
            files.write(&file, "x", 1, false),
            Err(StateError::Conflict(_))
        ));
        let r1 = files.read(&file).unwrap().0;
        let r2 = files.write(&file, "two", r1, false).unwrap();
        // A writer still holding r1.
        assert!(matches!(
            files.write(&file, "stale", r1, false),
            Err(StateError::Conflict(_))
        ));
        assert_eq!(fs::read_to_string(&file).unwrap(), "two");
        // Written by someone else (a longer file: a different size, whatever the clock's resolution).
        fs::write(&file, "someone else's").unwrap();
        assert!(matches!(
            files.write(&file, "mine", r2, false),
            Err(StateError::Conflict(_))
        ));
        assert_eq!(fs::read_to_string(&file).unwrap(), "someone else's");
        // Read again, it can be written.
        let (r3, content) = files.read(&file).unwrap();
        assert!(r3 > r2);
        assert_eq!(content.as_deref(), Some("someone else's"));
        files.write(&file, "mine", r3, true).unwrap();
        assert_eq!(fs::read_to_string(&file).unwrap(), "mine");
        assert_eq!(
            fs::read_to_string(dir.path().join("state.json.bak")).unwrap(),
            "someone else's"
        );
    }

    #[test]
    fn a_failed_write_leaves_the_file_and_no_temporary_file() {
        let dir = TestDir::new();
        let file = dir.write("state.json", "one");
        let files = StateFiles::default();
        let r1 = files.read(&file).unwrap().0;
        // The backup cannot be written: a folder is in its place.
        fs::create_dir(dir.path().join("state.json.bak")).unwrap();
        assert!(matches!(files.write(&file, "two", r1, true), Err(StateError::Io(_))));
        assert_eq!(fs::read_to_string(&file).unwrap(), "one");
        let tmp = dir.path().join(format!("state.json.{}.tmp", std::process::id()));
        assert!(!tmp.exists());
        // Read again first.
        assert!(matches!(
            files.write(&file, "two", r1, false),
            Err(StateError::Conflict(_))
        ));
    }
}
