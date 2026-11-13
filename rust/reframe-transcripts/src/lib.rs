//! Claude Code transcripts (`<home>/projects/<project>/<session id>.jsonl`), read as reframe's TypeScript reads them
//! (src/main/claude/discovery.ts and titles.ts): past sessions newest first, with search and paging, and each
//! session's title. Transcripts can be megabytes, so only their start and end are read, and what was read is cached
//! by modification time. Nothing here watches, polls or writes; the PTY host tells an index what changed.
#![forbid(unsafe_code)]

mod folders;
mod index;
mod info;
mod js;
pub mod overview;
mod titles;

pub use folders::{folder_key, folder_workspaces, workspace_key, FolderLocation, FolderWorkspace};
pub use index::{Changes, ListOptions, Listing, TranscriptIndex};
pub use info::{read_transcript_info, TranscriptInfo};
pub use titles::{latest_title, TranscriptTitles};

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
                "reframe-transcripts-{}-{}",
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
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}
