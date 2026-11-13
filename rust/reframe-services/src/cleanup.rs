//! What reframe leaves on disk and cleans up after a while (the `cleanup` service's `files`), as src/main/cleanup.ts
//! cleans it: files dropped or pasted into sessions (one folder each under the drops folder) older than a week, and
//! the hook settings files (`<session id>.json`) of sessions reframe no longer has. Nothing else is removed: only
//! entries directly in those two folders, and in the hooks folder only names ending in `.json`.

use std::collections::HashSet;
use std::fs;
use std::path::Path;
use std::time::UNIX_EPOCH;

/// How long a dropped file stays (DROPS_KEEP_MS).
pub const DROPS_KEEP_MS: f64 = 7.0 * 24.0 * 60.0 * 60_000.0;

/// olderThan: the entries older than `max_age_ms`.
pub fn older_than(entries: &[(String, f64)], now: f64, max_age_ms: f64) -> Vec<String> {
    entries
        .iter()
        .filter(|(_, mtime)| now - mtime > max_age_ms)
        .map(|(name, _)| name.clone())
        .collect()
}

/// strayHookFiles: hook settings files (`<session id>.json`) of sessions reframe no longer has.
pub fn stray_hook_files(names: &[String], session_ids: &HashSet<String>) -> Vec<String> {
    names
        .iter()
        .filter(|name| name.strip_suffix(".json").is_some_and(|id| !session_ids.contains(id)))
        .cloned()
        .collect()
}

/// The names in a folder (none when it cannot be read).
fn names(dir: &Path) -> Vec<String> {
    let Ok(entries) = fs::read_dir(dir) else {
        return Vec::new();
    };
    entries
        .flatten()
        .filter_map(|e| e.file_name().into_string().ok())
        .collect()
}

/// Each entry's modification time (ms since 1970, following links as fs.stat does); those that cannot be told left out.
fn entries(dir: &Path) -> Vec<(String, f64)> {
    names(dir)
        .into_iter()
        .filter_map(|name| {
            let modified = fs::metadata(dir.join(&name)).ok()?.modified().ok()?;
            let ms = match modified.duration_since(UNIX_EPOCH) {
                Ok(d) => d.as_secs_f64() * 1000.0,
                Err(e) => -(e.duration().as_secs_f64() * 1000.0),
            };
            Some((name, ms))
        })
        .collect()
}

/// `fs.rm(path, { recursive: true, force: true })`: a folder with what is in it, a file, or a link itself (never
/// what it points to); one that is not there counts as removed.
fn remove(path: &Path) -> bool {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return true;
    };
    let outcome = if meta.file_type().is_symlink() {
        fs::remove_file(path).or_else(|_| fs::remove_dir(path))
    } else if meta.is_dir() {
        fs::remove_dir_all(path)
    } else {
        fs::remove_file(path)
    };
    outcome.is_ok() || fs::symlink_metadata(path).is_err()
}

/// cleanUp: removes old drops and stray hook files; how many of each. One that cannot go now (in use) goes next time.
pub fn clean_up(drops: &Path, hooks: &Path, session_ids: &HashSet<String>, now: f64) -> (usize, usize) {
    let old = older_than(&entries(drops), now, DROPS_KEEP_MS);
    let stray = stray_hook_files(&names(hooks), session_ids);
    let removed = |dir: &Path, names: Vec<String>| names.iter().filter(|name| remove(&dir.join(name))).count();
    (removed(drops, old), removed(hooks, stray))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;
    use std::time::{Duration, SystemTime};

    #[test]
    fn picks_old_entries_and_stray_hook_files() {
        let entries = [("old".to_owned(), 0.0), ("new".to_owned(), 900.0)];
        assert_eq!(older_than(&entries, 1000.0, 500.0), ["old"]);
        let names: Vec<String> = ["a.json", "b.json", "notes.txt", ".json", "c.JSON"]
            .map(String::from)
            .into();
        let ids = HashSet::from(["a".to_owned()]);
        assert_eq!(stray_hook_files(&names, &ids), ["b.json", ".json"]);
    }

    #[test]
    fn removes_old_drops_and_stray_hook_files_and_nothing_else() {
        let dir = TestDir::new();
        dir.write("drops/old/image.png", "");
        dir.write("drops/new/image.png", "");
        dir.write("hooks/kept.json", "{}");
        dir.write("hooks/gone.json", "{}");
        dir.write("hooks/notes.txt", "");
        let long_ago = SystemTime::now() - Duration::from_millis(DROPS_KEEP_MS as u64 + 60_000);
        fs::File::options()
            .write(true)
            .open(dir.path().join("drops/old/image.png"))
            .unwrap()
            .set_modified(long_ago)
            .unwrap();
        // The folder's own time is what counts; set it through a handle that can open a folder.
        set_dir_modified(&dir.path().join("drops/old"), long_ago);
        let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs_f64() * 1000.0;
        let ids = HashSet::from(["kept".to_owned()]);
        let (drops, hooks) = (dir.path().join("drops"), dir.path().join("hooks"));
        assert_eq!(clean_up(&drops, &hooks, &ids, now), (1, 1));
        let mut left = names(&drops);
        left.sort();
        assert_eq!(left, ["new"]);
        let mut left = names(&hooks);
        left.sort();
        assert_eq!(left, ["kept.json", "notes.txt"]);
        let none = dir.path().join("none");
        assert_eq!(clean_up(&none, &none, &HashSet::new(), now), (0, 0));
    }

    #[cfg(windows)]
    fn set_dir_modified(dir: &Path, at: SystemTime) {
        use std::os::windows::fs::OpenOptionsExt;
        const FILE_FLAG_BACKUP_SEMANTICS: u32 = 0x0200_0000;
        fs::File::options()
            .write(true)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(dir)
            .unwrap()
            .set_modified(at)
            .unwrap();
    }

    #[cfg(not(windows))]
    fn set_dir_modified(dir: &Path, at: SystemTime) {
        fs::File::open(dir).unwrap().set_modified(at).unwrap();
    }
}
