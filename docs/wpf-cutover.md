# Cutover: from the Electron app to the WPF shell (S14)

From this release on, the managed Windows release is the native WPF shell (`wpf/Reframe.Shell`) with the Rust PTY host
(`rust/reframe-host`). Electron stays in the repository and in CI (visual parity, conformance) until one stable week
after the cutover; its removal is a separate PR. The PTY host protocol is unchanged.

## Package

`maintenance/package-shell.ps1 -ShellDir <reframe-shell artifact> -HostDir <reframe-host artifact> -PackageDirectory <new folder>`
assembles `reframe-<version>-win-portable\` and its zip from CI's artifacts (it never builds):

| Path                                                       | What                                                                                     |
| ---------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| `reframe.exe`                                              | the shell's apphost, renamed (the managed scripts and deploy look for `reframe.exe`)     |
| `Reframe.Shell.dll`, runtime, WPF, `Microsoft.Terminal.*`  | the self-contained publish (no .NET install needed) with the patched terminal control    |
| `reframe-host\reframe-host.exe`, `conpty.dll`, `OpenConsole.exe` | the Rust host; the shell starts it from a versioned copy under `REFRAME_PTYHOST_ROOT` |
| `history_backend.py`                                       | the history jobs' backend (the shell looks beside itself first)                          |
| `resources\maintenance\windows\stop-managed.ps1`, `release-files.ps1` | as before: the deploy runs the candidate's stop script from here               |
| `LICENSE`                                                  |                                                                                          |

`reframe.exe`'s file version is the release version (`package.json`), checked by the script and by the deploy. No
Electron file ships (checked). There is no portable data folder: a start without `--profile`, `REFRAME_USER_DATA` or
`reframe-config.json` uses the shell's own test profile.

CI's `wpf` job builds the package and uploads it as `reframe-portable`, and runs the packaged smoke on it.

## What the shell took over from reframe.exe

- **Profile and routes.** `--profile`, else `REFRAME_USER_DATA` (start-managed.ps1 sets it with the CLI bindings, runtime,
  history helper, `REFRAME_PTYHOST_ROOT` and `REFRAME_APP_ID`), else a direct start (autostart, a shortcut to the
  executable) reads `reframe-config.json` beside the executable exactly as `src/main/launch-config.ts` did, verifying
  each binary by sha256; the launcher's variables win. A direct start of a managed release also takes the launcher's app
  id (`Darkaxt.Reframe.<profile>` when `maintenance\start-<profile>.vbs` exists). Same profile folder, same
  `state.json`/`.bak`; the shell reads every Electron-written version (S1 fixtures).
- **`--quit`.** Asks the profile's running shell (single-instance pipe) to quit as its tray's Quit does: state saved,
  sessions left running in the host. Nothing running: exits 0 without a window; no answer in 10 s: exits 1. This is
  the only quit there is, so `stop-managed.ps1` and `-KeepSessions` work unchanged (keep-sessions is the shell's quit).
- **Entry points.** At a managed start (`REFRAME_APP_ID`), the shell keeps the Start menu shortcut
  (`REFRAME_APP_SHORTCUT`) and the installation's `Reframe.lnk` running the launcher VBS with
  `System.AppUserModel.ID` and `System.AppUserModel.ToastActivatorCLSID` = `toastActivatorClsid(app id)` (created when
  missing, the two properties added when lacking), and points an existing login entry named after the app id (the one
  Electron's `setLoginItemSettings` wrote) at itself with `--autostart`; Task Manager's enabled/disabled state is kept.
  Settings → Start with Windows writes that same entry. No HKCU `LocalServer32` registration is written: toast clicks
  reach the running shell through its registered class object; a click while it is not running starts nothing (as
  before). Adding one would go in `Toasts.cs`.
- **Android client listener (out of scope).** A Tailscale network listener would sit beside the single-instance pipe in
  `App.OnStartup` (or as a host service); nothing of it exists yet.

## Packaged smoke

`maintenance/windows/smoke-packaged.ps1 -Package <package folder> -Scratch <new D: folder>` replaces the Electron
Playwright specs the deploy ran. It builds a throwaway managed installation in the scratch folder (Electron-written v1
state, fake claude binding, no launcher VBS so no app id, shortcut or login entry, notifications off) and checks:
start-managed's launch receipt, the process path and file version, a responsive main window, the Rust host started
from the installation's ptyhost root; stop-managed `-KeepSessions` (`--quit`, owned processes exit, `last-stop.json`),
unchanged preferences; a direct start through `reframe-config.json` ended by `reframe.exe --quit`. It writes
`smoke-result.json`, exits 1 on failure and only ever stops processes running from its scratch folder. The deploy runs
it on a hidden desktop (`Invoke-OnHiddenDesktop.ps1`); CI runs it on the runner.

## Deploy changes (outside this repository)

`deploy-reframe.ps1` must: download the `reframe-shell` and `reframe-host` artifacts of the passed CI run and package
with `package-shell.ps1` (no Electron build); run `smoke-packaged.ps1` on the hidden desktop instead of the Playwright
specs; keep the last Electron release (`releases\<v>` and `maintenance\<v>`) out of pruning while it is the rollback
target; verify the shell's main window and launch receipt as before. `-KeepRunning` and `-HotSwap` keep their meaning:
a hot swap quits Electron with its sessions kept (its own stop script, `--quit`) and the shell attaches to the running
host, Node or Rust.

## Rollback to Electron

The cutover activation's receipt folder (`release-<version>\activation\`) holds `rollback.json` (previous executable:
the last Electron release) and a frozen `restore-managed.ps1`.

1. Quit the shell, keeping sessions: `& (Get-Content D:\Tools\Reframe\active.json -Raw | ConvertFrom-Json).stopScript -ActivePath D:\Tools\Reframe\active.json -KeepSessions`.
2. Restore: `& <receipt>\restore-managed.ps1 -Receipt <receipt>\rollback.json -ReceiptSha256 <its sha256> -KeepSessions`.
   With `-KeepSessions` only `active.json` and the launcher are restored (state.json stays as the shell left it,
   same schema) and only a running reframe.exe blocks; without it, Claude work must be idle and the frozen state is
   restored too. A rotated `.credentials.json` is accepted when it is valid JSON.
3. It restarts through the VBS launcher: Electron takes the live host's sessions over on the same profile.
4. Settings → Start with Windows: toggle it in Electron if autostart was on (the entry points at the shell's release).

The rollback needs the Electron release's `releases\<v>` and `maintenance\<v>` folders unchanged (the restore checks the
whole payload), so the deploy keeps them until Electron is retired.
