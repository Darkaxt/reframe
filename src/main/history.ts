import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
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

/** Installation-bound adapter. The native helper supplies exact-handle retirement; no shell is involved. */
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

  async call(
    command: string,
    home: string,
    id: string,
    operation: string,
    extra: string[] = [],
  ): Promise<Record<string, unknown>> {
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
      const child = spawn(
        python,
        ['-I', '-B', backend, command, '--home', home, '--id', id, '--operation', operation, ...extra],
        {
          windowsHide: true,
          cwd: app.getPath('userData'),
          env: {
            ...process.env,
            TEMP: process.env.REFRAME_RUNTIME || process.env.TEMP,
            TMP: process.env.REFRAME_RUNTIME || process.env.TMP,
          },
          stdio: ['ignore', 'pipe', 'pipe'],
        },
      );
      let stdout = '',
        stderr = '';
      child.stdout.on('data', (b) => {
        stdout += String(b);
      });
      child.stderr.on('data', (b) => {
        if (stderr.length < 8192) stderr += String(b);
      });
      child.on('error', reject);
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
              Object.assign(Error(packet.error || `Chat operation incomplete. Exact receipt: ${operation}`), {
                code: packet.code,
              }),
            );
          else resolve(packet.result);
        } catch (error) {
          reject(
            Error(
              `Chat operation failed; originals/recovery receipts remain at ${operation}. ${stderr || String(error)}`,
            ),
          );
        }
      });
    });
  }

  async prepare(home: string, id: string): Promise<PreparedHistory> {
    const operation = path.join(this.root(), 'operations', crypto.randomUUID());
    return { ...(await this.call('prepare', home, id, operation)), operation } as unknown as PreparedHistory;
  }
  async check(home: string, id: string): Promise<void> {
    await this.call('check', home, id, this.root());
  }
  async recover(
    home: string,
  ): Promise<{ entries: ArchiveEntry[]; warnings: { operation: string; error: string }[]; restored: string[] }> {
    return (await this.call('recover', home, '00000000-0000-4000-8000-000000000000', this.root())) as unknown as {
      entries: ArchiveEntry[];
      warnings: { operation: string; error: string }[];
      restored: string[];
    };
  }
  async archive(
    home: string,
    plan: PreparedHistory,
    metadata: HistoryMetadata,
  ): Promise<{ archive: string; sha256: string }> {
    const file = path.join(plan.operation, 'metadata.json');
    fs.writeFileSync(file, JSON.stringify(metadata), { flag: 'wx' });
    return (await this.call('archive', home, plan.id, plan.operation, [
      '--plan',
      plan.plan,
      '--sha256',
      plan.sha256,
      '--metadata',
      file,
    ])) as unknown as { archive: string; sha256: string };
  }
  async delete(home: string, plan: PreparedHistory): Promise<void> {
    await this.call('delete', home, plan.id, plan.operation, ['--plan', plan.plan, '--sha256', plan.sha256]);
  }
  async restore(
    home: string,
    entry: { id: string; operation: string; archive: string; sha256: string },
  ): Promise<void> {
    await this.call('restore', home, entry.id, entry.operation, ['--archive', entry.archive, '--sha256', entry.sha256]);
  }
}
