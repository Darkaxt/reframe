//! The adopted Claude clients (src/main/client-adoption.ts): a `reframe.client-adoption.v1` record names each
//! provider's binary with its sha256, and a binary is used only while its contents match. A file's hash is kept per
//! path, size and modification time.

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, PoisonError};
use std::time::SystemTime;

use serde_json::{json, Value};

use crate::{is_absolute, sha256_file};

pub const SCHEMA: &str = "reframe.client-adoption.v1";

/// What a record gives a provider: `{ ok: true, path, version }` or `{ ok: false, reason }` (AdoptedClient).
#[derive(Debug, Clone, PartialEq)]
pub enum Adopted {
    Client { path: String, version: String },
    Refused(String),
}

impl Adopted {
    pub fn to_json(&self) -> Value {
        match self {
            Adopted::Client { path, version } => json!({ "ok": true, "path": path, "version": version }),
            Adopted::Refused(reason) => json!({ "ok": false, "reason": reason }),
        }
    }
}

/// The hashes worked out so far, by path, while the file keeps its size and modification time.
#[derive(Default)]
pub struct Hashes(Mutex<HashMap<PathBuf, Known>>);

struct Known {
    size: u64,
    modified: Option<SystemTime>,
    sha256: String,
}

impl Hashes {
    pub fn sha256(&self, file: &Path) -> std::io::Result<String> {
        let meta = fs::metadata(file)?;
        let (size, modified) = (meta.len(), meta.modified().ok());
        if let Some(known) = self.0.lock().unwrap_or_else(PoisonError::into_inner).get(file) {
            if known.size == size && known.modified == modified {
                return Ok(known.sha256.clone());
            }
        }
        let hash = sha256_file(file)?;
        self.0.lock().unwrap_or_else(PoisonError::into_inner).insert(
            file.to_path_buf(),
            Known {
                size,
                modified,
                sha256: hash.clone(),
            },
        );
        Ok(hash)
    }
}

/// The provider's adopted client from the record at `record_file`, verified by hash (adoptedClient).
pub fn adopted_client(hashes: &Hashes, record_file: &str, provider: &str) -> Adopted {
    let record: Value = match fs::read(record_file).map(|bytes| {
        let text = String::from_utf8_lossy(&bytes).into_owned();
        // A record written by Windows PowerShell starts with a byte order mark.
        serde_json::from_str(text.strip_prefix('\u{FEFF}').unwrap_or(&text))
    }) {
        Ok(Ok(record)) => record,
        _ => return Adopted::Refused(format!("client adoption record {record_file} cannot be read")),
    };
    if record.get("schema").and_then(Value::as_str) != Some(SCHEMA) {
        return Adopted::Refused(format!("{record_file} is not a client adoption record"));
    }
    let binding = record.get("clients").and_then(|c| c.get(provider));
    let file = binding.and_then(|b| b.get("binary")).and_then(Value::as_str);
    let expected = binding.and_then(|b| b.get("sha256")).and_then(Value::as_str);
    let (Some(file), Some(expected)) = (file.filter(|f| is_absolute(f)), expected) else {
        return Adopted::Refused(format!("no adopted {provider} client in {record_file}"));
    };
    let Ok(actual) = hashes.sha256(Path::new(file)) else {
        return Adopted::Refused(format!("adopted {provider} client {file} cannot be read"));
    };
    if actual != expected.to_lowercase() {
        return Adopted::Refused(format!("adopted {provider} client {file} changed (sha256 {actual})"));
    }
    Adopted::Client {
        path: file.to_string(),
        version: record.get("version").and_then(Value::as_str).unwrap_or("").to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testdir::TestDir;

    fn record(dir: &TestDir, name: &str, body: &str) -> String {
        dir.write(name, body);
        dir.text(name)
    }

    #[test]
    fn verifies_by_hash_with_or_without_a_bom() {
        let dir = TestDir::new();
        let bin = dir.write("claude.exe", "the client");
        let hash = sha256_file(&bin).unwrap();
        let body = json!({ "schema": SCHEMA, "version": "2.1", "clients": { "chatgpt": {
            "binary": bin.to_string_lossy(), "sha256": hash.to_uppercase() } } })
        .to_string();
        let hashes = Hashes::default();
        let expected = Adopted::Client {
            path: bin.to_string_lossy().into_owned(),
            version: "2.1".into(),
        };
        assert_eq!(
            adopted_client(&hashes, &record(&dir, "a.json", &body), "chatgpt"),
            expected
        );
        let bom = record(&dir, "b.json", &format!("\u{FEFF}{body}"));
        assert_eq!(adopted_client(&hashes, &bom, "chatgpt"), expected);
        assert_eq!(
            adopted_client(&hashes, &bom, "anthropic"),
            Adopted::Refused(format!("no adopted anthropic client in {bom}"))
        );

        std::fs::write(&bin, "another client").unwrap();
        let changed = sha256_file(&bin).unwrap();
        assert_eq!(
            adopted_client(&hashes, &bom, "chatgpt"),
            Adopted::Refused(format!(
                "adopted chatgpt client {} changed (sha256 {changed})",
                bin.to_string_lossy()
            ))
        );
    }

    #[test]
    fn refuses_missing_and_corrupt_records() {
        let dir = TestDir::new();
        let missing = dir.text("missing.json");
        assert_eq!(
            adopted_client(&Hashes::default(), &missing, "chatgpt"),
            Adopted::Refused(format!("client adoption record {missing} cannot be read"))
        );
        let corrupt = record(&dir, "corrupt.json", "{\"schema\":");
        assert!(
            matches!(adopted_client(&Hashes::default(), &corrupt, "chatgpt"), Adopted::Refused(r) if r.contains("cannot be read"))
        );
        let other = record(&dir, "other.json", "{\"schema\":\"x\"}");
        assert_eq!(
            adopted_client(&Hashes::default(), &other, "chatgpt"),
            Adopted::Refused(format!("{other} is not a client adoption record"))
        );
    }
}
