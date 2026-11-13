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
  `{ pid, pipe, protocol, version, startedAt, sessionPids, kind, observer? }`. `sessionPids` lists the running sessions'
  processes; `kind` is `"node"` or `"rust"` (absent from hosts before 0.10.1-claudegpt.18: those are Node hosts);
  `observer` (since 0.10.1-claudegpt.25) is the version of the host's observer mode (see Observers), so that a client
  knows before its hello that it can attach without replacing the attached reframe.
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

**Handshake.** The first line of a connection must be `{ type: "hello", token, protocol, role? }` (`role`:
`"observer"` for a read-only client, see Observers; anything else, or none, is the primary). The host answers:

- `{ type: "welcome", protocol, version, pid, hooks: { port, token }, sessions: [{ sessionId, instance, pid,
  exitCode, meta?, claudeSessionId? }], kind, services?, role?, primary? }` when the token is the profile's and `protocol`
  equals the host's (1). `exitCode` is null while the session runs. `claudeSessionId` (since 0.10.1-claudegpt.25) is
  Claude Code's session id where the host knows it: the session's registry entry as `sessions` last read it, else the
  `session_id` of its latest hook, else `meta.launched` or `meta.wsl`; so a session started directly (without
  `meta.launched`) has it once its entry was read or a hook came. `role` (`"primary"` or `"observer"`) and `primary`
  (whether a primary client is attached, after this hello) come from hosts with observers. `hooks` is where the sessions' hook settings point, whichever reframe is attached.
  Hook messages held while no reframe was attached follow the welcome. `services` (since 0.10.1-claudegpt.20) names
  the services the host offers with each one's version, `{ transcripts: 1 }`; a host without it offers none.
- `{ type: "refused", reason: "token" | "protocol", protocol }`, and the connection is closed. A first line that
  is not a hello, or has another token, is `token`; the right token with another version is `protocol`.

One reframe per profile: a newly welcomed primary connection takes over. The previous one gets `{ type: "replaced" }`
and is closed, and its views are closed (observers' views stay). Messages of a primary connection that is not the
attached one are ignored.

**Observers** (`services.observer: 1`, and `observer: 1` in the descriptor, since 0.10.1-claudegpt.25). A connection
whose hello says `role: "observer"` is welcomed beside the attached reframe and never replaces it, nor is it replaced;
any number may be attached. Without a primary attached the host still exits when idle: observers do not keep it.
A client must check the descriptor before such a hello: an older host ignores `role` and takes it as a primary.

- It is told `spawned`, `spawn-failed`, `exit` and `sessions.status` events, and `svc-event { service: "observer",
  event: "primary", data: { primary } }` when a primary attaches or goes. Not hooks (they stay held for the next
  reframe), transcript changes or errors.
- It may open and close views of its own (its view ids are its own: the same id as the primary's is another view).
  They get the snapshot, live output, `exit` and `view-closed` as the primary's do. **They are read-only**: its
  `view-message`s (input and resizes) are ignored, so it never types into a session or changes its size under the
  primary's terminal. Input would have been simple, but two writers into one prompt are not safe; an observer that
  needs to type takes over as the primary instead.
- Its other requests (`spawn`, `write`, `dispose`, `shutdown`) are ignored. Of the services it may ask only reads:
  `sessions.list`, `observer.primary`, `transcripts.list`, `info`, `times` and `title`; anything else is answered
  with `forbidden`. Its `sessions.list` leaves the announcements held for the primary where they are.
- `observer.primary {}` → `{ primary }`: whether a primary client is attached (any client may ask).

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
  service), `bad-params`, `io` or `busy` (8 requests being worked on and 64 more waiting their turn; before 0.10.1-claudegpt.23, more than 8 at once),
  `conflict` (`state` only), `forbidden` (an observer's request it may not make). Answers go only to the
  connection that asked, if it is still the attached one (or still attached, for an observer); they can come in any order.
- `svc-event { service, event, data }`: told to the attached reframe (and the `sessions` and `observer` events to observers). Events while no reframe is attached are not
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

**`quota`, `clients` (version 1 each) and `launch` (version 2; 1 before `launch.spawnPlan`).** Reads reframe asks for when it needs them, made in the host as
`src/main/quota.ts`, `client-adoption.ts` and `launch-config.ts` make them (the Rust host: `rust/reframe-services`).
The host keeps no timers for them: reframe asks for the Codex quota at start and every five minutes, as before (not
again when no Coordinator is installed). Each request runs off the host's task, at most 8 at once and 64 more waiting
in turn (more: `busy`; from 0.10.1-claudegpt.23, before which more than 8 were refused). Without the service in the welcome, or when a request fails, reframe reads for itself
(`src/main/host-reads.ts`).

| Method | Result |
| --- | --- |
| `quota.codex { localAppData, timeoutMs? }` | `{ coordinator: false, usage: null }` without `<localAppData>/Tools/llm-coordinator/active.json` naming a release (`[\w.-]+`) whose `releases/<release>/LLMCoordinator.exe` is there (an empty or null `localAppData` has none). Else `{ coordinator: true, usage }`: the exe is run hidden with `--native-client-stdin-active` and `{"method":"usage.snapshot","hours":24}` on its input, and `usage` is the JSON it writes, or null if it exits other than 0, writes no JSON or more than 64 MiB, or does not exit within `timeoutMs` (20000, for the whole run: also one that closed its output but goes on), when it is killed (the process, as `execFile` kills it). Reframe reads the windows from it with `parseCoordinatorUsage`. |
| `quota.statusLine { claudeHome }` | Whether `<claudeHome>/settings.json` sets a `statusLine` (truthy); true also when it is there but cannot be read or parsed. |
| `clients.adopted { record, provider }` | `adoptedClient(record, provider)`: `{ ok: true, path, version }` or `{ ok: false, reason }`. The record is a `reframe.client-adoption.v1` JSON file (a leading byte order mark is skipped); the binary is used only while its sha256 matches. The host keeps the hashes by path, size and modification time. |
| `launch.evaluate { file }` | `evaluateLaunchConfig(file)`: `{ file, userData, runtime, claudeHome, env, refused, problems }` (the variables a direct start sets, the providers it refuses and why), or null when the file cannot be read (a byte order mark is not skipped) or lacks an absolute `userData`, `runtime` or `claudeHome`. Nothing is applied or created. Reframe applies its launch config before it reaches any host (the profile it sets decides which host that is), so it reads it itself; the method is there for what runs once a host does. |
| `launch.spawnPlan { session, mode, freshClaudeSessionId?, env, claudeHome, settingsDir, hooks: { port, token }, captureStatusLine?, claudeBin?, nodeRunner?, refused?, adopted?, adoption?, wsl? }` (version 2) | What reframe's `SessionManager.start` spawns for a session, worked out as `spawnPlan` (`src/main/claude/spawn-plan.ts`; the Rust host: `rust/reframe-services/src/spawn_plan.rs`) without starting, writing or asking anything. `session` is the state record's (`id`, `claudeSessionId`, `cwd`, `name`, `claudeName`, `used`, `launch`); `mode` is `new`, `resume` or `fresh` (a new conversation under `freshClaudeSessionId`, unused: restart's "start fresh"); `env` is what claude inherits (reframe's own). claude is the adopted client (`adopted`, the answer of `clients.adopted`, or `adoption`, a record the host reads for the session's provider), else `REFRAME_ANTHROPIC_CLAUDE_BIN` (Anthropic sessions), `claudeBin` (Settings), `REFRAME_CLAUDE_BIN`, else the PATH's (with npm/pnpm/yarn batch files seen through as `resolve.ts` does); a `.js` one runs by `nodeRunner` or `REFRAME_NODE`; `refused` (the launch config refused the provider) finds none. `wsl { share, command: { file, prefixArgs, env }, info: { claude, path, claudeHome } \| null, homeDir }` runs folders under the share in their distribution (the caller asks the distribution for `info`). Result: `{ ok: true, mode, file, args, cwd, env, settings?: { file, content }, meta, patch?, warnings }`: `mode` is `new` also for a resume of an unused session without a transcript; `args` is a string (cmd.exe's whole command line) for a batch file reframe cannot see through; `env` is the whole environment (parent Claude session markers removed, `REFRAME_SESSION` added; for Anthropic sessions the routing variables removed and `ANTHROPIC_BASE_URL` set); `settings` is the hook (and, with `captureStatusLine` for Anthropic, status line) settings file the caller writes before spawning (it holds the hook token); `meta` is the `spawn`'s; `patch` is what a fresh start changes in the record. Else `{ ok: false, error: folderMissing \| transcriptMissing \| claudeNotFound, warnings }`. `bad-params` for an input without the session's ids and folder, a known mode, `env`, `claudeHome`, `settingsDir` or `hooks`. Reframe itself still builds its spawns in-process with the same functions; test/spawn-plan-fixtures holds both hosts' implementations to the same plans. Never read: Claude Code's credentials. |

**`sessions` (version 2; 1 before 0.10.1-claudegpt.28).** Each session's status, worked out in the host from its hooks and its Claude Code
session registry entry as `SessionManager` (`src/main/sessions.ts`) works it out in-process (the Rust host:
`rust/reframe-sessions`; the Node host: `src/ptyhost/sessions.ts`). The host follows a session only if its spawn's
`meta` says where its entry is: `claudeHome` (Windows: `<claudeHome>/sessions/<pid>.json`; with `launched`, claude's
own entry found by its session id once and then by its process id, while that process runs and has the entry's
`procStart`), or `wsl.share` (WSL: the entry in `wsl.home` found by `wsl.claudeSessionId`, whose Linux process
`<share>/<distro>/proc/<pid>/cmdline` is a claude's, then by that process id). Reframe gives that from
0.10.1-claudegpt.22 on; sessions started by an older one are not followed here (reframe follows them itself).

- The status starts as `starting`. Hooks: `UserPromptSubmit` → `busy`; `Stop` → `idle`, announcing `done`;
  `Notification` other than `idle_prompt` → `waiting`, announcing `waiting`.
- **Background work** (version 2): at a `Stop`, the host looks at claude's own Windows process (the spawn's process
  for `claudeHome`, the registry entry's with `launched`; never in WSL, nor before a launched claude's entry was
  found) for *tool shells*: direct children named `bash.exe`, `sh.exe`, `pwsh.exe`, `powershell.exe` or `cmd.exe`
  (any case), at least 5 s old (status line and hook commands are younger) and not older than claude. With any, the
  session is `idle` with `background: true`, and nothing is announced: Claude Code starts a turn of its own when that
  work ends. Only such sessions are looked at again, every 20 s (`REFRAME_PTYHOST_BACKGROUND_MS` in the host's
  environment, for tests); once none is left (still idle), `background` goes and `done` is announced. Any other status
  (`UserPromptSubmit`, a registry `busy` or `waiting`, a `Notification`, the exit) ends the wait; a registry `idle`
  does not. Each look is one toolhelp snapshot of the system's processes (CreateToolhelp32Snapshot; koffi in the Node
  host) and the creation time of claude's children: a few milliseconds. The registry is read once a second (one
  read per session at a time, off the host's task): an entry under the process id older than the process (its
  `procStart` differs, or without one, its `startedAt` is more than 2 s before the spawn) is ignored; a registry
  `status` that changed is taken (in WSL, which has no hooks, `waiting` announces `waiting`, and `idle` after
  `busy` announces `done`); otherwise a `starting` session becomes the entry's status or `idle`. The exit makes it
  `ended`, and nothing changes it after that. How an exit counts (ended, or failed to start), who ended a session,
  and whether an announcement is shown are reframe's.
- Event `sessions.status { sessionId, instance, status, registry?, notify?, claudeSessionId?, revision }`, told when
  the status, the entry, Claude Code's session id, `background` or an announcement changed: `status` is `starting`, `idle`, `busy`, `waiting` or `ended` (never a new value: a reframe before .28 shows a session with background work as plain `idle`, without the `done`, which it gets when the work ends);
  `background: true` (version 2) only while idle with background work; `registry` the entry as
  last read, as `parseRegistryEntry` gives it (`{ pid, sessionId, cwd?, name?, nameSource?, status?, startedAt?,
  procStart? }`; `sessionId` changes on `/clear`, `name` on a rename, `cwd` is the distribution's path in WSL);
  `notify` is `done` or `waiting`, an intent (reframe words it, and decides whether to show it).
  `claudeSessionId` is as in the welcome (the entry's `sessionId`, else the latest hook's `session_id`, else the
  spawn's). `revision` (since 0.10.1-claudegpt.25) is the host's status revision: one counter for every session,
  bumped by each change (told or not), so it only grows while the host runs.
- `list {}` → `{ sessions: [ same as the event ], revision }` for every session followed, for a reframe that attaches
  (events are not held); each item has the revision of its last change, and `revision` is the newest of all (every
  change up to it is in the list). An announcement made while no reframe was attached comes with it, once (the latest
  per session). **Ordering:** a list's answer and status events can arrive in either order (the answer is sent when
  the request is done). A client keeps the revision of what it applied per session and applies an event only when its
  revision is newer than the list's `revision` and than that session's; a list item only when newer than that
  session's. Without `revision` (hosts before .25) it applies everything as it comes.

Reframe takes the status of a session the host follows from these alone (it does not follow its hooks or registry
for it then); identity (`claudeSessionId`, folder, pids, first use) it takes from the forwarded hooks and the
`registry` here, as before. Without the service, or if `list` fails, it follows the sessions itself.

**`state` (version 1, since 0.10.1-claudegpt.23).** Reframe's `state.json`, written by the host as
`src/main/state/store.ts` writes it in-process (the Rust host: `rust/reframe-services/src/state.rs`; the Node host:
`src/main/state/files.ts`), so the file, its format and its path stay the same whichever writes it, and any release
reads it. The content is opaque to the host: the schema, its checks and migrations stay reframe's. Requests are
taken one at a time, in order (a queue of their own, so a slow quota read never holds a save up).

| Method | Result |
| --- | --- |
| `state.read { file }` | `{ revision, content }`: the file's text (UTF-8), or null when it is not there. The revision is the host's for the file as read: unchanged (same size and modification time) since it was last read or written here, the same number; else a new one, higher than any before. |
| `state.write { file, content, revision, backup }` | `{ revision }`, the new one. Refused with `conflict` (nothing written) unless `revision` is the file's current one here and the file has not changed on disk since: a file never read here, a stale writer, or one written meanwhile by someone else (reframe itself included). The content goes to `<file>.<host pid>.tmp`, is flushed to disk (fsync) and renamed over the file (retried a few times while something holds it); with `backup` and a file there, that file is first copied to `<file>.bak`. A failed write removes the temporary file and leaves the file; it must be read again before the next write. |

Revisions live as long as the host: a new host (after an update) knows no file, so a writer reads it first.
Reframe 0.10.1-claudegpt.23 does not use the service yet: it loads `state.json` itself (before a host is started:
the state says which host) and saves it in-process and synchronously, because its callers expect the file written
when a save returns. `StateStore.useHost` (one write at a time, always the newest state, the backup asked for when
the file there is the one last written or read again and found good, one retry after reading again on `conflict`,
in-process when the host fails) is there for when the saves can be awaited.

**`cleanup`, `wsl` and `history` (version 1 each).** What Electron's main process does
for these, served by the host so that a shell without it (the WPF shell) gets the same: the Node host runs reframe's
own `src/main/cleanup.ts`, `src/main/wsl/distros.ts` (with `src/shared/wsl.ts`) and `src/main/history-helper.ts`;
the Rust host ports them (`rust/reframe-services/src/cleanup.rs`, `wsl.rs`, `history.rs`; the orphan check in
`rust/reframe-host/src/jobs.rs`). Primary only (an observer is refused, `forbidden`); each request runs off the
host's task with the other reads (at most 8 at once, 64 waiting). Electron reframe itself still runs the
TypeScript in-process; the services are there for the shell. Paths are Windows paths; `bad-params` for a missing or
relative one where an absolute one is asked for.

| Method | Result |
| --- | --- |
| `cleanup.files { drops, hooks, sessionIds, now? }` | `{ drops, hooks }`: how many were removed, as `cleanUp` removes them. In `drops` (reframe's `<temp>/reframe-drops`) each entry directly in it whose modification time is more than 7 days before `now` (ms, default the host's clock) is removed (a folder with what it holds; a link itself, never what it points to); in `hooks` (`<userData>/hooks`) each entry whose name ends in `.json` and whose name without it is not in `sessionIds`. Nothing else, nothing deeper; an entry that cannot go now goes next time. A folder that is not there is nothing to clean. |
| `cleanup.orphans { claudeHome, sessions: [{ pid, claudeSessionId }], hosted: [pid] }` | `{ killed: [pid] }` (sorted): `SessionManager.cleanupOrphans` without reframe's own running sessions (the caller leaves those out). A session's process is killed (`taskkill /PID <pid> /T /F`, hidden) only when it runs, is not in `hosted` (sessions a host runs), and `<claudeHome>/sessions/<pid>.json` names that very process (its `pid`) with that `sessionId` and, if it has a `procStart`, the process's creation time matches; its entry is removed after it once it is gone. WSL sessions' claudes are not looked at here (`wsl.stopClaude`). |
| `wsl.list { share?, command? }` | The distributions there are (`wsl.exe -l -q`, UTF-16 or UTF-8), `[]` without WSL. |
| `wsl.running { share?, command? }` | Those that run now (`-l --running -q`), without `docker-desktop*`; asking starts none. Asked afresh each time (reframe's own keeps an answer 3 s). A distribution not running (and not found with claude) is probed again next time. |
| `wsl.info { distro, share?, command? }` | `WslInfo` `{ distro, claude, path, home, claudeHome, mountRoot }` or null: the probe (`PROBE_SCRIPT`, through the user's interactive login shell, which starts the distribution; 60 s), asked once per distribution (by its lower-case name) and host as `WslDistros.info` asks it; `claude` is null when the only one is the Windows one under the mount root. |
| `wsl.isClaude { distro, pid }` | Whether `<share>\<distro>\proc\<pid>\cmdline` is a Claude Code's (`looksLikeClaude`). |
| `wsl.stopClaude { distro, pid }` | `stopped` or `failed`: never a process that is not a claude (`failed` without signalling); else `kill -s INT`, `INT`, then `TERM` in the distribution, each followed by up to 3 s of waiting for its `/proc` folder to go. |
| `wsl.parsePath { path, share? }` | `parseWslPath`: `{ distro, path }` for a folder under `share`, `\\wsl.localhost` or `\\wsl$` (any case, either slash), else null. |
| `wsl.windowsPath { distro, path, share }` | `wslWindowsPath`: the Linux `path` as a Windows path under `share`. |
| `wsl.linuxPath { path, distro, mountRoot, share? }` | `linuxPathOf`: a drive path under `mountRoot` (`C:\x` → `/mnt/c/x`), the distribution's own folder as its Linux path, else null. |
| `history.prepare { backend, cwd, home, root, ids }` | The helper's `prepare` `results` (per chat `{ ok: true, id, plan, sha256, files, bytes, operation }` or `{ ok: false, id, error, code? }`), each chat planned in `<root>\operations\<random UUID>`. |
| `history.retire { backend, cwd, home, jobs: [{ prepared, mode: archive \| delete, metadata }] }` | The helper's `retire` `results` (`{ ok: true, id, archive?, sha256? }` or a failure; `code: chat_active` for a chat that runs). |
| `history.recover { backend, cwd, home, root }` | The helper's `recover` result `{ entries, warnings, restored }`. |
| `history.restore { backend, cwd, home, id, operation, archive, sha256 }` | null once restored. |

`wsl`: `share` and `command` (`{ file, prefixArgs, env }`, as `wslCommand` gives it) replace the share and wsl.exe;
without them, the host's `REFRAME_WSL_SHARE` (else `\\wsl.localhost`) and `REFRAME_WSL_EXE` (else
`%SystemRoot%\System32\wsl.exe`; a `.js`/`.cjs`/`.mjs` stand-in runs by `REFRAME_NODE`; the Rust host has no
fallback for a script without it and then finds no distributions). wsl.exe runs hidden with `WSL_UTF8=1` (10 s for
the lists and signals). `distro` must be a plain name (no `\`, `/`, `.` or `..`). Not served (yet): keeping a
distribution running (`keep`/`release`, which need a keeper process tied to the shell's life) and the takeover
helpers `terminalOf`, `shellsOf`, `idle` (stage S12). Real WSL is not exercised in CI (the conformance tests use
`test/fake-wsl`); the path and parsing rules have unit tests in both languages.

`history` wraps `maintenance/history_backend.py` unchanged (it is not ported): the host runs
`<REFRAME_HISTORY_PYTHON> -I -B <backend> <command> --home <home> …` hidden, in `cwd` (reframe's profile folder),
with `TEMP`/`TMP` set to `REFRAME_RUNTIME` when set, the job list as JSON on its input for `prepare` and `retire`,
and no time limit (a job runs as long as it needs). The Python is the host's own environment's, never the request's:
absolute, and only with `REFRAME_HISTORY_HELPER_SHA256` set (the helper itself verifies its thread-cleanup helper,
`REFRAME_HISTORY_HELPER`, against it); otherwise `io` with reframe's message, nothing run. `backend` must be an
absolute path to a file named `history_backend.py`; `root` an absolute folder not on drive C: (`isArchiveRoot`);
`cwd` absolute. A helper that fails answers `io` with its `error` (else `Chat operation incomplete (<command>).`);
one whose output is not its JSON, `io` starting `Chat operation failed; originals/recovery receipts are kept.`.
The caller chooses `root` (reframe: the archive root setting, `REFRAME_ARCHIVE_ROOT` or `<userData>/archives`) and
applies the results to the state (removing retired chats, recording archives) itself.

**`overview` (version 1; the Rust host only).** The activity index behind the WPF shell's Overview pane, built from
thread activity (Claude Code transcripts `<home>/projects/<project>/<session id>.jsonl`), not from repositories (the
Rust host: `rust/reframe-transcripts/src/overview.rs`). The Node host does not offer it (the Overview is the WPF
shell's). Footprint first: the index is built by one worker thread in background mode
(`THREAD_MODE_BACKGROUND_BEGIN`: lowest CPU, disk and memory priority), streaming each transcript through a 64 KiB
buffer (a line over 4 MiB is skipped, not parsed), pausing 5 ms between transcripts, and waiting while any session the
host follows is `busy`. Per transcript it remembers the offset read and a fingerprint of its first KiB, so afterwards
only appended lines are read; a transcript that got shorter or starts differently is read again from its start (its
earlier counts replaced). A line not finished yet (no newline) is read next time; a line that is not JSON is counted as
skipped. The first pass may take as long as it takes. After it, the worker reads again only when configured anew (at
start) and once per closed day: it sleeps until the next local midnight and reads at the first moment after it with no
session busy. No watchers and no other timers. A transcript that is gone keeps its activity (`gone: true`).

What is counted, per local day (Windows' time zone at the time of the event), per transcript: **prompts** (`user`
lines whose content is text or an image, not tool results, `isMeta` or local command output), **turns** (assistant
messages, one per message id however many lines it takes), **sessions** (on the day of a transcript's first prompt
or turn), **active minutes** (distinct minutes with a prompt or turn), assistant turns per **model** and tool uses per
**kind** (`edit`: Edit, MultiEdit, Write, NotebookEdit; `bash`: Bash, BashOutput, KillShell, PowerShell; `read`: Read,
Glob, Grep, LS; `web`: WebFetch, WebSearch; `other`). Sidechain lines are not counted. Per transcript also its first
`cwd` (the folder), its title (the latest `custom-title`, else the latest `ai-title`) and when it was last active. No
prompt text is ever kept. The cache is one JSON file (`{ schema: 1, firstPassComplete, lastPassDay, files: { path: {
provider, sessionId, offset, fingerprint, …, days: { "YYYY-MM-DD": counts } } } }`), saved (to `<file>.tmp`, then
renamed) after every 64 transcripts read and at the end of a pass, so an interrupted pass resumes; a cache of another
schema is dropped and built again.

| Method | Result |
| --- | --- |
| `overview.configure { cache, homes: [{ provider, home }] }` | Status (as `status`). `cache` is an absolute path in the profile (reframe: `<userData>/overview.json`); `homes` the Claude Code folders, one per provider whose transcripts live apart. A new configuration loads that cache and starts a pass; the same one again does nothing. Not for observers. |
| `overview.status {}` | `{ state, firstPassComplete, filesDone, filesTotal, bytesRead, lastPassDay?, lastPassMs?, lastError? }`, `state` being `unconfigured`, `indexing`, `paused` (a session is busy) or `idle` (until the next day). |
| `overview.summary { year? }` | `{ from, to, days: [{ date, prompts, turns, sessions, activeMinutes }], totals, streaks: { current, longest }, years }`: the days with activity in `year`, or in the 365 days up to today without one; streaks are days in a row with a prompt (over all time; `current` counts up to today or yesterday); `years` has activity, newest first. |
| `overview.threads { limit? }` | `{ threads: [{ sessionId, provider, title, titleSource, folder, turns, prompts, activeMinutes, lastActive, gone }] }`: the most active transcripts (most turns, then latest), at most `limit` (10, up to 1000). |
| `overview.breakdown { year? }` | `{ from, to, providers, models, tools }`: turns per provider and model, tool uses per kind (every kind, zeros included), in the range as for `summary`. |
| `overview.timeline {}` | `{ months: [{ month, sessions, prompts, turns, activeMinutes, folderCount, folders: [{ folder, sessions, prompts, turns, activeMinutes }] }] }`, newest month first, folders by turns. |

Every method but `configure` answers from the index as it is (during the first pass, what was read so far); observers
may ask them. Footprint measured on fixtures (`cargo test -p reframe-transcripts --release -- --ignored --nocapture
footprint`): see the pull request that added it.

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
