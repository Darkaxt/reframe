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

Provider workspace correction (owner clarified after .4 installation): one Projects
tree, grouping by the final folder name regardless of parent path or provider.
The .4 separate provider headings and full-path project identity were an incorrect
interpretation and must be replaced. Windows folder-name case/separator aliases
merge; distinct final names stay separate. Keep actual per-chat working paths and
provider selections intact, with provider icons on chats. Preserve every matching
chat across paths in project paging/search, selection, pins and restart expansion.
Migrate prior full-path/provider-scoped expansion metadata to the merged project.
The ChatGPT/Anthropic roots are physical new-session defaults, not sidebar nodes.
Do not move or duplicate histories or repositories. Each merged workspace retains
its real folder/provider locations for contextual creation; full paths remain
visible in chat/tooltips and New session. The + button chooses an actual existing
location and changing provider selects its known location when available, otherwise
the established provider default root. Keep Pinned/Recents unified.

Terminal route repair (owner screenshot after correction work began): the earlier
ad hoc command cleared only the Sonnet alias; --model opus resolved to the shared
gateway model. Supply a reviewed standalone Windows launcher using the complete
subscription-route isolation, including all built-in aliases and additional named
aliases present in settings/environment. Bind the dedicated executable/hash via
the managed active record, preserve parent environment and saved settings, forward
explicit model/effort/permission arguments unchanged, and use tool-owned D: runtime.
Verify built-in model-alias overrides and a fictional fourth alias locally,
plus one benign native subscription request. Publish the launcher with this release
and install a stable usable command. Never alter global routing or prompt login.

Owner addition: each project-workspace row has a visible accessible + control
opening New session with that exact workspace folder/provider preselected.
Global New session defaults to Documents/ChatGPT for ChatGPT/Codex and
Documents/Anthropic for Claude (the owner's exact Windows Documents paths).
Switching provider selects that provider's default root. Existing project/history
bindings stay intact; do not move or duplicate existing repositories. Prepare the
owner-requested persistent base folders if missing at managed activation.

Current correction stages: implementation ACTIVE; Windows qualification NOT STARTED;
final delivery NOT STARTED. .4 installed payload remains usable while correcting
the grouping. Its in-progress exact cleanup must complete without racing new build
outputs; preserve original session59497 and cleanup transaction. Publish/verify a
complete .5 Windows release and normal managed activation, parent pin and cleanup.
Existing latency/helper/effort/quit logic is unchanged; reuse applicable evidence.
The historical .4 stage record below describes the superseded interpretation.

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
