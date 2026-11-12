//! readTranscriptInfo: a transcript's folder and title from its first and last lines.

use std::fs::File;
use std::io::{self, Read, Seek, SeekFrom};
use std::path::Path;

use serde::Serialize;
use serde_json::{Map, Value};

use crate::js;

const HEAD_BYTES: u64 = 64 * 1024;
const TAIL_BYTES: u64 = 256 * 1024;
const TITLE_LENGTH: usize = 90;

/// A past session as the session list shows it.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TranscriptInfo {
    pub session_id: String,
    /// Milliseconds since 1970, as Node's `mtimeMs`.
    pub modified_at: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cwd: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
}

/// cwd and a title from a transcript's first and last lines; `None` when it holds no prompt.
pub fn read_transcript_info(file: &Path) -> io::Result<Option<(Option<String>, Option<String>)>> {
    let mut handle = File::open(file)?;
    let size = handle.metadata()?.len();
    let head = read_chunk(&mut handle, 0, size.min(HEAD_BYTES))?;
    let mut tail = String::new();
    if size > HEAD_BYTES {
        let start = HEAD_BYTES.max(size.saturating_sub(TAIL_BYTES));
        let chunk = read_chunk(&mut handle, start, size - start)?;
        tail = js::after_first_line(&chunk).to_string(); // drop the partial first line
    }
    let mut entries = parse_lines(&head);
    entries.extend(parse_lines(&tail));
    Ok(summarize(&entries))
}

/// The bytes at `position` (zeros past the end, as a Buffer that was not filled), decoded as Node decodes UTF-8.
fn read_chunk(handle: &mut File, position: u64, length: u64) -> io::Result<String> {
    let mut buffer = vec![0u8; length as usize];
    handle.seek(SeekFrom::Start(position))?;
    let mut filled = 0;
    while filled < buffer.len() {
        match handle.read(&mut buffer[filled..])? {
            0 => break,
            n => filled += n,
        }
    }
    Ok(String::from_utf8_lossy(&buffer).into_owned())
}

pub(crate) fn parse_lines(text: &str) -> Vec<Map<String, Value>> {
    text.split('\n')
        .filter(|line| !js::trim(line).is_empty())
        // a cut-off line at a chunk boundary is not JSON
        .filter_map(|line| match serde_json::from_str::<Value>(line) {
            Ok(Value::Object(entry)) => Some(entry),
            _ => None,
        })
        .collect()
}

fn is_type(entry: &Map<String, Value>, kind: &str) -> bool {
    entry.get("type").and_then(Value::as_str) == Some(kind)
}

fn summarize(entries: &[Map<String, Value>]) -> Option<(Option<String>, Option<String>)> {
    let last = |kind: &str, field: &str| {
        entries.iter().rev().find_map(|entry| {
            let value = is_type(entry, kind).then(|| entry.get(field)).flatten()?.as_str()?;
            let value = js::trim(value);
            (!value.is_empty()).then(|| value.to_string())
        })
    };
    let first_prompt = entries
        .iter()
        .filter(|e| is_type(e, "user"))
        .filter_map(|e| e.get("message")?.as_object()?.get("content")?.as_str())
        .find(|c| !c.starts_with('<') && !js::trim(c).is_empty())
        .map(str::to_string);
    let last_prompt = last("last-prompt", "lastPrompt");
    if first_prompt.is_none() && last_prompt.is_none() {
        return None;
    }
    let cwd = entries
        .iter()
        .filter(|e| is_type(e, "user"))
        .find_map(|e| e.get("cwd")?.as_str())
        .map(str::to_string);
    let title = last("agent-name", "agentName")
        .or_else(|| last("ai-title", "aiTitle"))
        .or(last_prompt)
        .or(first_prompt)
        .map(|t| js::truncate(js::trim(&js::collapse_spaces(&t)), TITLE_LENGTH));
    Some((cwd, title))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;

    fn user(content: &str, cwd: &str) -> String {
        serde_json::json!({ "type": "user", "cwd": cwd, "message": { "content": content } }).to_string() + "\n"
    }

    #[test]
    fn reads_the_folder_and_the_best_title() {
        let dir = TestDir::new();
        let text = format!(
            "{}{}{}{}",
            user("<command>", "C:\\x"),
            user("first   prompt\n here", "C:\\x"),
            r#"{"type":"ai-title","aiTitle":" AI "}"#.to_string() + "\n",
            r#"{"type":"last-prompt","lastPrompt":"later"}"#
        );
        let file = dir.write("a.jsonl", text);
        assert_eq!(
            read_transcript_info(&file).unwrap(),
            Some((Some("C:\\x".into()), Some("AI".into())))
        );
        let file = dir.write("b.jsonl", user("first   prompt\n here", "C:\\x"));
        assert_eq!(
            read_transcript_info(&file).unwrap().unwrap().1.unwrap(),
            "first prompt here"
        );
        let file = dir.write("c.jsonl", user("<only a command>", "C:\\x"));
        assert_eq!(read_transcript_info(&file).unwrap(), None);
    }

    #[test]
    fn a_bom_line_and_a_truncated_last_line_are_dropped() {
        let dir = TestDir::new();
        let text = format!(
            "\u{FEFF}{}{}{}",
            user("bom line", "C:\\bom"),
            user("kept", "C:\\kept"),
            r#"{"type":"ai-title","aiTi"#
        );
        let file = dir.write("a.jsonl", text);
        assert_eq!(
            read_transcript_info(&file).unwrap(),
            Some((Some("C:\\kept".into()), Some("kept".into())))
        );
    }

    #[test]
    fn a_large_transcript_is_read_at_its_start_and_end() {
        let dir = TestDir::new();
        let mut text = user("the first", "C:\\big");
        let filler =
            r#"{"type":"assistant","message":{"content":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}}"#;
        // An ai-title in the middle (neither head nor tail) is not seen.
        while text.len() < 100 * 1024 {
            text += filler;
            text.push('\n');
        }
        text += r#"{"type":"ai-title","aiTitle":"middle"}"#;
        text.push('\n');
        while text.len() < 600 * 1024 {
            text += filler;
            text.push('\n');
        }
        text += r#"{"type":"last-prompt","lastPrompt":"the last"}"#;
        let file = dir.write("big.jsonl", text);
        assert_eq!(
            read_transcript_info(&file).unwrap(),
            Some((Some("C:\\big".into()), Some("the last".into())))
        );
    }
}
