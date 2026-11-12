# WPF shell smoke test in Windows Sandbox

A UI smoke test of the experimental WPF shell on a clean Windows: a disposable Windows Sandbox guest.

```sh
python -I wpf/tests/sandbox-smoke/run.py --shell <reframe-shell dir> --host <reframe-host dir> --scratch D:/Temp/<task>/smoke
```

- `--shell`: the CI artifact `reframe-shell` (a self-contained `dotnet publish -r win-x64`).
- `--host`: the CI artifact `reframe-host` (`reframe-host.exe` with a static CRT, `conpty.dll` and `OpenConsole.exe`).
- `--scratch`: a new folder; it gets `work/` (the guest's `C:\Coordinator\Work`: logs, collected evidence and the
  screenshots `shot1-attached.png`, `shot2-tab-open.png`, `shot3-input.png`), `session.jsonl` (the gate session) and
  `results.json`.

It exits 0 only when every check passed:

1. the host starts on guest disk and a `cmd.exe` session (`smoke-1`) is created over its pipe;
2. the shell starts as the logged-in guest user, its window exists and the session row's automation name is the
   session's label;
3. Enter opens the tab, keyboard focus lands in the terminal ("Text Area"), and the tab and its close button have
   accessible names;
4. typed input (OS-level input, SendInput) round-trips: `set /a 6*7+1000` answers `1042`.

## Prerequisites

- Windows Sandbox enabled, and LLM Coordinator installed with its Windows Sandbox gate
  (`%LOCALAPPDATA%\Tools\llm-coordinator\bin\coordinator-client.exe`) and its pinned WinApp CLI.
- Python 3.10 or later.

## How it runs

Everything goes through one Coordinator sandbox gate session (`gate run --tool windows-sandbox`, JSON lines), which
owns the guest, queues behind other RAM-intensive work and disposes of the guest at the end. `workflow.end` is always
sent, also after a failed check. Nothing runs on the host desktop: the shell needs a connected desktop and the user's
session, and `sandbox.connect` opens the Sandbox client minimised; never restore or focus it.

Notes:

- The profile stays on guest disk (`C:\ShellTest`): mapped folders support neither ACLs nor change notifications.
- `sandbox.powershell` runs as SYSTEM, so `guest/Start-Host.ps1` sets `USERDOMAIN=NT AUTHORITY` for the files reframe
  grants to the user. The gate wants the script's sha256 in lower case.
- The terminal ignores posted window messages; input after focus uses `--via send-input`.
