//! The Overview index over fixture transcripts (never a real Claude Code folder).

use std::fs::OpenOptions;
use std::io::Write;
use std::time::{Duration, Instant};

use serde_json::json;

use super::*;
use crate::testdir::TestDir;

fn user(at: &str, text: &str) -> String {
    json!({ "type": "user", "timestamp": at, "cwd": "D:\\Work\\alpha", "sessionId": "s",
        "message": { "role": "user", "content": text } })
    .to_string()
}

fn tool_result(at: &str) -> String {
    json!({ "type": "user", "timestamp": at, "message": { "role": "user",
        "content": [{ "type": "tool_result", "tool_use_id": "t", "content": "secret output" }] } })
    .to_string()
}

fn assistant(at: &str, id: &str, model: &str, tools: &[&str]) -> String {
    let mut content = vec![json!({ "type": "text", "text": "ok" })];
    content.extend(
        tools
            .iter()
            .map(|t| json!({ "type": "tool_use", "id": "t", "name": t, "input": {} })),
    );
    json!({ "type": "assistant", "timestamp": at,
        "message": { "id": id, "model": model, "role": "assistant", "content": content } })
    .to_string()
}

fn lines(items: &[String]) -> String {
    items.iter().map(|l| format!("{l}\n")).collect()
}

fn session() -> String {
    lines(&[
        user("2026-03-01T10:00:00.000Z", "PRIVATE PROMPT TEXT"),
        assistant("2026-03-01T10:00:05.000Z", "m1", "claude-opus-5-5", &["Read", "Edit"]),
        // The same message again (Claude writes a line per content block): one turn.
        assistant("2026-03-01T10:00:06.000Z", "m1", "claude-opus-5-5", &["Bash"]),
        tool_result("2026-03-01T10:00:07.000Z"),
        assistant(
            "2026-03-01T10:02:00.000Z",
            "m2",
            "claude-sonnet-5",
            &["WebFetch", "Task"],
        ),
        json!({ "type": "ai-title", "aiTitle": "Fix the build" }).to_string(),
        user("2026-03-02T09:00:00Z", "next day"),
        json!({ "type": "user", "isMeta": true, "timestamp": "2026-03-02T09:00:01Z",
            "message": { "content": "meta" } })
        .to_string(),
    ])
}

fn index(path: &Path, entry: &mut FileEntry) -> ReadOutcome {
    index_file(path, entry, &Utc).unwrap()
}

#[test]
fn counts_prompts_turns_sessions_minutes_models_and_tools() {
    let dir = TestDir::new();
    let file = dir.write("projects/p/s1.jsonl", session());
    let mut entry = FileEntry::default();
    index(&file, &mut entry);
    let first = &entry.days["2026-03-01"];
    assert_eq!(
        first.counts,
        Counts {
            prompts: 1,
            turns: 2,
            sessions: 1,
            active_minutes: 2
        }
    );
    assert_eq!(first.models.get("claude-opus-5-5"), Some(&1));
    assert_eq!(first.models.get("claude-sonnet-5"), Some(&1));
    let tools: Vec<_> = first.tools.iter().map(|(k, v)| (k.as_str(), *v)).collect();
    assert_eq!(tools, [("bash", 1), ("edit", 1), ("other", 1), ("read", 1), ("web", 1)]);
    assert_eq!(entry.days["2026-03-02"].counts.prompts, 1);
    assert_eq!(entry.days["2026-03-02"].counts.sessions, 0);
    assert_eq!(entry.title.as_deref(), Some("Fix the build"));
    assert_eq!(entry.title_source.as_deref(), Some("ai"));
    assert_eq!(entry.cwd.as_deref(), Some("D:\\Work\\alpha"));
    // No prompt text in what is kept.
    assert!(!serde_json::to_string(&entry).unwrap().contains("PRIVATE"));
}

#[test]
fn reads_only_appended_lines_and_waits_for_unfinished_ones() {
    let dir = TestDir::new();
    let file = dir.write("projects/p/s1.jsonl", session());
    let mut entry = FileEntry::default();
    let read = index(&file, &mut entry);
    assert_eq!(read.bytes, std::fs::metadata(&file).unwrap().len());
    assert_eq!(index(&file, &mut entry).bytes, 0);

    // A line being written (no newline yet) is not read, then read once finished.
    let mut out = OpenOptions::new().append(true).open(&file).unwrap();
    let late = user("2026-03-02T09:30:00Z", "later");
    write!(out, "{}", &late[..20]).unwrap();
    assert_eq!(index(&file, &mut entry).bytes, 0);
    writeln!(out, "{}", &late[20..]).unwrap();
    let read = index(&file, &mut entry);
    assert_eq!(read.bytes, late.len() as u64 + 1);
    assert!(!read.rewritten);
    assert_eq!(entry.days["2026-03-02"].counts.prompts, 2);
    assert_eq!(entry.days["2026-03-01"].counts.prompts, 1);
}

#[test]
fn a_truncated_or_rewritten_file_is_read_again_from_its_start() {
    let dir = TestDir::new();
    let file = dir.write("projects/p/s1.jsonl", session());
    let mut entry = FileEntry::default();
    index(&file, &mut entry);

    // Shorter: read again, nothing counted twice.
    std::fs::write(&file, lines(&[user("2026-04-01T08:00:00Z", "only")])).unwrap();
    let read = index(&file, &mut entry);
    assert!(read.rewritten);
    assert_eq!(entry.days.keys().collect::<Vec<_>>(), ["2026-04-01"]);
    assert_eq!(entry.days["2026-04-01"].counts.sessions, 1);

    // Longer but starting differently.
    let mut other = lines(&[user("2026-05-01T08:00:00Z", "different start")]);
    other.push_str(&session());
    std::fs::write(&file, other).unwrap();
    assert!(index(&file, &mut entry).rewritten);
    assert!(!entry.days.contains_key("2026-04-01"));
    assert_eq!(entry.days["2026-05-01"].counts.sessions, 1);
    assert_eq!(entry.days["2026-03-01"].counts.turns, 2);
}

#[test]
fn malformed_and_too_long_lines_are_skipped() {
    let dir = TestDir::new();
    let mut text = String::from("{not json\n\n");
    text.push_str(&format!(
        "{{\"type\":\"user\",\"pad\":\"{}\"}}\n",
        "x".repeat(MAX_LINE_BYTES)
    ));
    text.push_str(&session());
    let file = dir.write("projects/p/s1.jsonl", text);
    let mut entry = FileEntry::default();
    index(&file, &mut entry);
    assert_eq!(entry.skipped_lines, 2);
    assert_eq!(entry.days["2026-03-01"].counts.turns, 2);
}

#[test]
fn timestamps_and_days() {
    assert_eq!(parse_timestamp("1970-01-01T00:00:00.000Z"), Some(0));
    assert_eq!(parse_timestamp("2026-03-01T10:00:05.5Z"), Some(1_772_359_205_500));
    assert_eq!(parse_timestamp("2026-03-01T12:00:05+02:00"), Some(1_772_359_205_000));
    assert_eq!(parse_timestamp("yesterday"), None);
    assert_eq!(Utc.day(1_772_359_205_500), "2026-03-01");
    struct Plus3;
    impl Calendar for Plus3 {
        fn offset_minutes(&self, _: i64) -> i32 {
            180
        }
    }
    assert_eq!(
        Plus3.day(parse_timestamp("2026-02-28T22:30:00Z").unwrap()),
        "2026-03-01"
    );
    for days in [-1, 0, 59, 365, 11_016, 20_513] {
        assert_eq!(parse_day(&day_text(days)), Some(days));
    }
}

fn cache_of(entries: &[(&str, &str, String)]) -> (TestDir, Cache) {
    let dir = TestDir::new();
    let mut cache = Cache::new();
    for (provider, name, text) in entries {
        let file = dir.write(&format!("projects/p/{name}.jsonl"), text);
        let mut entry = FileEntry {
            provider: provider.to_string(),
            session_id: name.to_string(),
            ..FileEntry::default()
        };
        index(&file, &mut entry);
        cache.files.insert(file.to_string_lossy().into_owned(), entry);
    }
    (dir, cache)
}

#[test]
fn queries() {
    let other = lines(&[
        user("2026-03-02T11:00:00Z", "b"),
        assistant("2026-03-02T11:01:00Z", "x1", "gpt-5", &["Grep"]),
        json!({ "type": "custom-title", "customTitle": "Mine" }).to_string(),
        json!({ "type": "ai-title", "aiTitle": "Not this" }).to_string(),
        user("2026-03-03T11:00:00Z", "c"),
    ]);
    let (_dir, cache) = cache_of(&[("claude", "s1", session()), ("chatgpt", "s2", other)]);

    let summary = summary(&cache, &json!({ "year": 2026 }), "2026-03-04").unwrap();
    assert_eq!(
        summary["totals"],
        json!({ "prompts": 4, "turns": 3, "sessions": 2, "activeMinutes": 6 })
    );
    assert_eq!(summary["days"].as_array().unwrap().len(), 3);
    assert_eq!(
        summary["days"][1],
        json!({ "date": "2026-03-02", "prompts": 2, "turns": 1, "sessions": 1,
        "activeMinutes": 3 })
    );
    assert_eq!(summary["streaks"], json!({ "current": 3, "longest": 3 }));
    assert_eq!(summary["years"], json!(["2026"]));
    let later = super::summary(&cache, &json!({}), "2026-03-10").unwrap();
    assert_eq!(later["streaks"]["current"], 0);
    assert_eq!(later["from"], "2025-03-11");
    assert!(super::summary(&cache, &json!({ "year": "x" }), "2026-03-10").is_err());

    let threads = threads(&cache, &json!({ "limit": 1 })).unwrap();
    assert_eq!(threads["threads"].as_array().unwrap().len(), 1);
    assert_eq!(threads["threads"][0]["sessionId"], "s1");
    assert_eq!(threads["threads"][0]["turns"], 2);
    let all = super::threads(&cache, &json!({})).unwrap();
    assert_eq!(all["threads"][1]["title"], "Mine");
    assert_eq!(all["threads"][1]["titleSource"], "custom");

    let breakdown = breakdown(&cache, &json!({ "year": 2026 }), "2026-03-04").unwrap();
    assert_eq!(breakdown["providers"], json!({ "chatgpt": 1, "claude": 2 }));
    assert_eq!(
        breakdown["models"],
        json!({ "claude-opus-5-5": 1, "claude-sonnet-5": 1, "gpt-5": 1 })
    );
    assert_eq!(
        breakdown["tools"],
        json!({ "bash": 1, "edit": 1, "other": 1, "read": 2, "web": 1 })
    );

    let timeline = timeline(&cache).unwrap();
    assert_eq!(timeline["months"][0]["month"], "2026-03");
    assert_eq!(timeline["months"][0]["sessions"], 2);
    assert_eq!(timeline["months"][0]["folderCount"], 1);
    assert_eq!(timeline["months"][0]["folders"][0]["folder"], "D:\\Work\\alpha");
}

fn wait_for(overview: &Overview, done: impl Fn(&Status) -> bool) -> Status {
    let deadline = Instant::now() + Duration::from_secs(20);
    loop {
        let status = overview.status();
        if done(&status) {
            return status;
        }
        assert!(Instant::now() < deadline, "overview stuck: {status:?}");
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn the_worker_indexes_both_homes_saves_and_resumes_from_its_cache() {
    let dir = TestDir::new();
    dir.write("claude/projects/a/s1.jsonl", session());
    dir.write("claude/projects/a/notes.txt", "not a transcript");
    dir.write(
        "chatgpt/projects/b/s2.jsonl",
        lines(&[user("2026-03-05T10:00:00Z", "x")]),
    );
    let config = Config {
        cache_file: dir.path().join("profile/overview.json"),
        homes: vec![
            Home {
                provider: "claude".into(),
                home: dir.path().join("claude"),
            },
            Home {
                provider: "chatgpt".into(),
                home: dir.path().join("chatgpt"),
            },
        ],
    };
    let overview = Overview::new(Box::new(Utc), || {});
    assert_eq!(overview.status().state, "unconfigured");
    // Busy sessions hold it.
    overview.set_busy(true);
    overview.configure(config.clone());
    let status = wait_for(&overview, |s| s.state == "paused");
    assert!(!status.first_pass_complete);
    overview.set_busy(false);
    let status = wait_for(&overview, |s| s.first_pass_complete && s.state == "idle");
    assert_eq!((status.files_done, status.files_total), (2, 2));
    assert_eq!(overview.query(|c| c.files.len()), 2);
    overview.stop();

    // A new run starts from the cache: only appended bytes are read.
    let saved = Cache::load(&config.cache_file);
    assert!(saved.first_pass_complete);
    assert_eq!(saved.files.len(), 2);
    let mut out = OpenOptions::new()
        .append(true)
        .open(dir.path().join("chatgpt/projects/b/s2.jsonl"))
        .unwrap();
    let added = user("2026-03-06T10:00:00Z", "y");
    writeln!(out, "{added}").unwrap();
    // A start reads again (as each closed day does), only what was appended.
    let again = Overview::new(Box::new(Utc), || {});
    again.configure(config.clone());
    let status = wait_for(&again, |s| {
        s.first_pass_complete && s.state == "idle" && s.files_done == 2
    });
    assert_eq!(status.bytes_read, added.len() as u64 + 1);
    let summary = again
        .query(|c| summary(c, &json!({ "year": 2026 }), "2026-03-06"))
        .unwrap();
    assert_eq!(summary["totals"]["prompts"], 4);
    again.stop();

    // A cache of another schema is dropped.
    std::fs::write(&config.cache_file, r#"{"schema":999,"files":{}}"#).unwrap();
    assert_eq!(Cache::load(&config.cache_file), Cache::new());
}

/// Rough footprint of a full first pass (run with `cargo test -p reframe-transcripts --release -- --ignored
/// --nocapture footprint`): synthetic transcripts, single thread, without the worker's pauses.
#[test]
#[ignore]
fn footprint() {
    let dir = TestDir::new();
    let mut body = String::new();
    for i in 0..400 {
        let minute = format!("2026-03-01T{:02}:{:02}:00Z", 8 + i / 60, i % 60);
        body.push_str(&user(&minute, &"p".repeat(300)));
        body.push('\n');
        body.push_str(&assistant(
            &minute,
            &format!("m{i}"),
            "claude-opus-5-5",
            &["Read", "Bash"],
        ));
        body.push('\n');
        body.push_str(&tool_result(&minute).replace("secret output", &"o".repeat(4000)));
        body.push('\n');
    }
    let files = 200;
    for f in 0..files {
        dir.write(&format!("projects/p{}/s{f}.jsonl", f % 20), &body);
    }
    let homes = [Home {
        provider: "claude".into(),
        home: dir.path().to_path_buf(),
    }];
    let started = Instant::now();
    let mut cache = Cache::new();
    let mut bytes = 0;
    for (provider, session_id, path) in list_transcripts(&homes) {
        let mut entry = FileEntry {
            provider,
            session_id,
            ..FileEntry::default()
        };
        bytes += index_file(&path, &mut entry, &Utc).unwrap().bytes;
        cache.files.insert(path.to_string_lossy().into_owned(), entry);
    }
    let elapsed = started.elapsed();
    let cache_bytes = serde_json::to_vec(&cache).unwrap().len();
    let again = Instant::now();
    let paths: Vec<_> = cache.files.keys().cloned().collect();
    for path in paths {
        let entry = cache.files.get_mut(&path).unwrap();
        assert_eq!(index_file(Path::new(&path), entry, &Utc).unwrap().bytes, 0);
    }
    println!(
        "footprint: {files} transcripts, {} MiB in {:?} ({:.0} MiB/s); cache {} KiB; unchanged refresh {:?}",
        bytes / (1024 * 1024),
        elapsed,
        bytes as f64 / (1024.0 * 1024.0) / elapsed.as_secs_f64(),
        cache_bytes / 1024,
        again.elapsed()
    );
}
