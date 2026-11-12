// Loads and saves state.json. Writes are atomic (temp file + rename) and the previous good file is
// kept as state.json.bak, so a crash mid-write or a corrupt file never loses the layout.
// A file that cannot be used is never written over: one from a newer reframe is left alone (nothing is written at
// all, so going back to that version keeps everything), any other is set aside as state.json.rejected-<time>.
// Once a PTY host that serves `state` (since 0.10.1-claudegpt.23) is attached, saves are written by it, the same way
// and to the same file (src/main/state/files.ts, rust/reframe-services/src/state.rs), one at a time and always the
// newest state; a save it cannot make (no host any more, or it failed) is made here. save() returns before such a
// write is done: flush() resolves once it is, and is awaited before anything outside reads the file and on quitting
// (where a host that does not answer in time is given up on and the state is written here). Loading stays here: it comes
// before the host is started (the state says which host). The schema stays here too: the host never reads it.
import fs from 'node:fs';
import path from 'node:path';
import { appStateSchema, defaultState, STATE_VERSION, type AppState } from '@shared/state';
import type { HostServices } from '../../ptyhost/protocol';
import type { LogFn } from '../log';
import { renameWithRetry } from './files';

/** The part of PtyHostClient used here. */
export interface StateHost {
  services: HostServices;
  call(service: string, method: string, params: Record<string, unknown>): Promise<unknown>;
}

export type LoadSource = 'file' | 'backup' | 'default' | 'newer';

export interface LoadResult {
  state: AppState;
  source: LoadSource;
  /** The file could not be used and was set aside under this name, for this reason. */
  rejected?: { file: string; reason: string };
}

/** How many set-aside files are kept. */
const KEEP_REJECTED = 5;

export class StateStore {
  private timer: NodeJS.Timeout | null = null;
  private pending: AppState | null = null;
  /** Size and time of the file as this store last loaded or wrote it: while they match, it is known to be good. */
  private known: { size: number; mtimeMs: number } | null = null;
  /** The file belongs to a newer reframe: nothing is written. */
  private readOnly = false;
  private host: () => StateHost | undefined = () => undefined;
  private log: LogFn | undefined;
  /** The host's revision of the file as last read or written there, and whether that content is a good one. */
  private remote: { revision: number; good: boolean } | null = null;
  /** The newest state not yet written, while host writes are going on. */
  private unsaved: AppState | null = null;
  private writing = false;
  private written: Promise<void> = Promise.resolve();
  /** The state being written through the host (it may be swapped for a newer one on a conflict). */
  private inFlight: AppState | null = null;
  /** The newest state given to save. */
  private newest: AppState | null = null;
  /** The host did not save in time (on flush with a deadline): from then on everything is written here. */
  private abandoned = false;

  constructor(readonly file: string) {}

  /** Saves through this host whenever it serves `state`. */
  useHost(host: () => StateHost | undefined, log?: LogFn): void {
    this.host = host;
    this.log = log;
  }

  private served(): StateHost | null {
    const host = this.host();
    return !this.abandoned && host && (host.services.state ?? 0) >= 1 ? host : null;
  }

  /** Once the saves given so far are written (by the host or here). */
  settle(): Promise<void> {
    return this.written;
  }

  get backupFile(): string {
    return `${this.file}.bak`;
  }

  load(): LoadResult {
    const fromFile = readFile(this.file);
    if (fromFile.state) {
      this.known = stat(this.file);
      return { state: fromFile.state, source: 'file' };
    }
    if (fromFile.newer) {
      this.readOnly = true;
      return { state: defaultState(), source: 'newer' };
    }
    const rejected = fromFile.reason ? { file: this.setAside(), reason: fromFile.reason } : undefined;
    const fromBackup = readFile(this.backupFile);
    if (fromBackup.newer) {
      // The backup is a newer reframe's as well: it must not be written over either.
      this.readOnly = true;
      return { state: defaultState(), source: 'newer', ...(rejected ? { rejected } : {}) };
    }
    if (fromBackup.state) return { state: fromBackup.state, source: 'backup', ...(rejected ? { rejected } : {}) };
    return { state: defaultState(), source: 'default', ...(rejected ? { rejected } : {}) };
  }

  /**
   * Writes immediately (and cancels a pending debounced write); through the host, if it serves `state`, or after
   * the host writes going on, in a moment (see settle). Without them it is written here before this returns.
   */
  save(state: AppState): void {
    this.cancel();
    if (this.readOnly) return;
    this.newest = state;
    if ((!this.writing || this.abandoned) && !this.served()) {
      this.saveHere(state);
      return;
    }
    this.unsaved = state;
    if (this.writing) return;
    this.writing = true;
    this.written = this.drain();
  }

  /** Writes the newest state until none is left: through the host while it serves `state`, else here. */
  private async drain(): Promise<void> {
    for (;;) {
      const state = this.unsaved;
      if (!state) {
        this.writing = false;
        return;
      }
      this.unsaved = null;
      this.inFlight = state;
      const host = this.served();
      try {
        if (host) {
          try {
            await this.saveThere(host);
            continue;
          } catch (error) {
            this.log?.('warn', 'the pty host could not save the state; saving here', { error: String(error) });
          }
        }
        // A newer one given meanwhile is written next; one already written here (a flush that gave up waiting for
        // the host) is not written over with this older one.
        if (!this.unsaved && this.inFlight === this.newest) this.saveHere(this.inFlight);
      } catch (error) {
        this.log?.('error', 'could not save the state', { error: String(error) });
      }
    }
  }

  /** Writes through the host: read there first when its revision is not known, or was not the file's any more. */
  private async saveThere(host: StateHost): Promise<void> {
    for (let attempt = 0; ; attempt++) {
      const content = JSON.stringify(this.inFlight, null, 2);
      if (!this.remote) {
        const read = (await host.call('state', 'read', { file: this.file })) as {
          revision: number;
          content: string | null;
        };
        this.remote = { revision: read.revision, good: read.content !== null && !!parse(read.content).state };
      }
      try {
        const { revision } = (await host.call('state', 'write', {
          file: this.file,
          content,
          revision: this.remote.revision,
          backup: this.remote.good,
        })) as { revision: number };
        this.remote = { revision, good: true };
        this.known = null;
        return;
      } catch (error) {
        this.remote = null;
        // Changed since (written here meanwhile, say, or by another reframe): read again, and write the newest.
        if (attempt > 0 || (error as { code?: unknown }).code !== 'conflict' || !this.served()) throw error;
        // Retried with the newest state.
        if (this.unsaved) {
          this.inFlight = this.unsaved;
          this.unsaved = null;
        }
        this.log?.('info', 'the state file changed since the pty host last wrote it; reading it again', {
          error: String(error),
        });
      }
    }
  }

  private saveHere(state: AppState): void {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const temp = `${this.file}.${process.pid}.tmp`;
    const fd = fs.openSync(temp, 'w');
    try {
      fs.writeSync(fd, JSON.stringify(state, null, 2));
      fs.fsyncSync(fd);
    } finally {
      fs.closeSync(fd);
    }
    if (this.isGood()) fs.copyFileSync(this.file, this.backupFile);
    renameWithRetry(temp, this.file);
    this.known = stat(this.file);
    this.remote = null;
  }

  /** Coalesces frequent saves (window moves, resizes) into one write. */
  saveSoon(state: AppState, delayMs = 500): void {
    this.pending = state;
    if (this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      const next = this.pending;
      this.pending = null;
      if (next) this.save(next);
    }, delayMs);
  }

  /**
   * Resolves once the newest state (a debounced one too) is on disk. With `deadlineMs`, a host that has not written
   * it by then is given up on: the newest state is written here, and so is everything after.
   */
  async flush(deadlineMs?: number): Promise<void> {
    const next = this.pending;
    if (next) this.save(next);
    if (!this.writing || this.abandoned) return;
    const done = this.written.then(() => true);
    if (deadlineMs === undefined) {
      await done;
      return;
    }
    let timer: NodeJS.Timeout | undefined;
    const late = new Promise<false>((resolve) => (timer = setTimeout(() => resolve(false), deadlineMs)));
    const inTime = await Promise.race([done, late]);
    clearTimeout(timer);
    if (inTime) return;
    this.log?.('warn', 'the pty host did not save the state in time; saving here', { deadlineMs });
    this.abandoned = true;
    this.unsaved = null;
    if (this.newest) this.saveHere(this.newest);
  }

  private cancel(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.pending = null;
  }

  /** Whether the file on disk is a good one, worth keeping as the backup; read again only if it changed meanwhile. */
  private isGood(): boolean {
    const now = stat(this.file);
    if (!now) return false;
    if (this.known && now.size === this.known.size && now.mtimeMs === this.known.mtimeMs) return true;
    return !!readFile(this.file).state;
  }

  /** Moves the unusable file out of the way (beyond KEEP_REJECTED such files, the oldest go); its new name. */
  private setAside(): string {
    const dir = path.dirname(this.file);
    const prefix = `${path.basename(this.file)}.rejected-`;
    const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    // Within the same second, numbered on from the highest number there (never one freed by trimming), so that the
    // names sort by age.
    const same = fs.readdirSync(dir).filter((name) => name.startsWith(prefix + stamp));
    const highest = Math.max(0, ...same.map((name) => Number(/-(\d{3})$/.exec(name)?.[1] ?? 1)));
    const target = path.join(dir, prefix + stamp + (same.length > 0 ? `-${String(highest + 1).padStart(3, '0')}` : ''));
    try {
      fs.renameSync(this.file, target);
    } catch {
      // Held open by something: it stays where it is, and the next save will not take it for the backup either.
      return this.file;
    }
    const old = fs
      .readdirSync(dir)
      .filter((name) => name.startsWith(prefix))
      .sort()
      .slice(0, -KEEP_REJECTED);
    for (const name of old) fs.rmSync(path.join(dir, name), { force: true });
    return target;
  }
}

/** The state in a file, or why there is none: missing (no reason), from a newer reframe, or unusable. */
function readFile(file: string): { state?: AppState; newer?: boolean; reason?: string } {
  let text: string;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch {
    return {};
  }
  return parse(text);
}

/** The state in a file's text, or why there is none. */
function parse(text: string): { state?: AppState; newer?: boolean; reason?: string } {
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch (error) {
    return { reason: `not JSON (${(error as Error).message})` };
  }
  const version = (json as { version?: unknown } | null)?.version;
  if (typeof version === 'number' && version > STATE_VERSION) return { newer: true };
  const parsed = appStateSchema.safeParse(json);
  if (parsed.success) return { state: parsed.data };
  const issues = parsed.error.issues.slice(0, 5).map((i) => `${i.path.join('.') || '(file)'}: ${i.message}`);
  return { reason: issues.join('; ') };
}

function stat(file: string): { size: number; mtimeMs: number } | null {
  try {
    const { size, mtimeMs } = fs.statSync(file);
    return { size, mtimeMs };
  } catch {
    return null;
  }
}
