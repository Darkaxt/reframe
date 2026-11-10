import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import path from 'node:path';
import { app } from 'electron';
import type { Provider } from '@shared/providers';
import { isArchiveRoot } from './archive-root';

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

/**
 * Installation-bound adapter. The native helper supplies exact-handle retirement; no shell is involved.
 * prepare and retire take any number of chats in one Python process: a batch costs two processes (and one live
 * Claude process census, taken right before the first change), not two per chat.
 */
export class History {
  constructor(private selectedRoot: () => string | undefined = () => undefined) {}
  root(): string {
    const root =
      this.selectedRoot() || process.env.REFRAME_ARCHIVE_ROOT || path.join(app.getPath('userData'), 'archives');
    if (!isArchiveRoot(root)) {
      throw Error('Set REFRAME_ARCHIVE_ROOT to an absolute folder on a non-C drive.');
    }
    return root;
  }

  async call(command: string, home: string, extra: string[] = [], input?: unknown): Promise<Record<string, unknown>> {
    const python = process.env.REFRAME_HISTORY_PYTHON;
    if (!python || !path.isAbsolute(python) || !process.env.REFRAME_HISTORY_HELPER_SHA256) {
      throw Error(
        'Archive/delete need the configured Python and verified thread-cleanup helper. See the ClaudeGPT installation guide.',
      );
    }
    const backend = app.isPackaged
      ? path.join(process.resourcesPath, 'history_backend.py')
      : path.join(app.getAppPath(), 'maintenance', 'history_backend.py');
    return new Promise((resolve, reject) => {
      const child = spawn(python, ['-I', '-B', backend, command, '--home', home, ...extra], {
        windowsHide: true,
        cwd: app.getPath('userData'),
        env: {
          ...process.env,
          TEMP: process.env.REFRAME_RUNTIME || process.env.TEMP,
          TMP: process.env.REFRAME_RUNTIME || process.env.TMP,
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

  /** Plans (reads only) each chat's exact files, each in its own operation folder. */
  async prepare(home: string, ids: string[]): Promise<Outcome<PreparedHistory>[]> {
    const operations = path.join(this.root(), 'operations');
    const jobs = ids.map((id) => ({ id, operation: path.join(operations, crypto.randomUUID()) }));
    return (await this.call('prepare', home, [], { jobs })).results as Outcome<PreparedHistory>[];
  }

  /** Archives (verified ZIP first) or deletes prepared chats; a live or changed chat is left as it is. */
  async retire(home: string, jobs: RetireJob[]): Promise<Outcome<{ id: string; archive?: string; sha256?: string }>[]> {
    const input = {
      jobs: jobs.map(({ prepared, mode, metadata }) => ({
        id: prepared.id,
        operation: prepared.operation,
        plan: prepared.plan,
        sha256: prepared.sha256,
        mode,
        metadata,
      })),
    };
    return (await this.call('retire', home, [], input)).results as Outcome<{
      id: string;
      archive?: string;
      sha256?: string;
    }>[];
  }

  async recover(
    home: string,
  ): Promise<{ entries: ArchiveEntry[]; warnings: { operation: string; error: string }[]; restored: string[] }> {
    return (await this.call('recover', home, ['--operation', this.root()])) as unknown as {
      entries: ArchiveEntry[];
      warnings: { operation: string; error: string }[];
      restored: string[];
    };
  }

  async restore(
    home: string,
    entry: { id: string; operation: string; archive: string; sha256: string },
  ): Promise<void> {
    await this.call('restore', home, [
      '--id',
      entry.id,
      '--operation',
      entry.operation,
      '--archive',
      entry.archive,
      '--sha256',
      entry.sha256,
    ]);
  }
}
