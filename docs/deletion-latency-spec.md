# Windows chat deletion latency repair — 2026-11-10

Authority: owner reports an actual selected ten-chat batch deleting approximately
one chat per minute and asks why offline status is insufficient. Preserve the
original running batch and completed outcomes; never replay selected deletions.

Observed cause: each item invokes a full PowerShell/CIM process census four
times through batch precheck, planning, plan loading and final retirement.
Read-only same-host measurement: old census11942ms versus native psutil83ms,
same live IDs. Actual operation records approximately44 seconds apart.

Required outcome: one fresh native activity census immediately before each
history/index mutation, owned by the installed retirement helper. Read-only
planning does not repeat activity checks. Offline UI state is rechecked locally;
external/native registrations and explicit fork command references remain
protected. Active items skip, unrelated items continue. No cached safety census,
timeouts, parallel deletes, new worker, broad deletion or retry of the real batch.
Keep one confirmation per batch, exact snapshots, shared-index exclusivity,
identity/hash deletion, active/protected/path safeguards, partial receipts and
all existing provider/model/effort/permissions/configuration/history behavior.

Reuse installed psutil7.2.2 native process APIs; declare the helper dependency.
Missing dependency or unreadable relevant process fails closed. Publish/deploy
the narrowly changed helper bundle from its canonical agent-toolbelt source,
preserving unrelated dirty work. Install only after the original batch ends,
bind the reviewed helper hash in Reframe's managed release and launcher, and
preserve a rollback helper copy matching the previous release's frozen binding.
Current user settings changed after .3 installed verification; preserve their
fresh hash as the next activation baseline instead of restoring historical bytes.

Owner additions before publication: show an explicit per-chat Effort selector in
New session, inherit a persisted Reframe default when configured, and expose that
default in Settings. Default/CLI inheritance preserves existing settings; explicit
effort uses the native --effort flag and persists for resume. Support low, medium,
high, xhigh and max as native session choices; never modify global CLI settings,
credentials, provider/model routing or permission controls.

Provider workspace correction: one Reframe ecosystem with separate ChatGPT and
Claude project-folder trees. Identity is provider plus canonical full folder path.
The same folder can appear under both providers with disjoint chat lists; same
basenames at different full paths remain distinct. Keep Pinned/Recents unified,
preserve icons/selection/pins/history and per-provider paging/expansion through
restart. Provider changes move chat metadata into the selected provider tree,
preserving the same conversation and working directory. Migrate prior unscoped
expanded-folder metadata without moving physical folders or histories.

Owner addition: each project-workspace row has a visible accessible + control
opening New session with that exact workspace folder/provider preselected.
Global New session defaults to Documents/ChatGPT for ChatGPT/Codex and
Documents/Anthropic for Claude (the owner's exact Windows Documents paths).
Switching provider selects that provider's default root. Existing project/history
bindings stay intact; do not move or duplicate existing repositories. Prepare the
owner-requested persistent base folders if missing at managed activation.

1. Implementation — COMPLETE by source reconciliation for expanded owner scope. Minimal native reader/helper error code, remove
   redundant prechecks, keep final mutation guard and active-item skip; focused
   regressions and versioned packaging inputs prepared.
2. Windows qualification — ACTIVE for expanded scope; latency evidence remains applicable to unchanged paths. Verify fresh registration/command/fork
   guards, unreadable/missing-dependency preservation, one census per deletion,
   same native selected-menu batch with fictional histories and shared rows,
   actual ten-chat elapsed time and no real-user deletion/replay. Verify types,
   formatting and affected tests plus native package.
3. Final delivery — NOT STARTED. Verified commits/push, normal Windows release,
   independent download/hash/payload, fresh safe idle and original batch completed,
   installed helper/CLI/provider/protected hashes, responsive app and existing
   recovery; parent pin/provenance and transactional cleanup. Windows is the gate;
   do not wait on non-Windows runners. No proxy or CLI rebuild/restart.
