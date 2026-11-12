// Where the quota counters come from. Codex: LLM Coordinator's installed native client asks the running Coordinator
// for its usage snapshot (read only; its state stays its own). Claude: the status line input of reframe's own
// Anthropic sessions (see statusLineSettings), captured only when the user has no status line of their own, and else
// what the Coordinator observed of it (newer Coordinators).
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { parseCoordinatorProviders, parseCoordinatorUsage, type ObservedQuota, type QuotaWindow } from '@shared/quota';

/** The installed Coordinator's executable, from its active.json (`releases/<release>/LLMCoordinator.exe`). */
export function coordinatorExecutable(localAppData = process.env.LOCALAPPDATA): string | null {
  if (process.platform !== 'win32' || !localAppData) return null;
  const root = path.join(localAppData, 'Tools', 'llm-coordinator');
  try {
    const active = JSON.parse(fs.readFileSync(path.join(root, 'active.json'), 'utf8')) as { release?: unknown };
    if (typeof active.release !== 'string' || !/^[\w.-]+$/.test(active.release)) return null;
    const exe = path.join(root, 'releases', active.release, 'LLMCoordinator.exe');
    return fs.existsSync(exe) ? exe : null;
  } catch {
    return null;
  }
}

/**
 * The running Coordinator's answer to `usage.snapshot` (its JSON), or null when it cannot be started, fails, writes no
 * JSON or does not answer in time (it is killed then).
 */
export function readCodexUsage(exe: string, timeoutMs = 20_000): Promise<unknown> {
  return new Promise((resolve) => {
    const child = execFile(
      exe,
      ['--native-client-stdin-active'],
      { windowsHide: true, timeout: timeoutMs, maxBuffer: 64 * 1024 * 1024 },
      (error, stdout) => {
        if (error) return resolve(null);
        try {
          resolve(JSON.parse(stdout));
        } catch {
          resolve(null);
        }
      },
    );
    child.stdin?.end(JSON.stringify({ method: 'usage.snapshot', hours: 24 }));
  });
}

/** The quota windows of a Coordinator answer; null without any. */
export function codexWindows(usage: unknown): QuotaWindow[] | null {
  try {
    const windows = parseCoordinatorUsage(usage);
    return windows.length ? windows : null;
  } catch {
    return null;
  }
}

/** The Claude quota a Coordinator answer has (newer Coordinators observe it too); null without it. */
export function claudeObserved(usage: unknown): ObservedQuota | null {
  try {
    return parseCoordinatorProviders(usage).claude ?? null;
  } catch {
    return null;
  }
}

/** The Codex quota windows the running Coordinator observed; null without a Coordinator or an answer. */
export async function readCodexQuota(exe: string, timeoutMs = 20_000): Promise<QuotaWindow[] | null> {
  return codexWindows(await readCodexUsage(exe, timeoutMs));
}

/** Whether the user's Claude Code settings set a status line (reframe's capture would replace it, so it is not done). */
export function userHasStatusLine(claudeHome: string): boolean {
  try {
    const settings = JSON.parse(fs.readFileSync(path.join(claudeHome, 'settings.json'), 'utf8')) as {
      statusLine?: unknown;
    };
    return !!settings.statusLine;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== 'ENOENT';
  }
}
