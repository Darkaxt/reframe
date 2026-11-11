# The PTY host protocol

reframe keeps its terminals in a PTY host: a process of its own that owns every claude process (each in a ConPTY)
and a terminal mirror per session, and outlives reframe so that the next reframe takes the sessions over (hot swap,
since 0.10.1-claudegpt.16). There are two implementations of the same contract:

| Host | Source | Executable | Selected by |
| --- | --- | --- | --- |
| Node | `src/ptyhost/index.ts` | `reframe-ptyhost.exe` (reframe's Electron run as Node) | default |
| Rust | `rust/reframe-host` | `reframe-host.exe` | `REFRAME_PTYHOST=rust` |

The contract below is what both implement; `src/ptyhost/host.conformance.ts` (`npm run test:conformance`) runs the same tests against both.

## Files of a profile

The profile's host folder (`<userData>/ptyhost`, `REFRAME_PTYHOST_DIR`) holds:

- `token`: 64 hex digits made by reframe on first use (`ensureToken`), readable by the current user only
  (`icacls /inheritance:r /grant:r <user>:F`, then every other entry removed). The host reads it at start.
- `hosts/<pid>.json`: the descriptor of each running host, written whole (temporary file, then rename):
  `{ pid, pipe, protocol, version, startedAt, sessionPids, kind }`. `sessionPids` lists the running sessions'
  processes; `kind` is `"node"` or `"rust"` (absent from hosts before 0.10.1-claudegpt.18: those are Node hosts).
  A host removes its descriptor when it exits; reframe removes those of hosts that are gone.
- `host.log`: one JSON object per line, `{ at, pid, message, ...details }`.

## Start

reframe starts a host detached (no window, no inherited handles, out of reframe's job where allowed) with
`REFRAME_PTYHOST_DIR` and `REFRAME_PTYHOST_VERSION` (reframe's version) and waits up to 20 s for its descriptor. A
host listens on `\\.\pipe\reframe-ptyhost-<first 16 hex digits of sha256(lower-cased host folder)>-<pid>`, and on
`127.0.0.1:<free port>` for Claude Code's HTTP hooks.

An installed reframe never runs a host from its release folder (an update removes that folder while the host runs):
it copies the host first to a versioned folder, `REFRAME_PTYHOST_ROOT` (the managed launcher sets
`D:\Tools\Reframe\ptyhost`) or `<userData>/ptyhost/runtimes`:

- Node: `<version>-<hash12>` with reframe's executable as `reframe-ptyhost.exe`, its libraries and `app.asar`.
- Rust: `<version>-rust-<hash12>` with `reframe-host.exe`, `conpty.dll` and `OpenConsole.exe`.

Copies whose host executable can be deleted (no host runs from them) are removed when a host is started.

## Messages

One JSON object per line (UTF-8, `\n`), both ways. A line that is not JSON is dropped.

**Handshake.** The first line of a connection must be `{ type: "hello", token, protocol }`. The host answers:

- `{ type: "welcome", protocol, version, pid, hooks: { port, token }, sessions: [{ sessionId, instance, pid,
  exitCode, meta? }], kind }` when the token is the profile's and `protocol` equals the host's (1). `exitCode` is
  null while the session runs. `hooks` is where the sessions' hook settings point, whichever reframe is attached.
  Hook messages held while no reframe was attached follow the welcome.
- `{ type: "refused", reason: "token" | "protocol", protocol }`, and the connection is closed. A first line that
  is not a hello, or has another token, is `token`; the right token with another version is `protocol`.

One reframe per profile: a newly welcomed connection takes over. The previous one gets `{ type: "replaced" }` and is
closed, and every view is closed. Messages of a connection that is not the attached one are ignored.

**Requests** (reframe → host):

| Request | Effect |
| --- | --- |
| `spawn { sessionId, instance, file, args, cwd, env, cols, rows, meta? }` | Ends and forgets a session of that id, then starts `file` in a new ConPTY of `cols`×`rows`. `args` is a list (quoted as node-pty quotes it) or a string (the rest of the command line as it is, for cmd.exe). A relative `file` is looked up in the host's `Path`. `env` is the whole environment, in its order. Answers `spawned { sessionId, instance, pid }` or `spawn-failed { sessionId, instance, message }`. Views already open for the id attach. |
| `write { sessionId, data }` | Input to the session, if it runs. |
| `dispose { sessionId, instance }` | If the session is still that instance: kills its process if it runs, sends `view-closed { viewId }` for its views and forgets it. |
| `view-open { viewId, sessionId }` | Opens a view. If the session exists, the view gets `snapshot`, then live output (and `exit` if it ended); else it waits for the session's spawn. |
| `view-message { viewId, message }` | `{ type: "input", data }`, or `{ type: "resize", cols, rows }` (only if both > 1): resizes the ConPTY and the mirror. Only while the session runs. |
| `view-close { viewId }` | Closes the view. |
| `shutdown` | "End sessions and quit": ends every session, removes the descriptor and exits. |

Unknown requests are ignored.

**Events** (host → reframe): `spawned`, `spawn-failed`, `exit { sessionId, instance, pid, exitCode }` (after the
session's last output), `error { message }` (something failed in the host; it goes on), `hook { message: {
sessionId, event, payload } }`, and for views `view { viewId, message }` where `message` is
`{ type: "snapshot", data, cols, rows }`, `{ type: "data", data }` or `{ type: "exit", exitCode }`, and
`view-closed { viewId }`.

**Snapshots.** `data` is a terminal escape-sequence stream that repaints the mirror into a terminal that was reset
and resized to `cols`×`rows` (the renderer does exactly that): scrollback and screen line by line (a line that wrapped
is left to wrap again), then the cursor position, the current attributes and the input modes (cursor keys, keypad,
bracketed paste, mouse) and a hidden cursor. The Node host's mirror is xterm.js (headless, 5000 lines of scrollback,
Unicode 11 widths) with the serialize addon; the Rust host's is vt100 (5000 lines, Unicode widths) with its own
serializer. Known differences of the Rust mirror: no strikethrough, blink, invisible or overline attributes, and in
the alternate screen only that screen is restored (not the normal one behind it).

## Hooks

`POST /hook/<encodeURIComponent(sessionId)>/<event>` with `X-Reframe-Token: <hooks.token>`, where `event` is
`UserPromptSubmit`, `Stop`, `Notification` or `StatusLine`; the body is Claude Code's JSON (at most 1 MiB). The
answer is `{}` with 200, 404 (not POST or not that path, or another event), 403 (token) or 400 (an id that does not
decode). The hook goes to the attached reframe; without one, up to 200 (status lines excepted) are held for the next.

## Lifetime and quitting

reframe leaves its host in one of two ways (`PtyHostClient.stop`): `keep` closes the connection, and the sessions go
on until the next reframe takes them over; `end` sends `shutdown`. A host with no reframe attached and no session
running for 10 s exits. A connection that closes closes its views. A dead host's sessions are gone; reframe starts
a new host (at most 3 times in a minute).

**Which host a reframe uses.** A reframe attaches to a running host of the profile whose `protocol` it speaks and
whose `kind` is the one it is configured for, and takes its sessions over. A running host of the other kind (or of
another protocol) is left alone: it keeps serving its sessions, which stay out of this reframe, and exits when its
last session ended. New sessions go to the configured host, started if none runs.
