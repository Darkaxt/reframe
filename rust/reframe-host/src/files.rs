//! The host's files in the profile's host folder: the token it checks (written by reframe), its descriptor
//! (`hosts/<pid>.json`, how a reframe finds it) and its log (`host.log`, one JSON object per line).

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::{json, Map, Value};

use crate::protocol::{HOST_KIND, PROTOCOL_VERSION};

pub struct Files {
    dir: PathBuf,
    descriptor: PathBuf,
    pid: u32,
}

impl Files {
    pub fn new(dir: &Path, pid: u32) -> Self {
        Self {
            dir: dir.to_path_buf(),
            descriptor: dir.join("hosts").join(format!("{pid}.json")),
            pid,
        }
    }

    /// The profile's token (made by reframe before it starts a host).
    pub fn token(&self) -> std::io::Result<String> {
        Ok(fs::read_to_string(self.dir.join("token"))?.trim().to_string())
    }

    /// Appends a line to host.log; there is nowhere to say it if that fails.
    pub fn log(&self, message: &str, details: Value) {
        let mut entry = Map::new();
        entry.insert("at".into(), Value::String(iso_now()));
        entry.insert("pid".into(), json!(self.pid));
        entry.insert("message".into(), Value::String(message.into()));
        if let Value::Object(details) = details {
            entry.extend(details);
        }
        if let Ok(mut file) = OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.dir.join("host.log"))
        {
            let _ = writeln!(file, "{}", Value::Object(entry));
        }
    }

    /// Writes the descriptor whole (a temporary file renamed over it).
    pub fn write_descriptor(
        &self,
        pipe: &str,
        version: &str,
        started_at: u64,
        session_pids: &[u32],
    ) -> std::io::Result<()> {
        let descriptor = json!({
            "pid": self.pid,
            "pipe": pipe,
            "protocol": PROTOCOL_VERSION,
            "version": version,
            "startedAt": started_at,
            "sessionPids": session_pids,
            "kind": HOST_KIND,
        });
        fs::create_dir_all(self.descriptor.parent().unwrap_or(&self.dir))?;
        let temp = self.descriptor.with_extension("json.tmp");
        fs::write(&temp, descriptor.to_string())?;
        fs::rename(&temp, &self.descriptor)
    }

    pub fn remove_descriptor(&self) {
        let _ = fs::remove_file(&self.descriptor);
    }
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_millis() as u64)
}

/// `Date.prototype.toISOString` of now.
pub fn iso_now() -> String {
    iso(now_ms())
}

pub fn iso(ms: u64) -> String {
    let (days, rest) = ((ms / 86_400_000) as i64, ms % 86_400_000);
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:03}Z",
        rest / 3_600_000,
        rest / 60_000 % 60,
        rest / 1000 % 60,
        rest % 1000
    )
}

/// `n` random bytes as hex.
pub fn random_hex(n: usize) -> String {
    let mut bytes = vec![0u8; n];
    getrandom::fill(&mut bytes).expect("the system's random source is available");
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn iso_dates() {
        assert_eq!(iso(0), "1970-01-01T00:00:00.000Z");
        assert_eq!(iso(1_788_998_400_123), "2026-09-10T00:00:00.123Z");
        assert_eq!(iso(951_782_400_000 + 3_723_004), "2000-02-29T01:02:03.004Z");
    }

    #[test]
    fn descriptor_and_log() {
        let dir = std::env::temp_dir().join(format!("reframe-host-files-{}", random_hex(6)));
        fs::create_dir_all(&dir).unwrap();
        let files = Files::new(&dir, 77);
        files.write_descriptor(r"\\.\pipe\x", "1.2.3", 5, &[9, 10]).unwrap();
        let written: Value =
            serde_json::from_str(&fs::read_to_string(dir.join("hosts").join("77.json")).unwrap()).unwrap();
        assert_eq!(
            written,
            json!({"pid":77,"pipe":r"\\.\pipe\x","protocol":1,"version":"1.2.3","startedAt":5,"sessionPids":[9,10],"kind":"rust"})
        );
        files.log("hello", json!({"n": 1}));
        let log: Value = serde_json::from_str(fs::read_to_string(dir.join("host.log")).unwrap().trim()).unwrap();
        assert_eq!(
            (log["message"].as_str(), log["n"].as_i64(), log["pid"].as_i64()),
            (Some("hello"), Some(1), Some(77))
        );
        files.remove_descriptor();
        assert!(!dir.join("hosts").join("77.json").exists());
        fs::remove_dir_all(&dir).unwrap();
    }
}
