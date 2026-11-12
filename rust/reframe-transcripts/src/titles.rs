//! Claude Code's own name for a session: the latest custom-title (from --name or /rename) or ai-title line of its
//! transcript. Claude Code appends these again and again, so the end of the file has the current one; only the end is
//! read, and only again when the file grew.

use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{self, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::js;

/// How much of a transcript's end is read for its title.
const TAIL_BYTES: u64 = 512 * 1024;

/// The latest title in transcript lines (JSONL). A custom title and an AI title count alike: the later wins.
pub fn latest_title(text: &str) -> Option<String> {
    text.split('\n').rev().find_map(|line| {
        if !line.contains("\"custom-title\"") && !line.contains("\"ai-title\"") {
            return None;
        }
        // a line cut by the tail, or one being written, is not JSON
        let entry: Value = serde_json::from_str(line).ok()?;
        let field = match entry.get("type")?.as_str()? {
            "custom-title" => "customTitle",
            "ai-title" => "aiTitle",
            _ => return None,
        };
        let title = js::trim(entry.get(field)?.as_str()?);
        (!title.is_empty()).then(|| title.to_string())
    })
}

struct Known {
    file: PathBuf,
    size: u64,
    title: Option<String>,
}

/// Titles per Claude session id of one Claude Code folder, as TranscriptTitles keeps them.
#[derive(Default)]
pub struct TranscriptTitles {
    known: HashMap<String, Known>,
}

impl TranscriptTitles {
    /// The session's current title; what was read before if the transcript cannot be read now.
    pub fn read(&mut self, home: &Path, session_id: &str) -> Option<String> {
        match self.read_now(home, session_id) {
            Ok(title) => title,
            Err(_) => self.known.get(session_id).and_then(|k| k.title.clone()),
        }
    }

    fn read_now(&mut self, home: &Path, session_id: &str) -> io::Result<Option<String>> {
        let prior = self.known.get(session_id);
        let file = match prior
            .map(|p| p.file.clone())
            .or_else(|| find_transcript(home, session_id))
        {
            Some(file) => file,
            None => return Ok(None),
        };
        let Ok(size) = fs::metadata(&file).map(|m| m.len()) else {
            self.known.remove(session_id);
            return Ok(None);
        };
        if let Some(prior) = prior.filter(|p| p.size == size) {
            return Ok(prior.title.clone());
        }
        // Grown: only what was added (a title there is newer); otherwise the end of the file.
        let grown = prior.filter(|p| size > p.size);
        let start = match grown {
            Some(prior) => prior.size.max(size.saturating_sub(TAIL_BYTES)),
            None => size.saturating_sub(TAIL_BYTES),
        };
        let mut text = read_range(&file, start, size)?;
        if start > 0 && grown.is_none() {
            text = js::after_first_line(&text).to_string();
        }
        let title = latest_title(&text).or_else(|| grown.and_then(|p| p.title.clone()));
        self.known.insert(
            session_id.to_string(),
            Known {
                file,
                size,
                title: title.clone(),
            },
        );
        Ok(title)
    }
}

fn find_transcript(home: &Path, session_id: &str) -> Option<PathBuf> {
    let projects = home.join("projects");
    fs::read_dir(&projects)
        .ok()?
        .filter_map(Result::ok)
        .map(|dir| projects.join(dir.file_name()).join(format!("{session_id}.jsonl")))
        .find(|file| fs::metadata(file).is_ok())
}

fn read_range(file: &Path, start: u64, end: u64) -> io::Result<String> {
    let mut handle = File::open(file)?;
    handle.seek(SeekFrom::Start(start))?;
    let mut buffer = Vec::with_capacity((end - start) as usize);
    handle.take(end - start).read_to_end(&mut buffer)?;
    Ok(String::from_utf8_lossy(&buffer).into_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;
    use std::io::Write;

    #[test]
    fn the_later_of_custom_and_ai_titles_wins() {
        let custom = r#"{"type":"custom-title","customTitle":" Mine "}"#;
        let ai = r#"{"type":"ai-title","aiTitle":"AI's"}"#;
        assert_eq!(latest_title(&format!("{custom}\n{ai}\n")).as_deref(), Some("AI's"));
        assert_eq!(latest_title(&format!("{ai}\n{custom}\n")).as_deref(), Some("Mine"));
        assert_eq!(
            latest_title(&format!("{ai}\n{{\"type\":\"custom-title\",\"customTi")).as_deref(),
            Some("AI's")
        );
        assert_eq!(latest_title(r#"{"type":"user","message":"\"ai-title\""}"#), None);
    }

    #[test]
    fn reads_the_end_and_then_only_what_was_added() {
        let dir = TestDir::new();
        let file = dir.write("projects/p/s.jsonl", "{\"type\":\"ai-title\",\"aiTitle\":\"first\"}\n");
        let mut titles = TranscriptTitles::default();
        assert_eq!(titles.read(dir.path(), "s").as_deref(), Some("first"));
        let mut handle = File::options().append(true).open(&file).unwrap();
        writeln!(handle, "{{\"type\":\"user\"}}").unwrap();
        assert_eq!(titles.read(dir.path(), "s").as_deref(), Some("first"));
        writeln!(handle, "{{\"type\":\"custom-title\",\"customTitle\":\"renamed\"}}").unwrap();
        assert_eq!(titles.read(dir.path(), "s").as_deref(), Some("renamed"));
        assert_eq!(titles.read(dir.path(), "missing"), None);
        drop(handle);
        fs::remove_file(&file).unwrap();
        assert_eq!(titles.read(dir.path(), "s"), None);
    }
}
