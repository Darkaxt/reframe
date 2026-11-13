//! WSL distributions (the `wsl` service), as src/main/wsl/distros.ts and src/shared/wsl.ts handle them: which there
//! are and which run (`wsl.exe -l -q`), what it takes to run claude in one (asked once per distribution through the
//! user's login shell, which also starts it), whether a Linux process is a claude (its `/proc/<pid>/cmdline` through
//! the share) and ending one (Ctrl+C twice, then a termination, never a process that is not a claude), and the
//! conversions between Windows and Linux paths. wsl.exe and the share can be replaced (REFRAME_WSL_EXE,
//! REFRAME_WSL_SHARE, or the request's `command` and `share`) as in reframe's tests.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::Read;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, Instant};

use reframe_sessions::registry::looks_like_claude;
use serde_json::{json, Value};

/// Where Windows shows the distributions; `\\wsl$` is the older name of the same (WSL_SHARES).
pub const WSL_SHARES: [&str; 2] = ["\\\\wsl.localhost", "\\\\wsl$"];
const MARKER: &str = "@@REFRAME@@";
/// execFile's maxBuffer: more output fails the run.
const MAX_OUTPUT: u64 = 1024 * 1024;

/// PROBE_SCRIPT: asks the user's login shell for what reframe needs, between markers.
pub const PROBE_SCRIPT: &str = concat!(
    "s=\"${SHELL:-/bin/sh}\"\n",
    "ask='printf \"\\n@@REFRAME@@\\n%s\\n%s\\n%s\\n%s\\n\" \"$PATH\" \"$(command -v claude)\" \"$HOME\" \"${CLAUDE_CONFIG_DIR:-}\"'\n",
    "out=$(\"$s\" -lic \"$ask\" 2>/dev/null </dev/null)\n",
    "case \"$out\" in *@@REFRAME@@*) ;; *) out=$(sh -c \"$ask\") ;; esac\n",
    "printf '%s\\n' \"$out\" | sed -n '/^@@REFRAME@@$/,$p'\n",
    "printf 'MOUNT=%s\\n' \"$(wslpath -u 'C:\\' 2>/dev/null)\"",
);

/// wsl.exe, or a stand-in (WslCommand): `file` run with `prefix_args` before wsl.exe's own arguments.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct WslCommand {
    pub file: String,
    pub prefix_args: Vec<String>,
    pub env: Vec<(String, String)>,
}

/// wslCommand: REFRAME_WSL_EXE, a script of which runs by REFRAME_NODE; else the system's wsl.exe. None for a script
/// without REFRAME_NODE (reframe would run it by its own executable as node, which the Rust host is not).
pub fn wsl_command(env: impl Fn(&str) -> Option<String>) -> Option<WslCommand> {
    let override_exe = env("REFRAME_WSL_EXE").filter(|s| !s.is_empty());
    if let Some(script) = override_exe.as_ref().filter(|o| {
        let lower = o.to_ascii_lowercase();
        lower.ends_with(".js") || lower.ends_with(".cjs") || lower.ends_with(".mjs")
    }) {
        let node = env("REFRAME_NODE").filter(|s| !s.is_empty())?;
        return Some(WslCommand {
            file: node,
            prefix_args: vec![script.clone()],
            env: Vec::new(),
        });
    }
    let file = override_exe.unwrap_or_else(|| {
        let root = env("SystemRoot").unwrap_or_else(|| "C:\\Windows".to_owned());
        format!("{}\\System32\\wsl.exe", root.trim_end_matches(['\\', '/']))
    });
    Some(WslCommand {
        file,
        prefix_args: Vec::new(),
        env: Vec::new(),
    })
}

/// wslShare: where Windows shows the distributions' files.
pub fn wsl_share(env: impl Fn(&str) -> Option<String>) -> String {
    env("REFRAME_WSL_SHARE")
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| WSL_SHARES[0].to_owned())
}

/// decodeWslOutput: wsl.exe writes UTF-16 unless WSL_UTF8 is set, which older versions ignore.
pub fn decode_wsl_output(output: &[u8]) -> String {
    let utf16 = output.len() >= 2 && output[1] == 0 && output[0] != 0;
    let text = if utf16 {
        let units: Vec<u16> = output
            .chunks(2)
            .map(|c| u16::from_le_bytes([c[0], *c.get(1).unwrap_or(&0)]))
            .collect();
        String::from_utf16_lossy(&units)
    } else {
        String::from_utf8_lossy(output).into_owned()
    };
    text.strip_prefix('\u{FEFF}').map(str::to_owned).unwrap_or(text)
}

/// parseDistroList: distribution names from `wsl.exe -l -q`.
pub fn parse_distro_list(output: &str) -> Vec<String> {
    output
        .split('\n')
        .map(|line| {
            line.strip_suffix('\r')
                .unwrap_or(line)
                .replace('\0', "")
                .trim()
                .to_owned()
        })
        .filter(|line| !line.is_empty())
        .collect()
}

/// `mount.replace(/[^/]+\/*$/, '')`: the mount of C: without its last part.
fn without_last_part(mount: &str) -> String {
    let trimmed = mount.trim_end_matches('/');
    if trimmed.is_empty() {
        return mount.to_owned();
    }
    match trimmed.rfind('/') {
        Some(at) => trimmed[..=at].to_owned(),
        None => String::new(),
    }
}

/// parseProbe: what the probe found (WslInfo as JSON), or None when it did not answer.
pub fn parse_probe(distro: &str, output: &str) -> Option<Value> {
    let output = output.replace('\r', "");
    let lines: Vec<&str> = output.split('\n').collect();
    let at = lines.iter().position(|l| *l == MARKER)?;
    let field = |n: usize| lines.get(at + 1 + n).copied().unwrap_or("");
    let (path_value, claude, home, config_dir) = (field(0), field(1), field(2), field(3));
    if !home.starts_with('/') {
        return None;
    }
    let mount = lines
        .iter()
        .find_map(|l| l.strip_prefix("MOUNT="))
        .filter(|m| !m.is_empty())
        .unwrap_or("/mnt/c/");
    let mut mount_root = without_last_part(mount);
    if mount_root.is_empty() {
        mount_root = "/mnt/".to_owned();
    }
    // The Windows PATH is part of WSL's; a claude found there is the Windows one, which cannot run here.
    let in_distro = claude.starts_with('/') && !claude.starts_with(&mount_root);
    let claude_home = if config_dir.starts_with('/') {
        config_dir.to_owned()
    } else {
        format!("{}/.claude", home.trim_end_matches('/'))
    };
    Some(json!({
        "distro": distro,
        "claude": if in_distro { Value::from(claude) } else { Value::Null },
        "path": path_value,
        "home": home,
        "claudeHome": claude_home,
        "mountRoot": mount_root,
    }))
}

/// A WSL folder: its distribution and absolute Linux path (WslPath).
#[derive(Debug, PartialEq, Eq)]
pub struct WslPath {
    pub distro: String,
    pub path: String,
}

/// parseWslPath: the distribution and Linux path of a folder in WSL, or None for any other folder.
pub fn parse_wsl_path(windows_path: &str, share: Option<&str>) -> Option<WslPath> {
    let normalized = windows_path.replace('/', "\\");
    let roots = share.into_iter().chain(WSL_SHARES);
    for root in roots {
        let prefix = format!("{}\\", root.trim_end_matches(['\\', '/']));
        let count = prefix.chars().count();
        let head: String = normalized.chars().take(count).collect();
        if head.to_lowercase() != prefix.to_lowercase() || head.chars().count() < count {
            continue;
        }
        let rest: String = normalized.chars().skip(count).collect();
        let mut parts = rest.split('\\').filter(|p| !p.is_empty());
        let distro = parts.next()?.to_owned();
        let path = format!("/{}", parts.collect::<Vec<_>>().join("/"));
        return Some(WslPath { distro, path });
    }
    None
}

/// wslWindowsPath: the Windows path of a Linux path in a distribution, under `share`.
pub fn wsl_windows_path(distro: &str, linux_path: &str, share: &str) -> String {
    let mut parts = vec![share.trim_end_matches(['\\', '/']), distro];
    parts.extend(linux_path.split('/').filter(|p| !p.is_empty()));
    parts.join("\\")
}

/// linuxPathOf: a Windows path as programs in `distro` reach it (a drive under the mount root, a folder of the
/// distribution itself as its Linux path); None for what the distribution cannot reach.
pub fn linux_path_of(windows_path: &str, distro: &str, mount_root: &str, share: Option<&str>) -> Option<String> {
    let mut chars = windows_path.chars();
    if let (Some(letter), Some(':')) = (chars.next(), chars.next()) {
        let rest = chars.as_str();
        let drive = letter.is_ascii_alphabetic()
            && (rest.is_empty() || rest.starts_with(['\\', '/']))
            && !rest.contains(['\n', '\r', '\u{2028}', '\u{2029}']);
        if drive {
            let parts: String = rest
                .split(['\\', '/'])
                .filter(|p| !p.is_empty())
                .map(|p| format!("/{p}"))
                .collect();
            return Some(format!(
                "{}/{}{}",
                mount_root.trim_end_matches('/'),
                letter.to_ascii_lowercase(),
                parts
            ));
        }
    }
    parse_wsl_path(windows_path, share)
        .filter(|own| own.distro.to_lowercase() == distro.to_lowercase())
        .map(|own| own.path)
}

/// A distribution's probe: empty until asked, then its answer (None: no answer).
type Probe = Arc<Mutex<Option<Option<Value>>>>;

/// One wsl.exe (or stand-in) and share, with what each distribution answered (as one WslDistros keeps it).
pub struct Distros {
    command: Option<WslCommand>,
    share: String,
    /// The probe of each distribution (by its lower-case name), taken once: a slot is filled by the first asker.
    infos: Mutex<HashMap<String, Probe>>,
    /// The distributions found to have claude, which keep their answer.
    known: Mutex<HashSet<String>>,
    /// The processes that keep distributions running (started from the session list), by lower-case name.
    keepers: Mutex<HashMap<String, Child>>,
}

impl Distros {
    pub fn new(command: Option<WslCommand>, share: String) -> Self {
        Self {
            command,
            share,
            infos: Mutex::default(),
            known: Mutex::default(),
            keepers: Mutex::default(),
        }
    }

    /// keep: starts `distro` and keeps it running (`-e sleep infinity`) until [`Distros::release`], then probes it
    /// afresh (a probe from before this start is not taken as its answer). Kept already: its info.
    pub fn keep(&self, distro: &str) -> Value {
        let key = distro.to_lowercase();
        {
            let mut keepers = self.keepers.lock().unwrap();
            let kept = keepers
                .get_mut(&key)
                .is_some_and(|child| matches!(child.try_wait(), Ok(None)));
            if !kept {
                let Some(command) = self.command.as_ref() else {
                    return Value::Null;
                };
                let mut cmd = Command::new(&command.file);
                cmd.args(&command.prefix_args)
                    .args(["-d", distro, "-e", "sleep", "infinity"])
                    .env("WSL_UTF8", "1")
                    .envs(command.env.iter().map(|(k, v)| (k, v)))
                    .stdin(Stdio::null())
                    .stdout(Stdio::null())
                    .stderr(Stdio::null());
                #[cfg(windows)]
                {
                    use std::os::windows::process::CommandExt;
                    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
                    cmd.creation_flags(CREATE_NO_WINDOW);
                }
                match cmd.spawn() {
                    Ok(child) => {
                        keepers.insert(key.clone(), child);
                    }
                    Err(_) => return Value::Null,
                }
                self.infos.lock().unwrap().remove(&key);
            }
        }
        self.info(distro)
    }

    /// release: lets go of the distributions kept running (reframe quits).
    pub fn release(&self) {
        for (_, mut child) in self.keepers.lock().unwrap().drain() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    /// list: the distributions there are (none without WSL).
    pub fn list(&self) -> Vec<String> {
        parse_distro_list(&self.run(&["-l", "-q"], Duration::from_secs(10)).unwrap_or_default())
    }

    /// running: the distributions that run now, without Docker Desktop's own (asking does not start any). A
    /// distribution that stopped is asked again when it runs again (claude may have been installed since).
    pub fn running(&self) -> Vec<String> {
        let list: Vec<String> = self
            .run(&["-l", "--running", "-q"], Duration::from_secs(10))
            .map(|out| parse_distro_list(&out))
            .unwrap_or_default()
            .into_iter()
            .filter(|name| !name.to_lowercase().starts_with("docker-desktop"))
            .collect();
        let now: HashSet<String> = list.iter().map(|n| n.to_lowercase()).collect();
        let known = self.known.lock().unwrap();
        self.infos
            .lock()
            .unwrap()
            .retain(|key, _| now.contains(key) || known.contains(key));
        list
    }

    /// info: what it takes to run claude in `distro` (starts it); one without claude is taken at its word until it
    /// is seen not running.
    pub fn info(&self, distro: &str) -> Value {
        let key = distro.to_lowercase();
        let slot = self.infos.lock().unwrap().entry(key.clone()).or_default().clone();
        let mut slot = slot.lock().unwrap();
        if slot.is_none() {
            let info = self
                .run(&["-d", distro, "-e", "sh", "-c", PROBE_SCRIPT], Duration::from_secs(60))
                .ok()
                .and_then(|out| parse_probe(distro, &out));
            if info.as_ref().is_some_and(|i| i["claude"].is_string()) {
                self.known.lock().unwrap().insert(key);
            }
            *slot = Some(info);
        }
        slot.clone().flatten().unwrap_or(Value::Null)
    }

    fn proc_file(&self, distro: &str, pid: u32, file: &str) -> PathBuf {
        PathBuf::from(format!(
            "{}\\{}\\proc\\{}\\{}",
            self.share.trim_end_matches(['\\', '/']),
            distro,
            pid,
            file
        ))
    }

    /// alive: whether a Linux process runs in `distro` (as its /proc shows it).
    pub fn alive(&self, distro: &str, pid: u32) -> bool {
        let dir = self.proc_file(distro, pid, "");
        fs::metadata(dir.to_string_lossy().trim_end_matches('\\')).is_ok()
    }

    /// isClaude: whether the Linux process `pid` runs in `distro` and is a claude.
    pub fn is_claude(&self, distro: &str, pid: u32) -> bool {
        let cmdline = fs::read(self.proc_file(distro, pid, "cmdline"))
            .map(|b| String::from_utf8_lossy(&b).into_owned())
            .unwrap_or_default();
        looks_like_claude(&cmdline)
    }

    /// stopClaude: ends a claude running in `distro` (an interrupt, a second one, then a termination); never a
    /// program that merely has the pid of a claude by now.
    pub fn stop_claude(&self, distro: &str, pid: u32) -> &'static str {
        if !self.is_claude(distro, pid) {
            return "failed";
        }
        let pid_text = pid.to_string();
        for signal in ["INT", "INT", "TERM"] {
            let _ = self.run(
                &["-d", distro, "-e", "kill", "-s", signal, &pid_text],
                Duration::from_secs(10),
            );
            for _ in 0..15 {
                if !self.alive(distro, pid) {
                    return "stopped";
                }
                std::thread::sleep(Duration::from_millis(200));
            }
        }
        if self.alive(distro, pid) {
            "failed"
        } else {
            "stopped"
        }
    }

    /// Runs wsl.exe hidden with `WSL_UTF8=1`: its output, decoded; Err when it cannot start, fails, writes more than
    /// 1 MiB or does not exit within `timeout` (then it is killed).
    fn run(&self, args: &[&str], timeout: Duration) -> Result<String, String> {
        let command = self.command.as_ref().ok_or("no wsl.exe")?;
        let mut cmd = Command::new(&command.file);
        cmd.args(&command.prefix_args)
            .args(args)
            .env("WSL_UTF8", "1")
            .envs(command.env.iter().map(|(k, v)| (k, v)))
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            cmd.creation_flags(CREATE_NO_WINDOW);
        }
        let mut child = cmd.spawn().map_err(|e| e.to_string())?;
        let mut stdout = child.stdout.take().ok_or("no output")?;
        let (tx, rx) = mpsc::channel();
        std::thread::spawn(move || {
            let mut output = Vec::new();
            let read = (&mut stdout).take(MAX_OUTPUT + 1).read_to_end(&mut output);
            let _ = tx.send(read.ok().filter(|_| output.len() as u64 <= MAX_OUTPUT).map(|_| output));
        });
        let deadline = Instant::now() + timeout;
        let output = match rx.recv_timeout(timeout) {
            Ok(Some(output)) => output,
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                return Err("no answer".into());
            }
        };
        loop {
            if let Some(status) = child.try_wait().map_err(|e| e.to_string())? {
                return if status.success() {
                    Ok(decode_wsl_output(&output))
                } else {
                    Err(format!("wsl.exe failed ({status})"))
                };
            }
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                return Err("no answer".into());
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}

impl Drop for Distros {
    fn drop(&mut self) {
        self.release();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_list_in_utf16_and_utf8() {
        let utf16: Vec<u8> = "Ubuntu\r\nDebian\r\n"
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect();
        assert_eq!(parse_distro_list(&decode_wsl_output(&utf16)), ["Ubuntu", "Debian"]);
        let utf8 = "\u{FEFF}Ubuntu-24.04\r\n\r\n".as_bytes();
        assert_eq!(parse_distro_list(&decode_wsl_output(utf8)), ["Ubuntu-24.04"]);
        assert!(parse_distro_list("").is_empty());
    }

    fn answer(claude: &str, config_dir: &str) -> String {
        [
            "Welcome to a chatty .bashrc",
            "",
            "@@REFRAME@@",
            "/home/ann/.local/bin:/usr/bin:/mnt/c/Users/Ann/AppData/Roaming/npm",
            claude,
            "/home/ann",
            config_dir,
            "MOUNT=/mnt/c/",
        ]
        .join("\r\n")
    }

    #[test]
    fn parses_the_probe_past_what_the_shell_prints() {
        assert_eq!(
            parse_probe("Ubuntu", &answer("/home/ann/.local/bin/claude", "")).unwrap(),
            json!({
                "distro": "Ubuntu",
                "claude": "/home/ann/.local/bin/claude",
                "path": "/home/ann/.local/bin:/usr/bin:/mnt/c/Users/Ann/AppData/Roaming/npm",
                "home": "/home/ann",
                "claudeHome": "/home/ann/.claude",
                "mountRoot": "/mnt/",
            })
        );
        assert_eq!(
            parse_probe("Ubuntu", &answer("/usr/bin/claude", "/data/claude")).unwrap()["claudeHome"],
            "/data/claude"
        );
        // The Windows claude seen through the PATH does not count, nor a missing one.
        assert!(
            parse_probe("Ubuntu", &answer("/mnt/c/Users/Ann/AppData/Roaming/npm/claude", "")).unwrap()["claude"]
                .is_null()
        );
        assert!(parse_probe("Ubuntu", &answer("", "")).unwrap()["claude"].is_null());
        assert_eq!(parse_probe("Ubuntu", "command not found"), None);
        // Another mount root, and none at all.
        let other = "@@REFRAME@@\n/usr/bin\n/usr/bin/claude\n/root\n\nMOUNT=/drives/c/";
        assert_eq!(parse_probe("X", other).unwrap()["mountRoot"], "/drives/");
        let none = "@@REFRAME@@\n/usr/bin\n\n/root\n\nMOUNT=";
        assert_eq!(parse_probe("X", none).unwrap()["mountRoot"], "/mnt/");
        assert_eq!(parse_probe("X", none).unwrap()["claudeHome"], "/root/.claude");
    }

    #[test]
    fn mount_roots_as_the_regular_expression_cuts_them() {
        for (mount, root) in [
            ("/mnt/c/", "/mnt/"),
            ("/mnt/c", "/mnt/"),
            ("/c//", "/"),
            ("c", ""),
            ("/", "/"),
        ] {
            assert_eq!(without_last_part(mount), root, "{mount}");
        }
    }

    #[test]
    fn the_probe_script_is_reframes() {
        // As src/main/wsl/distros.ts joins PROBE_SCRIPT (printed by node from the TypeScript).
        let expected = "s=\"${SHELL:-/bin/sh}\"\nask='printf \"\\n@@REFRAME@@\\n%s\\n%s\\n%s\\n%s\\n\" \"$PATH\" \"$(command -v claude)\" \"$HOME\" \"${CLAUDE_CONFIG_DIR:-}\"'\nout=$(\"$s\" -lic \"$ask\" 2>/dev/null </dev/null)\ncase \"$out\" in *@@REFRAME@@*) ;; *) out=$(sh -c \"$ask\") ;; esac\nprintf '%s\\n' \"$out\" | sed -n '/^@@REFRAME@@$/,$p'\nprintf 'MOUNT=%s\\n' \"$(wslpath -u 'C:\\' 2>/dev/null)\"";
        assert_eq!(PROBE_SCRIPT, expected);
    }

    fn wp(distro: &str, path: &str) -> Option<WslPath> {
        Some(WslPath {
            distro: distro.into(),
            path: path.into(),
        })
    }

    #[test]
    fn parses_wsl_paths() {
        assert_eq!(
            parse_wsl_path("\\\\wsl.localhost\\Ubuntu\\home\\ann\\shop", None),
            wp("Ubuntu", "/home/ann/shop")
        );
        assert_eq!(parse_wsl_path("\\\\WSL$\\Debian\\srv\\", None), wp("Debian", "/srv"));
        assert_eq!(parse_wsl_path("//wsl.localhost/Ubuntu", None), wp("Ubuntu", "/"));
        assert_eq!(
            parse_wsl_path("C:\\tmp\\share\\Fake\\home\\ann", Some("C:\\tmp\\share")),
            wp("Fake", "/home/ann")
        );
        for other in [
            "C:\\Users\\ann\\shop",
            "\\\\server\\share\\shop",
            "\\\\wsl.localhost\\",
            "\\\\wsl.localhostX\\Ubuntu\\home",
            "\\\\wsl",
        ] {
            assert_eq!(parse_wsl_path(other, None), None, "{other}");
        }
    }

    #[test]
    fn puts_linux_paths_under_the_share() {
        assert_eq!(
            wsl_windows_path("Ubuntu", "/home/ann/shop", "\\\\wsl.localhost"),
            "\\\\wsl.localhost\\Ubuntu\\home\\ann\\shop"
        );
        assert_eq!(
            wsl_windows_path("Ubuntu", "/", "C:\\tmp\\share\\"),
            "C:\\tmp\\share\\Ubuntu"
        );
    }

    #[test]
    fn reaches_drives_and_own_folders_from_linux() {
        assert_eq!(
            linux_path_of("C:\\Users\\Ann\\Pictures\\shot.png", "Ubuntu", "/mnt/", None).as_deref(),
            Some("/mnt/c/Users/Ann/Pictures/shot.png")
        );
        assert_eq!(linux_path_of("D:\\", "Ubuntu", "/mnt", None).as_deref(), Some("/mnt/d"));
        assert_eq!(linux_path_of("E:", "Ubuntu", "/mnt/", None).as_deref(), Some("/mnt/e"));
        assert_eq!(
            linux_path_of("\\\\wsl.localhost\\Ubuntu\\home\\ann\\a b.txt", "ubuntu", "/mnt/", None).as_deref(),
            Some("/home/ann/a b.txt")
        );
        assert_eq!(
            linux_path_of("\\\\wsl.localhost\\Debian\\home\\x", "Ubuntu", "/mnt/", None),
            None
        );
        assert_eq!(linux_path_of("\\\\server\\share\\x", "Ubuntu", "/mnt/", None), None);
        assert_eq!(linux_path_of("C:x", "Ubuntu", "/mnt/", None), None);
    }

    #[test]
    fn finds_wsl_exe_or_its_stand_in() {
        let env = |pairs: &'static [(&'static str, &'static str)]| {
            move |key: &str| pairs.iter().find(|(k, _)| *k == key).map(|(_, v)| (*v).to_owned())
        };
        assert_eq!(
            wsl_command(env(&[("SystemRoot", "D:\\Win")])).unwrap().file,
            "D:\\Win\\System32\\wsl.exe"
        );
        assert_eq!(wsl_command(env(&[])).unwrap().file, "C:\\Windows\\System32\\wsl.exe");
        let fake = wsl_command(env(&[
            ("REFRAME_WSL_EXE", "C:\\t\\fake-wsl.mjs"),
            ("REFRAME_NODE", "C:\\n\\node.exe"),
        ]));
        assert_eq!(
            fake,
            Some(WslCommand {
                file: "C:\\n\\node.exe".into(),
                prefix_args: vec!["C:\\t\\fake-wsl.mjs".into()],
                env: vec![],
            })
        );
        assert_eq!(wsl_command(env(&[("REFRAME_WSL_EXE", "C:\\t\\fake-wsl.mjs")])), None);
        assert_eq!(
            wsl_command(env(&[("REFRAME_WSL_EXE", "C:\\w.exe")])).unwrap().file,
            "C:\\w.exe"
        );
        assert_eq!(wsl_share(env(&[])), "\\\\wsl.localhost");
        assert_eq!(wsl_share(env(&[("REFRAME_WSL_SHARE", "C:\\s")])), "C:\\s");
    }

    #[test]
    fn a_missing_wsl_has_no_distributions() {
        let none = Distros::new(None, "C:\\nowhere".into());
        assert!(none.list().is_empty());
        assert!(none.running().is_empty());
        assert_eq!(none.info("Ubuntu"), Value::Null);
        assert!(!none.is_claude("Ubuntu", 1));
        assert_eq!(none.stop_claude("Ubuntu", 1), "failed");
    }
}
