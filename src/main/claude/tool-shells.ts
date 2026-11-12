// Background work of a claude: the tool shells it still runs after its turn ended (a Bash `run_in_background` task, a
// monitor). When one of them finishes, Claude Code starts a turn of its own, so a session with any is not done yet:
// it is idle with background work, and `done` is announced once the last is gone. The same rule as the Rust host's
// (rust/reframe-sessions: has_tool_shells), for the Node host (src/ptyhost/sessions.ts) and SessionManager.
import { win32 } from '../win32';

/** The executables of the shells Claude Code runs its tools in (Bash and PowerShell tools, and what they start). */
export const TOOL_SHELLS: ReadonlySet<string> = new Set([
  'bash.exe',
  'sh.exe',
  'pwsh.exe',
  'powershell.exe',
  'cmd.exe',
]);
/** A shell younger than this is not counted: status line and hook commands come and go within it. */
export const TOOL_SHELL_MIN_AGE_MS = 5_000;
/** How often a session idle with background work is looked at again (only those sessions). */
export const BACKGROUND_RECHECK_MS = 20_000;

/** A FILETIME (100 ns since 1601, as a decimal string) as ms since 1970. */
export function filetimeMs(filetime: string): number {
  return Number(BigInt(filetime) / 10_000n) - 11_644_473_600_000;
}

/**
 * Whether a claude has tool shells running: direct children named as in TOOL_SHELLS (any case), started (ms since
 * 1970) at least TOOL_SHELL_MIN_AGE_MS ago and not before claude (`parentStart`: a child of an earlier process under
 * the same id). A child whose start is unknown is not counted.
 */
export function hasToolShells(
  children: { exe: string; start: number | null }[],
  parentStart: number | null,
  now = Date.now(),
): boolean {
  return children.some(
    ({ exe, start }) =>
      TOOL_SHELLS.has(exe.toLowerCase()) &&
      start !== null &&
      now - start >= TOOL_SHELL_MIN_AGE_MS &&
      (parentStart === null || start >= parentStart),
  );
}

/**
 * Whether claude (Windows process `pid`) has tool shells running: one toolhelp snapshot of the system's processes
 * and the creation time of its children (a few milliseconds). False where that cannot be told (not Windows).
 */
export function toolShells(pid: number): boolean {
  const api = win32();
  if (!api) return false;
  try {
    const time = (start: string | null) => (start ? filetimeMs(start) : null);
    const children = api.childProcesses(pid).map((c) => ({ exe: c.exe, start: time(c.start) }));
    return hasToolShells(children, time(api.processStart(pid)));
  } catch {
    return false;
  }
}

/** How often to look again: REFRAME_PTYHOST_BACKGROUND_MS (for tests), else BACKGROUND_RECHECK_MS. */
export function backgroundRecheckMs(env: NodeJS.ProcessEnv = process.env): number {
  const ms = Number(env.REFRAME_PTYHOST_BACKGROUND_MS);
  return Number.isFinite(ms) && ms > 0 ? ms : BACKGROUND_RECHECK_MS;
}
