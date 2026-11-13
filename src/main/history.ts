import path from 'node:path';
import { app } from 'electron';
import { isArchiveRoot } from './archive-root';
import {
  historyPython,
  prepareInput,
  retireInput,
  runHistoryHelper,
  type ArchiveEntry,
  type Outcome,
  type PreparedHistory,
  type RetireJob,
} from './history-helper';

export type { ArchiveEntry, HistoryMetadata, Outcome, PreparedHistory, RetireJob } from './history-helper';

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
    historyPython(process.env);
    const backend = app.isPackaged
      ? path.join(process.resourcesPath, 'history_backend.py')
      : path.join(app.getAppPath(), 'maintenance', 'history_backend.py');
    return runHistoryHelper({ backend, cwd: app.getPath('userData'), env: process.env }, command, home, extra, input);
  }

  /** Plans (reads only) each chat's exact files, each in its own operation folder. */
  async prepare(home: string, ids: string[]): Promise<Outcome<PreparedHistory>[]> {
    return (await this.call('prepare', home, [], prepareInput(this.root(), ids))).results as Outcome<PreparedHistory>[];
  }

  /** Archives (verified ZIP first) or deletes prepared chats; a live or changed chat is left as it is. */
  async retire(home: string, jobs: RetireJob[]): Promise<Outcome<{ id: string; archive?: string; sha256?: string }>[]> {
    const input = retireInput(jobs);
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
