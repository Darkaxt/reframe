//! What the PTY host's `quota`, `clients` and `launch` services read (docs/ptyhost-protocol.md, "Services"), as
//! reframe's TypeScript reads it: the Codex quota from LLM Coordinator's native client and whether the user's Claude
//! Code settings have a status line (src/main/quota.ts), the adopted clients verified by hash
//! (src/main/client-adoption.ts) and the direct start's reframe-config.json (src/main/launch-config.ts). Nothing here
//! writes or changes the environment; reframe applies what it is told.
#![forbid(unsafe_code)]

pub mod adoption;
pub mod launch;
pub mod quota;

use std::fs;
use std::path::Path;

use sha2::{Digest, Sha256};

/// Whether Node's `path.win32.isAbsolute` takes `path` to be absolute: rooted (`\x`, `/x`, `\\server\share`) or a
/// drive letter with a separator (`C:\x`, `C:/x`).
pub fn is_absolute(path: &str) -> bool {
    let b = path.as_bytes();
    match b.first() {
        Some(b'\\' | b'/') => true,
        Some(c) if c.is_ascii_alphabetic() => b.len() >= 3 && b[1] == b':' && matches!(b[2], b'\\' | b'/'),
        _ => false,
    }
}

/// The lower-case hex sha256 of a file's contents.
pub fn sha256_file(file: &Path) -> std::io::Result<String> {
    let digest = Sha256::digest(fs::read(file)?);
    Ok(digest.iter().map(|b| format!("{b:02x}")).collect())
}

#[cfg(test)]
pub(crate) mod testdir {
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A folder of its own under the system's temporary folder, removed when dropped.
    pub struct TestDir(pub PathBuf);

    impl TestDir {
        pub fn new() -> Self {
            static NEXT: AtomicU32 = AtomicU32::new(0);
            let dir = std::env::temp_dir().join(format!(
                "reframe-services-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        pub fn write(&self, relative: &str, content: impl AsRef<[u8]>) -> PathBuf {
            let file = self.0.join(relative);
            std::fs::create_dir_all(file.parent().unwrap()).unwrap();
            std::fs::write(&file, content).unwrap();
            file
        }

        pub fn path(&self) -> &Path {
            &self.0
        }

        pub fn text(&self, relative: &str) -> String {
            self.0.join(relative).to_string_lossy().into_owned()
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn absolute_as_node_win32_says() {
        for p in ["C:\\x", "c:/x", "\\x", "/x", "\\\\server\\share"] {
            assert!(is_absolute(p), "{p}");
        }
        for p in ["", "C:", "C:x", "x\\y", "1:\\x", "./x"] {
            assert!(!is_absolute(p), "{p}");
        }
    }
}
