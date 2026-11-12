# The PTY host protocol

reframe keeps its terminals in a PTY host: a process of its own that owns every claude process (each in a ConPTY)
and a terminal mirror per session, and outlives reframe so that the next reframe takes the sessions over (hot swap,
since 0.10.1-claudegpt.16). There are two implementations of the same contract:

| Host | Source | Executable | Selected by |
| --- | --- | --- | --- |
| Node | `src/ptyhost/index.ts` | `reframe-ptyhost.exe` (reframe's Electron run as Node) | default |
| Rust | `rust/reframe-host` | `reframe-host.exe` | Settings > Terminal host, or `REFRAME_PTYHOST=rust` (overrides it) |

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
  exitCode, meta? }], kind, services? }` when the token is the profile's and `protocol` equals the host's (1). `exitCode` is
  null while the session runs. `hooks` is where the sessions' hook settings point, whichever reframe is attached.
  Hook messages held while no reframe was attached follow the welcome. `services` (since 0.10.1-claudegpt.20) names
  the services the host offers with each one's version, `{ transcripts: 1 }`; a host without it offers none.
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
| `svc { id, service, method, params }` | A request of a service (see Services), answered by `svc-result` with its `id`. |

Unknown requests are ignored.

**Events** (host → reframe): `spawned`, `spawn-failed`, `exit { sessionId, instance, pid, exitCode }` (after the
session's last output), `error { message }` (something failed in the host; it goes on), `hook { message: {
sessionId, event, payload } }`, and for views `view { viewId, message }` where `message` is
`{ type: "snapshot", data, cols, rows }`, `{ type: "data", data }` or `{ type: "exit", exitCode }`, and
`view-closed { viewId }`; for services `svc-result` and `svc-event` (see Services).

**Snapshots.** `data` is a terminal escape-sequence stream that repaints the mirror into a terminal that was reset
and resized to `cols`×`rows` (the renderer does exactly that): scrollback and screen line by line (a line that wrapped
is left to wrap again), then the cursor position, the current attributes and the input modes (cursor keys, keypad,
bracketed paste, mouse) and a hidden cursor. The Node host's mirror is xterm.js (headless, 5000 lines of scrollback,
Unicode 11 widths) with the serialize addon; the Rust host's is vt100 (5000 lines, Unicode widths) with its own
serializer. Known differences of the Rust mirror: no strikethrough, blink, invisible or overline attributes, and in
the alternate screen only that screen is restored (not the normal one behind it).

## Services

Added without a protocol change (a protocol bump would leave running sessions behind in a hot swap): a reframe asks a
service only of a host whose welcome advertises it, and reads for itself otherwise (an older host taken over).

- `svc { id, service, method, params }` → `svc-result { id, ok: true, result }` or
  `svc-result { id, ok: false, error: { code, message } }`, where `code` is `unknown-method` (also for an unknown
  service), `bad-params`, `io` or `busy` (more than 8 requests being worked on at once). Answers go only to the
  connection that asked, if it is still the attached one; they can come in any order.
- `svc-event { service, event, data }`: told to the attached reframe. Events while no reframe is attached are not
  held: the next reframe lists afresh when it attaches.

**`transcripts` (version 1).** The past sessions of a Claude Code folder `home` (as Windows reaches it): its
transcripts `<home>/projects/<project>/<session id>.jsonl`, read as `src/main/claude/discovery.ts` and `titles.ts`
read them (the Rust host: `rust/reframe-transcripts`). The host keeps an index per `home` for as long as it runs:
each transcript's start (64 KiB) and end (256 KiB) are read once per modification time, and a project folder is
listed again only once watching (or `invalidate`) says it changed.

| Method | Result |
| --- | --- |
| `list { home, query?, limit, exclude: [sessionId], folder? }` | `{ items: [{ sessionId, modifiedAt, cwd?, title? }], hasMore, folders }`, what `TranscriptIndex.list` returns: newest first, without `exclude`d ids and transcripts with no prompt; `folder` keeps a project's (same last folder name) sessions; `query` (trimmed, lower-cased as JavaScript does) must be in the title or folder; at most `limit` items, `hasMore` if there are more. `folders` groups every listed session with a folder by project; the Rust host sorts them case-insensitively, reframe sorts them again with `localeCompare`. |
| `info { home, sessionId }` | That session as `list` gives it, or null. |
| `times { home }` | `{ [sessionId]: modifiedAt }` of every transcript (without reading them). |
| `title { home, sessionId }` | Claude Code's title of the session: the latest custom-title or ai-title line of the last 512 KiB of its transcript (after that, only what was added), or null. |
| `invalidate { home, projects: [project] \| "all" }` | null; those project folders are listed again. |
| `watch { home }` | null, once the host watches `<home>/projects` (and `<home>`, for `projects` appearing); again: nothing more. `io` if `home` is not there. |

Event `transcripts.changed { home, projects: [project] | "all" }`: transcripts of a watched `home` changed (told a
second after the first change, however many follow). The host has listed them as changed already. The Rust host is
told by the system (ReadDirectoryChangesW) and never polls; the Node host uses `fs.watch` as reframe did.

**`quota`, `clients` and `launch` (version 1 each).** Reads reframe asks for when it needs them, made in the host as
`src/main/quota.ts`, `client-adoption.ts` and `launch-config.ts` make them (the Rust host: `rust/reframe-services`).
The host keeps no timers for them: reframe asks for the Codex quota at start and every five minutes, as before (not
again when no Coordinator is installed). Each request runs off the host's task, at most 8 per service at once
(more: `busy`). Without the service in the welcome, or when a request fails, reframe reads for itself
(`src/main/host-reads.ts`).

| Method | Result |
| --- | --- |
| `quota.codex { localAppData, timeoutMs? }` | `{ coordinator: false, usage: null }` without `<localAppData>/Tools/llm-coordinator/active.json` naming a release (`[\w.-]+`) whose `releases/<release>/LLMCoordinator.exe` is there (an empty or null `localAppData` has none). Else `{ coordinator: true, usage }`: the exe is run hidden with `--native-client-stdin-active` and `{"method":"usage.snapshot","hours":24}` on its input, and `usage` is the JSON it writes, or null if it exits other than 0, writes no JSON or more than 64 MiB, or does not exit within `timeoutMs` (20000), when it is killed (the process, as `execFile` kills it). Reframe reads the windows from it with `parseCoordinatorUsage`. |
| `quota.statusLine { claudeHome }` | Whether `<claudeHome>/settings.json` sets a `statusLine` (truthy); true also when it is there but cannot be read or parsed. |
| `clients.adopted { record, provider }` | `adoptedClient(record, provider)`: `{ ok: true, path, version }` or `{ ok: false, reason }`. The record is a `reframe.client-adoption.v1` JSON file (a leading byte order mark is skipped); the binary is used only while its sha256 matches. The host keeps the hashes by path, size and modification time. |
| `launch.evaluate { file }` | `evaluateLaunchConfig(file)`: `{ file, userData, runtime, claudeHome, env, refused, problems }` (the variables a direct start sets, the providers it refuses and why), or null when the file cannot be read (a byte order mark is not skipped) or lacks an absolute `userData`, `runtime` or `claudeHome`. Nothing is applied or created. Reframe applies its launch config before it reaches any host (the profile it sets decides which host that is), so it reads it itself; the method is there for what runs once a host does. |

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
