// How chat history jobs (archive, delete, restore, recovery) are run: by the pinned Python helper
// maintenance/history_backend.py, which verifies its thread-cleanup helper (REFRAME_HISTORY_HELPER,
// REFRAME_HISTORY_HELPER_SHA256) itself. Free of Electron, so that the Node PTY host serves it too (the `history`
// service, docs/ptyhost-protocol.md); src/main/history.ts is reframe's own adapter around it.
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import path from 'node:path';
import type { Provider } from '@shared/providers';

export interface HelperRun {
  /** history_backend.py, absolute. */
  backend: string;
  /** Where the helper runs (reframe's profile folder). */
  cwd: string;
  /** The helper's environment, whose REFRAME_HISTORY_PYTHON and REFRAME_HISTORY_HELPER_SHA256 pin it. */
  env: NodeJS.ProcessEnv;
}

export const HELPER_MISSING =
  'Archive/delete need the configured Python and verified thread-cleanup helper. See the ClaudeGPT installation guide.';

/** The configured Python: absolute, with the helper's sha256 given; else the helper is not to be run. */
export function historyPython(env: NodeJS.ProcessEnv): string {
  const python = env.REFRAME_HISTORY_PYTHON;
  if (!python || !path.isAbsolute(python) || !env.REFRAME_HISTORY_HELPER_SHA256) throw Error(HELPER_MISSING);
  return python;
}

/** Runs the helper's `command` once (any number of chats per run) and gives its result; rejects with its error. */
export function runHistoryHelper(
  run: HelperRun,
  command: string,
  home: string,
  extra: string[] = [],
  input?: unknown,
): Promise<Record<string, unknown>> {
  let python: string;
  try {
    python = historyPython(run.env);
  } catch (error) {
    return Promise.reject(error);
  }
  return new Promise((resolve, reject) => {
    const child = spawn(python, ['-I', '-B', run.backend, command, '--home', home, ...extra], {
      windowsHide: true,
      cwd: run.cwd,
      env: {
        ...run.env,
        TEMP: run.env.REFRAME_RUNTIME || run.env.TEMP,
        TMP: run.env.REFRAME_RUNTIME || run.env.TMP,
      },
      stdio: [input === undefined ? 'ignore' : 'pipe', 'pipe', 'pipe'],
    });
    let stdout = '',
      stderr = '';
    child.stdout!.on('data', (b) => {
      stdout += String(b);
    });
    child.stderr!.on('data', (b) => {
      if (stderr.length < 8192) stderr += String(b);
    });
    child.on('error', reject);
    if (input !== undefined) child.stdin!.end(JSON.stringify(input));
    child.on('close', (code) => {
      try {
        const packet = JSON.parse(stdout) as {
          ok: boolean;
          error?: string;
          code?: string;
          result: Record<string, unknown>;
        };
        if (code !== 0 || !packet.ok)
          reject(
            Object.assign(Error(packet.error || `Chat operation incomplete (${command}).`), { code: packet.code }),
          );
        else resolve(packet.result);
      } catch (error) {
        reject(Error(`Chat operation failed; originals/recovery receipts are kept. ${stderr || String(error)}`));
      }
    });
  });
}

export interface HistoryMetadata {
  id: string;
  label: string;
  cwd: string;
  provider?: Provider;
}
export interface PreparedHistory {
  plan: string;
  sha256: string;
  id: string;
  files: number;
  bytes: number;
  operation: string;
}
export interface ArchiveEntry extends HistoryMetadata {
  archive: string;
  sha256: string;
  operation: string;
  partial?: boolean;
}
/** One chat's outcome in a prepare/retire batch. */
export type Outcome<T> = ({ ok: true } & T) | { ok: false; id: string; error: string; code?: string | null };
export interface RetireJob {
  prepared: PreparedHistory;
  mode: 'archive' | 'delete';
  metadata: HistoryMetadata;
}

/** The helper's `prepare` input: each chat planned in an operation folder of its own under `<root>/operations`. */
export function prepareInput(root: string, ids: string[]): { jobs: { id: string; operation: string }[] } {
  const operations = path.join(root, 'operations');
  return { jobs: ids.map((id) => ({ id, operation: path.join(operations, crypto.randomUUID()) })) };
}

/** The helper's `retire` input. */
export function retireInput(jobs: RetireJob[]) {
  return {
    jobs: jobs.map(({ prepared, mode, metadata }) => ({
      id: prepared.id,
      operation: prepared.operation,
      plan: prepared.plan,
      sha256: prepared.sha256,
      mode,
      metadata,
    })),
  };
}
