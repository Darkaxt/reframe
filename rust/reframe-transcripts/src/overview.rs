//! The Overview's activity index (docs/ptyhost-protocol.md, "overview"): per local day, how many prompts, assistant
//! turns, sessions started and active minutes the Claude Code transcripts hold, with models, tool kinds, threads and
//! folders. It is built from thread activity (transcripts), never from repositories, and it never keeps prompt text.
//!
//! Footprint first: each transcript is read once, streaming, and afterwards only the bytes appended to it (its size and
//! the offset read so far are remembered, with a fingerprint of its start to notice a rewritten file). What was read is
//! kept per transcript and per day in a small JSON cache, so an interrupted pass resumes where it was. The `Overview`
//! worker is one background-priority thread: it yields between files, waits while a session is busy, and after its
//! first pass reads again only once a day (the first idle moment after local midnight) or when it is configured anew.
//! No watchers and no other timers.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::File;
use std::io::{self, BufRead, BufReader, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

/// The cache's schema; a cache of another one is dropped and the index built again.
pub const SCHEMA: u32 = 1;
/// The read buffer of a transcript.
const BUFFER_BYTES: usize = 64 * 1024;
/// A line longer than this is skipped (not parsed): one huge tool result must not take the memory with it.
pub const MAX_LINE_BYTES: usize = 4 * 1024 * 1024;
/// How much of a transcript's start is fingerprinted (a rewritten file starts differently).
const FINGERPRINT_BYTES: u64 = 1024;
/// The cache is saved after this many transcripts read (and at the end of a pass), so a pass can be resumed.
const SAVE_EVERY: usize = 64;
/// The pause between two transcripts.
const YIELD: Duration = Duration::from_millis(5);

/// Activity counts.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Counts {
    pub prompts: u64,
    pub turns: u64,
    pub sessions: u64,
    pub active_minutes: u64,
}

impl Counts {
    fn add(&mut self, other: &Counts) {
        self.prompts += other.prompts;
        self.turns += other.turns;
        self.sessions += other.sessions;
        self.active_minutes += other.active_minutes;
    }

    fn any(&self) -> bool {
        self.prompts + self.turns + self.sessions + self.active_minutes > 0
    }
}

/// One transcript's activity on one local day.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Day {
    #[serde(flatten)]
    pub counts: Counts,
    /// Assistant turns per model.
    #[serde(skip_serializing_if = "BTreeMap::is_empty")]
    pub models: BTreeMap<String, u64>,
    /// Tool uses per kind: edit, bash, read, web, other.
    #[serde(skip_serializing_if = "BTreeMap::is_empty")]
    pub tools: BTreeMap<String, u64>,
}

/// What was read of one transcript.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FileEntry {
    pub provider: String,
    pub session_id: String,
    /// The bytes read: the start of the first line not read yet (an unfinished last line is read later).
    pub offset: u64,
    /// Its start, as `fingerprint_len` bytes hashed (FNV-1a).
    pub fingerprint: u64,
    pub fingerprint_len: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cwd: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    /// `custom` or `ai`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title_source: Option<String>,
    /// Milliseconds since 1970 of its latest prompt or turn.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_active: Option<i64>,
    pub started: bool,
    /// The minute (since 1970) last counted as active, and the assistant message last counted as a turn.
    pub last_minute: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_message: Option<String>,
    /// Lines that were not JSON, or too long.
    pub skipped_lines: u64,
    /// No longer there: its activity is kept (history outlives a deleted transcript).
    pub gone: bool,
    pub days: BTreeMap<String, Day>,
}

/// The cache: every transcript's entry, by path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Cache {
    pub schema: u32,
    /// The local day of the last finished pass.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_pass_day: Option<String>,
    pub first_pass_complete: bool,
    pub files: BTreeMap<String, FileEntry>,
}

impl Default for Cache {
    fn default() -> Self {
        Self {
            schema: SCHEMA,
            last_pass_day: None,
            first_pass_complete: false,
            files: BTreeMap::new(),
        }
    }
}

impl Cache {
    pub fn new() -> Self {
        Self::default()
    }

    /// The cache in `file`, or a new one when it is not there, cannot be read or has another schema.
    pub fn load(file: &Path) -> Self {
        std::fs::read(file)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<Cache>(&bytes).ok())
            .filter(|cache| cache.schema == SCHEMA)
            .unwrap_or_default()
    }

    /// Writes it to `<file>.tmp`, then renames that over `file`.
    pub fn save(&self, file: &Path) -> io::Result<()> {
        if let Some(parent) = file.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let temporary = file.with_extension("json.tmp");
        std::fs::write(&temporary, serde_json::to_vec(self).map_err(io::Error::other)?)?;
        std::fs::rename(&temporary, file)
    }
}

/// Turns a time (milliseconds since 1970, UTC) into its local day, `YYYY-MM-DD`.
pub trait Calendar: Send + Sync {
    /// The local time zone's offset from UTC in minutes at that time.
    fn offset_minutes(&self, utc_ms: i64) -> i32;

    fn day(&self, utc_ms: i64) -> String {
        let local = utc_ms + i64::from(self.offset_minutes(utc_ms)) * 60_000;
        let (y, m, d) = civil(local.div_euclid(86_400_000));
        format!("{y:04}-{m:02}-{d:02}")
    }

    fn today(&self) -> String {
        self.day(now_ms())
    }

    /// How long until the next local midnight.
    fn until_midnight(&self) -> Duration {
        let now = now_ms();
        let local = now + i64::from(self.offset_minutes(now)) * 60_000;
        let left = 86_400_000 - local.rem_euclid(86_400_000);
        Duration::from_millis(left as u64 + 1000)
    }
}

/// UTC days (tests).
pub struct Utc;

impl Calendar for Utc {
    fn offset_minutes(&self, _: i64) -> i32 {
        0
    }
}

/// A calendar from a function (the host passes Windows' time zone).
pub struct OffsetFn(pub fn(i64) -> Option<i32>);

impl Calendar for OffsetFn {
    fn offset_minutes(&self, utc_ms: i64) -> i32 {
        (self.0)(utc_ms).unwrap_or(0)
    }
}

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// Year, month and day of a day number (days since 1970-01-01), Howard Hinnant's civil_from_days.
fn civil(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (yoe + era * 400 + i64::from(m <= 2), m, d)
}

/// Day number of a civil date (days_from_civil).
fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y.rem_euclid(400);
    let m = i64::from(m);
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + i64::from(d) - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

fn parse_day(day: &str) -> Option<i64> {
    let mut parts = day.split('-');
    let y = parts.next()?.parse().ok()?;
    let m = parts.next()?.parse().ok()?;
    let d = parts.next()?.parse().ok()?;
    Some(days_from_civil(y, m, d))
}

fn day_text(days: i64) -> String {
    let (y, m, d) = civil(days);
    format!("{y:04}-{m:02}-{d:02}")
}

/// Milliseconds since 1970 of an ISO time as Claude Code writes it (`2026-11-13T10:04:05.123Z`; an offset
/// `+hh:mm`/`-hh:mm` is taken too).
pub fn parse_timestamp(text: &str) -> Option<i64> {
    let b = text.as_bytes();
    if b.len() < 19 || b[4] != b'-' || b[7] != b'-' || (b[10] != b'T' && b[10] != b' ') {
        return None;
    }
    let num = |range: std::ops::Range<usize>| text.get(range)?.parse::<i64>().ok();
    let (y, mo, d) = (num(0..4)?, num(5..7)?, num(8..10)?);
    let (h, mi, s) = (num(11..13)?, num(14..16)?, num(17..19)?);
    if !(1..=12).contains(&mo) || !(1..=31).contains(&d) || h > 23 || mi > 59 || s > 60 {
        return None;
    }
    let mut rest = &text[19..];
    let mut millis = 0;
    if let Some(fraction) = rest.strip_prefix('.') {
        let digits = fraction.bytes().take_while(u8::is_ascii_digit).count();
        let mut ms = 0;
        for (i, c) in fraction[..digits].bytes().take(3).enumerate() {
            ms += i64::from(c - b'0') * [100, 10, 1][i];
        }
        millis = ms;
        rest = &fraction[digits..];
    }
    let offset = match rest {
        "" | "Z" | "z" => 0,
        _ if rest.len() == 6 && (rest.starts_with('+') || rest.starts_with('-')) => {
            let minutes = rest[1..3].parse::<i64>().ok()? * 60 + rest[4..6].parse::<i64>().ok()?;
            if rest.starts_with('-') {
                -minutes
            } else {
                minutes
            }
        }
        _ => return None,
    };
    let days = days_from_civil(y, mo as u32, d as u32);
    Some(((days * 24 + h) * 60 + mi - offset) * 60_000 + s * 1000 + millis)
}

/// The kind of a tool by its name.
pub fn tool_kind(name: &str) -> &'static str {
    match name {
        "Edit" | "MultiEdit" | "Write" | "NotebookEdit" => "edit",
        "Bash" | "BashOutput" | "KillShell" | "KillBash" | "PowerShell" => "bash",
        "Read" | "Glob" | "Grep" | "LS" | "NotebookRead" => "read",
        "WebFetch" | "WebSearch" => "web",
        _ => "other",
    }
}

fn fnv(bytes: &[u8]) -> u64 {
    bytes.iter().fold(0xcbf2_9ce4_8422_2325, |hash, b| {
        (hash ^ u64::from(*b)).wrapping_mul(0x0100_0000_01b3)
    })
}

fn read_start(file: &mut File, length: u64) -> io::Result<Vec<u8>> {
    let mut start = Vec::with_capacity(length as usize);
    file.seek(SeekFrom::Start(0))?;
    file.take(length).read_to_end(&mut start)?;
    Ok(start)
}

/// What reading a transcript did.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ReadOutcome {
    pub bytes: u64,
    /// It was shorter than read before or started differently: read again from the start.
    pub rewritten: bool,
}

/// Reads what was appended to `path` since `entry` was last brought up to date (all of it if it was rewritten).
pub fn index_file(path: &Path, entry: &mut FileEntry, calendar: &dyn Calendar) -> io::Result<ReadOutcome> {
    let mut file = File::open(path)?;
    let size = file.metadata()?.len();
    let mut outcome = ReadOutcome::default();
    if entry.offset > 0 {
        let start = read_start(&mut file, entry.fingerprint_len.min(size))?;
        if size < entry.offset || start.len() as u64 != entry.fingerprint_len || fnv(&start) != entry.fingerprint {
            let (provider, session_id) = (
                std::mem::take(&mut entry.provider),
                std::mem::take(&mut entry.session_id),
            );
            *entry = FileEntry {
                provider,
                session_id,
                ..FileEntry::default()
            };
            outcome.rewritten = true;
        }
    }
    entry.gone = false;
    if size == entry.offset {
        return Ok(outcome);
    }
    file.seek(SeekFrom::Start(entry.offset))?;
    let mut reader = BufReader::with_capacity(BUFFER_BYTES, file.take(size - entry.offset));
    let mut line = Vec::new();
    loop {
        line.clear();
        let (consumed, complete, too_long) = read_line(&mut reader, &mut line)?;
        if !complete {
            break; // the end, or a line still being written: read later
        }
        entry.offset += consumed;
        outcome.bytes += consumed;
        if too_long {
            entry.skipped_lines += 1;
            continue;
        }
        let text = line.strip_suffix(b"\n").unwrap_or(&line);
        let text = text.strip_suffix(b"\r").unwrap_or(text);
        let text = text.strip_prefix("\u{feff}".as_bytes()).unwrap_or(text);
        if text.iter().all(u8::is_ascii_whitespace) {
            continue;
        }
        match serde_json::from_slice::<Value>(text) {
            Ok(value) => take_line(entry, &value, calendar),
            Err(_) => entry.skipped_lines += 1,
        }
    }
    if entry.fingerprint_len < FINGERPRINT_BYTES.min(entry.offset) {
        let mut file = reader.into_inner().into_inner();
        let start = read_start(&mut file, FINGERPRINT_BYTES.min(entry.offset))?;
        entry.fingerprint_len = start.len() as u64;
        entry.fingerprint = fnv(&start);
    }
    Ok(outcome)
}

/// One line into `line` (at most MAX_LINE_BYTES of it kept): (bytes consumed, ended by a newline, too long).
fn read_line(reader: &mut impl BufRead, line: &mut Vec<u8>) -> io::Result<(u64, bool, bool)> {
    let mut consumed = 0u64;
    let mut too_long = false;
    loop {
        let available = reader.fill_buf()?;
        if available.is_empty() {
            return Ok((consumed, false, too_long));
        }
        let (chunk, done) = match available.iter().position(|b| *b == b'\n') {
            Some(at) => (&available[..=at], true),
            None => (available, false),
        };
        let length = chunk.len();
        if !too_long && line.len() + length <= MAX_LINE_BYTES {
            line.extend_from_slice(chunk);
        } else {
            too_long = true;
            line.clear();
        }
        reader.consume(length);
        consumed += length as u64;
        if done {
            return Ok((consumed, true, too_long));
        }
    }
}

fn is_prompt(message: Option<&Value>) -> bool {
    match message.and_then(|m| m.get("content")) {
        Some(Value::String(text)) => {
            let text = text.trim_start();
            !text.is_empty()
                && !text.starts_with("<local-command-stdout>")
                && !text.starts_with("<local-command-stderr>")
        }
        Some(Value::Array(items)) => {
            let kind = |item: &Value| item.get("type").and_then(Value::as_str).map(str::to_owned);
            let kinds: Vec<_> = items.iter().filter_map(kind).collect();
            !kinds.iter().any(|k| k == "tool_result") && kinds.iter().any(|k| k == "text" || k == "image")
        }
        _ => false,
    }
}

fn take_line(entry: &mut FileEntry, line: &Value, calendar: &dyn Calendar) {
    let text = |key: &str| line.get(key).and_then(Value::as_str);
    if entry.cwd.is_none() {
        if let Some(cwd) = text("cwd").filter(|c| !c.is_empty()) {
            entry.cwd = Some(cwd.to_owned());
        }
    }
    match text("type") {
        Some("custom-title") => {
            if let Some(title) = text("customTitle") {
                entry.title = Some(title.to_owned());
                entry.title_source = Some("custom".into());
            }
            return;
        }
        Some("ai-title") => {
            if let (Some(title), false) = (text("aiTitle"), entry.title_source.as_deref() == Some("custom")) {
                entry.title = Some(title.to_owned());
                entry.title_source = Some("ai".into());
            }
            return;
        }
        Some("user") | Some("assistant") => {}
        _ => return,
    }
    if line.get("isSidechain").and_then(Value::as_bool) == Some(true)
        || line.get("isMeta").and_then(Value::as_bool) == Some(true)
    {
        return;
    }
    let Some(at) = text("timestamp").and_then(parse_timestamp) else {
        return;
    };
    let message = line.get("message");
    let assistant = text("type") == Some("assistant");
    if assistant {
        let id = message.and_then(|m| m.get("id")).and_then(Value::as_str);
        let new_turn = id.is_none() || id != entry.last_message.as_deref();
        let day = entry.days.entry(calendar.day(at)).or_default();
        if new_turn {
            day.counts.turns += 1;
            if let Some(model) = message
                .and_then(|m| m.get("model"))
                .and_then(Value::as_str)
                .filter(|m| !m.is_empty() && !m.starts_with('<'))
            {
                *day.models.entry(model.to_owned()).or_default() += 1;
            }
            entry.last_message = id.map(str::to_owned);
        }
        if let Some(Value::Array(items)) = message.and_then(|m| m.get("content")) {
            for item in items {
                if item.get("type").and_then(Value::as_str) == Some("tool_use") {
                    let name = item.get("name").and_then(Value::as_str).unwrap_or("");
                    *day.tools.entry(tool_kind(name).to_owned()).or_default() += 1;
                }
            }
        }
    } else if is_prompt(message) {
        entry.days.entry(calendar.day(at)).or_default().counts.prompts += 1;
    } else {
        return; // a tool result
    }
    let day = entry.days.entry(calendar.day(at)).or_default();
    if !entry.started {
        entry.started = true;
        day.counts.sessions += 1;
    }
    let minute = at.div_euclid(60_000);
    if minute != entry.last_minute {
        entry.last_minute = minute;
        day.counts.active_minutes += 1;
    }
    entry.last_active = Some(entry.last_active.map_or(at, |last| last.max(at)));
}

// Queries: everything is summed from the per-transcript entries when asked (a few thousand of them).

/// Days of every transcript summed.
fn all_days(cache: &Cache) -> BTreeMap<&str, Counts> {
    let mut days: BTreeMap<&str, Counts> = BTreeMap::new();
    for entry in cache.files.values() {
        for (day, counts) in &entry.days {
            days.entry(day.as_str()).or_default().add(&counts.counts);
        }
    }
    days
}

fn in_range(day: &str, from: &str, to: &str) -> bool {
    day >= from && day <= to
}

/// The days shown: a calendar `year`, or the year up to `today`.
fn range(year: Option<i64>, today: &str) -> (String, String) {
    match year {
        Some(y) => (format!("{y:04}-01-01"), format!("{y:04}-12-31")),
        None => {
            let end = parse_day(today).unwrap_or(0);
            (day_text(end - 364), today.to_owned())
        }
    }
}

fn year_param(params: &Value) -> Result<Option<i64>, String> {
    match params.get("year") {
        None | Some(Value::Null) => Ok(None),
        Some(v) => v
            .as_i64()
            .filter(|y| (1970..=9999).contains(y))
            .map(Some)
            .ok_or("year must be a year".into()),
    }
}

/// `overview.summary`: per day of the range, totals, streaks (days in a row with a prompt) and the years there are.
pub fn summary(cache: &Cache, params: &Value, today: &str) -> Result<Value, String> {
    let (from, to) = range(year_param(params)?, today);
    let days = all_days(cache);
    let mut totals = Counts::default();
    let mut list = Vec::new();
    for (day, counts) in days.range(from.as_str()..=to.as_str()) {
        if counts.any() {
            totals.add(counts);
            list.push(json!({ "date": day, "prompts": counts.prompts, "turns": counts.turns,
                "sessions": counts.sessions, "activeMinutes": counts.active_minutes }));
        }
    }
    let active: Vec<i64> = days
        .iter()
        .filter(|(_, c)| c.prompts > 0)
        .filter_map(|(d, _)| parse_day(d))
        .collect();
    let (mut longest, mut run, mut previous) = (0u64, 0u64, None::<i64>);
    for &day in &active {
        run = if previous == Some(day - 1) { run + 1 } else { 1 };
        longest = longest.max(run);
        previous = Some(day);
    }
    let today_n = parse_day(today).unwrap_or(0);
    let current = match previous {
        Some(last) if last == today_n || last == today_n - 1 => run,
        _ => 0,
    };
    let years: BTreeSet<&str> = days.keys().filter_map(|d| d.get(0..4)).collect();
    Ok(json!({
        "from": from, "to": to, "days": list,
        "totals": totals,
        "streaks": { "current": current, "longest": longest },
        "years": years.into_iter().rev().collect::<Vec<_>>(),
    }))
}

/// `overview.threads`: the most active threads (most turns), at most `limit` (10).
pub fn threads(cache: &Cache, params: &Value) -> Result<Value, String> {
    let limit = match params.get("limit") {
        None | Some(Value::Null) => 10,
        Some(v) => v
            .as_u64()
            .filter(|n| *n <= 1000)
            .ok_or("limit must be a number up to 1000")? as usize,
    };
    let mut items: Vec<(u64, Option<i64>, Value)> = cache
        .files
        .values()
        .filter_map(|entry| {
            let mut counts = Counts::default();
            entry.days.values().for_each(|d| counts.add(&d.counts));
            if !counts.any() {
                return None;
            }
            Some((
                counts.turns,
                entry.last_active,
                json!({ "sessionId": entry.session_id, "provider": entry.provider, "title": entry.title,
                    "titleSource": entry.title_source, "folder": entry.cwd, "turns": counts.turns,
                    "prompts": counts.prompts, "activeMinutes": counts.active_minutes,
                    "lastActive": entry.last_active, "gone": entry.gone }),
            ))
        })
        .collect();
    items.sort_by(|a, b| b.0.cmp(&a.0).then(b.1.cmp(&a.1)));
    items.truncate(limit);
    Ok(json!({ "threads": items.into_iter().map(|(_, _, v)| v).collect::<Vec<_>>() }))
}

/// `overview.breakdown`: assistant turns per provider and model, tool uses per kind, in the range.
pub fn breakdown(cache: &Cache, params: &Value, today: &str) -> Result<Value, String> {
    let (from, to) = range(year_param(params)?, today);
    let mut providers: BTreeMap<&str, u64> = BTreeMap::new();
    let mut models: BTreeMap<&str, u64> = BTreeMap::new();
    let mut tools: BTreeMap<&str, u64> = ["edit", "bash", "read", "web", "other"].map(|k| (k, 0)).into();
    for entry in cache.files.values() {
        for (_, data) in entry.days.iter().filter(|(d, _)| in_range(d, &from, &to)) {
            *providers.entry(&entry.provider).or_default() += data.counts.turns;
            for (model, n) in &data.models {
                *models.entry(model).or_default() += n;
            }
            for (kind, n) in &data.tools {
                *tools.entry(kind).or_default() += n;
            }
        }
    }
    providers.retain(|_, n| *n > 0);
    Ok(json!({ "from": from, "to": to, "providers": providers, "models": models, "tools": tools }))
}

/// `overview.timeline`: per month (newest first), sessions started, prompts, turns and active minutes, per folder.
pub fn timeline(cache: &Cache) -> Result<Value, String> {
    #[derive(Default)]
    struct Month<'a> {
        counts: Counts,
        folders: BTreeMap<&'a str, Counts>,
    }
    let mut months: BTreeMap<&str, Month> = BTreeMap::new();
    for entry in cache.files.values() {
        let folder = entry.cwd.as_deref().unwrap_or("");
        for (day, data) in &entry.days {
            let month = months.entry(&day[..day.len().min(7)]).or_default();
            month.counts.add(&data.counts);
            month.folders.entry(folder).or_default().add(&data.counts);
        }
    }
    let list: Vec<Value> = months
        .iter()
        .rev()
        .map(|(month, data)| {
            let mut folders: Vec<_> = data.folders.iter().collect();
            folders.sort_by(|a, b| b.1.turns.cmp(&a.1.turns).then(a.0.cmp(b.0)));
            let folders: Vec<Value> = folders
                .into_iter()
                .map(|(folder, c)| {
                    json!({ "folder": if folder.is_empty() { Value::Null } else { json!(folder) },
                        "sessions": c.sessions, "prompts": c.prompts, "turns": c.turns,
                        "activeMinutes": c.active_minutes })
                })
                .collect();
            json!({ "month": month, "sessions": data.counts.sessions, "prompts": data.counts.prompts,
                "turns": data.counts.turns, "activeMinutes": data.counts.active_minutes,
                "folderCount": data.folders.len(), "folders": folders })
        })
        .collect();
    Ok(json!({ "months": list }))
}

// The worker.

/// A Claude Code folder to index, and which provider's it is.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Home {
    pub provider: String,
    pub home: PathBuf,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Config {
    pub cache_file: PathBuf,
    pub homes: Vec<Home>,
}

/// Where the worker is.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    /// `unconfigured`, `indexing`, `paused` (a session is busy), `idle` (until the next local day).
    pub state: &'static str,
    pub first_pass_complete: bool,
    pub files_done: u64,
    pub files_total: u64,
    pub bytes_read: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_pass_day: Option<String>,
    /// How long (wall time, milliseconds) the last finished pass took, pauses included.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_pass_ms: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_error: Option<String>,
}

struct State {
    config: Option<Config>,
    generation: u64,
    cache: Cache,
    status: Status,
    busy: bool,
    pass_wanted: bool,
    stop: bool,
    worker: bool,
}

struct Inner {
    state: Mutex<State>,
    wake: Condvar,
    calendar: Box<dyn Calendar>,
    on_start: fn(),
}

/// The indexer: a cache in memory, kept up to date by one background thread, asked by the `overview` service.
#[derive(Clone)]
pub struct Overview {
    inner: Arc<Inner>,
}

impl Overview {
    /// `on_start` runs first on the worker's thread (the host lowers its priority there).
    pub fn new(calendar: Box<dyn Calendar>, on_start: fn()) -> Self {
        Self {
            inner: Arc::new(Inner {
                state: Mutex::new(State {
                    config: None,
                    generation: 0,
                    cache: Cache::new(),
                    status: Status {
                        state: "unconfigured",
                        ..Status::default()
                    },
                    busy: false,
                    pass_wanted: false,
                    stop: false,
                    worker: false,
                }),
                wake: Condvar::new(),
                calendar,
                on_start,
            }),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        self.inner.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Sets the cache file and the homes; a new configuration loads its cache and starts a pass (the same one again
    /// changes nothing). Starts the worker the first time.
    pub fn configure(&self, config: Config) -> Status {
        let mut state = self.lock();
        if state.config.as_ref() != Some(&config) {
            state.cache = Cache::load(&config.cache_file);
            state.status.first_pass_complete = state.cache.first_pass_complete;
            state.status.last_pass_day = state.cache.last_pass_day.clone();
            state.config = Some(config);
            state.generation += 1;
            state.pass_wanted = true;
            state.status.state = "indexing";
            if !state.worker {
                state.worker = true;
                let me = self.clone();
                std::thread::Builder::new()
                    .name("overview".into())
                    .spawn(move || me.run())
                    .expect("overview thread");
            }
            self.inner.wake.notify_all();
        }
        state.status.clone()
    }

    /// Whether a session is busy: the worker waits while one is.
    pub fn set_busy(&self, busy: bool) {
        let mut state = self.lock();
        if state.busy != busy {
            state.busy = busy;
            self.inner.wake.notify_all();
        }
    }

    /// Ends the worker (after the transcript it is reading).
    pub fn stop(&self) {
        self.lock().stop = true;
        self.inner.wake.notify_all();
    }

    pub fn status(&self) -> Status {
        self.lock().status.clone()
    }

    pub fn today(&self) -> String {
        self.inner.calendar.today()
    }

    /// Runs `query` on the cache as it is.
    pub fn query<T>(&self, query: impl FnOnce(&Cache) -> T) -> T {
        query(&self.lock().cache)
    }

    fn run(self) {
        (self.inner.on_start)();
        loop {
            let mut state = self.lock();
            loop {
                if state.stop {
                    return;
                }
                let today = self.inner.calendar.today();
                if state.cache.first_pass_complete && state.cache.last_pass_day.as_deref() != Some(&today) {
                    state.pass_wanted = true; // a day closed
                }
                if state.pass_wanted && !state.busy {
                    break;
                }
                state.status.state = if state.pass_wanted { "paused" } else { "idle" };
                // The one timer: wake at the next local midnight (or when configured, unbusy or stopped).
                let wait = self.inner.calendar.until_midnight();
                state = self
                    .inner
                    .wake
                    .wait_timeout(state, wait)
                    .unwrap_or_else(|e| e.into_inner())
                    .0;
            }
            state.pass_wanted = false;
            state.status.state = "indexing";
            let Some(config) = state.config.clone() else { continue };
            let generation = state.generation;
            drop(state);
            self.pass(&config, generation);
        }
    }

    /// Reads every transcript of the homes (only what was appended, for those read before).
    fn pass(&self, config: &Config, generation: u64) {
        let started = Instant::now();
        let files = list_transcripts(&config.homes);
        {
            let mut state = self.lock();
            state.status.files_total = files.len() as u64;
            state.status.files_done = 0;
            state.status.bytes_read = 0;
            state.status.last_error = None;
        }
        let mut since_save = 0;
        for (provider, session_id, path) in &files {
            // Wait while a session is busy (told when that changes), or give up if configured anew or stopped.
            let mut state = self.lock();
            while state.busy && !state.stop && state.generation == generation {
                state.status.state = "paused";
                state = self.inner.wake.wait(state).unwrap_or_else(|e| e.into_inner());
            }
            if state.stop || state.generation != generation {
                return;
            }
            state.status.state = "indexing";
            let key = path.to_string_lossy().into_owned();
            let mut entry = state.cache.files.get(&key).cloned().unwrap_or_else(|| FileEntry {
                provider: provider.clone(),
                session_id: session_id.clone(),
                ..FileEntry::default()
            });
            drop(state);
            let outcome = index_file(path, &mut entry, self.inner.calendar.as_ref());
            let mut state = self.lock();
            if state.generation != generation {
                return;
            }
            match outcome {
                Ok(read) => {
                    state.status.bytes_read += read.bytes;
                    if read.bytes > 0 || read.rewritten {
                        since_save += 1;
                    }
                    state.cache.files.insert(key, entry);
                }
                Err(error) => state.status.last_error = Some(format!("{}: {error}", path.display())),
            }
            state.status.files_done += 1;
            if since_save >= SAVE_EVERY {
                since_save = 0;
                self.save(&mut state, config);
            }
            drop(state);
            std::thread::sleep(YIELD);
        }
        let mut state = self.lock();
        if state.generation != generation {
            return;
        }
        let present: BTreeSet<String> = files.iter().map(|(_, _, p)| p.to_string_lossy().into_owned()).collect();
        for (key, entry) in state.cache.files.iter_mut() {
            entry.gone = !present.contains(key);
        }
        let today = self.inner.calendar.today();
        state.cache.last_pass_day = Some(today.clone());
        state.cache.first_pass_complete = true;
        state.status.first_pass_complete = true;
        state.status.last_pass_day = Some(today);
        state.status.last_pass_ms = Some(started.elapsed().as_millis() as u64);
        self.save(&mut state, config);
    }

    fn save(&self, state: &mut State, config: &Config) {
        if let Err(error) = state.cache.save(&config.cache_file) {
            state.status.last_error = Some(format!("{}: {error}", config.cache_file.display()));
        }
    }
}

/// Every `<home>/projects/<project>/<session id>.jsonl`: (provider, session id, path), sorted by path.
pub fn list_transcripts(homes: &[Home]) -> Vec<(String, String, PathBuf)> {
    let mut files = Vec::new();
    for home in homes {
        let Ok(projects) = std::fs::read_dir(home.home.join("projects")) else {
            continue;
        };
        for project in projects.flatten() {
            let Ok(entries) = std::fs::read_dir(project.path()) else {
                continue;
            };
            for file in entries.flatten() {
                let path = file.path();
                let is_file = file.file_type().map(|t| t.is_file()).unwrap_or(false);
                if !is_file || path.extension().and_then(|e| e.to_str()) != Some("jsonl") {
                    continue;
                }
                let Some(stem) = path.file_stem().and_then(|s| s.to_str()) else {
                    continue;
                };
                files.push((home.provider.clone(), stem.to_owned(), path));
            }
        }
    }
    files.sort_by(|a, b| a.2.cmp(&b.2));
    files
}

#[cfg(test)]
mod tests;
