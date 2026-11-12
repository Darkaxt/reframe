// Windows' own Claude Code folder's past sessions and titles, from the PTY host when it serves them (`services.
// transcripts` in its welcome, since 0.10.1-claudegpt.20), so its index and watching outlive reframe; else (an older
// host taken over in a hot swap, or the host not there right now) read here, as before. Both give the same answers:
// the host runs the same code (Node host) or its port (Rust host), and the conformance tests hold them to it.
import { TranscriptIndex, type TranscriptInfo } from './discovery';
import { TranscriptTitles } from './titles';
import { ClaudeWatcher } from './watcher';
import type { FolderWorkspace } from '@shared/folder-workspaces';
import type { PtyHostClient } from '../ptyhost-client';
import type { LogFn } from '../log';

/** What the session list asks of a Claude Code folder's transcripts (TranscriptIndex, or this). */
export type TranscriptLister = Pick<TranscriptIndex, 'list' | 'find' | 'modifiedTimes' | 'invalidate'>;

/** The part of PtyHostClient used here. */
export type TranscriptHost = Pick<PtyHostClient, 'services' | 'call' | 'on'>;

type Projects = Iterable<string> | 'all';
type Listing = Awaited<ReturnType<TranscriptIndex['list']>>;

export class Transcripts implements TranscriptLister {
  private local: TranscriptIndex;
  private localTitles = new TranscriptTitles();
  /** The last title read per Claude session id (null: none), wherever it was read. */
  private titles = new Map<string, string | null>();
  /** Answers came from the host meanwhile: the local index was not kept up to date. */
  private localStale = false;
  /** Watches the transcripts here while the host does not. */
  private localWatcher: ClaudeWatcher | null = null;

  constructor(
    private home: string,
    private host: () => TranscriptHost | undefined,
    private log?: LogFn,
  ) {
    this.local = new TranscriptIndex(home);
  }

  private served(): TranscriptHost | null {
    const host = this.host();
    return host && (host.services.transcripts ?? 0) >= 1 ? host : null;
  }

  /** The host's answer if it serves transcripts and answers; otherwise the local one. */
  private async ask<T>(method: string, params: Record<string, unknown>, local: () => Promise<T>): Promise<T> {
    const host = this.served();
    if (host) {
      try {
        const result = (await host.call('transcripts', method, { home: this.home, ...params })) as T;
        this.localStale = true;
        return result;
      } catch (error) {
        this.log?.('warn', 'the pty host did not answer a transcript request; reading here', {
          method,
          error: String(error),
        });
      }
    }
    if (this.localStale) {
      this.local.invalidate('all');
      this.localStale = false;
    }
    return local();
  }

  async list(options: { query?: string; limit: number; exclude: Set<string>; folder?: string }): Promise<Listing> {
    const { query, limit, exclude, folder } = options;
    const listing = await this.ask('list', { query, limit, exclude: [...exclude], folder }, () =>
      this.local.list(options),
    );
    // The Rust host sorts the folders close to how localeCompare does; this is the order the list always had.
    return { ...listing, folders: [...listing.folders].sort((a: FolderWorkspace, b) => a.name.localeCompare(b.name)) };
  }

  find(sessionId: string): Promise<TranscriptInfo | null> {
    return this.ask('info', { sessionId }, () => this.local.find(sessionId));
  }

  async modifiedTimes(): Promise<Map<string, number>> {
    // The host answers an object (session id → time).
    const times = await this.ask<Record<string, number> | Map<string, number>>('times', {}, () =>
      this.local.modifiedTimes(),
    );
    return times instanceof Map ? times : new Map(Object.entries(times));
  }

  invalidate(projects: Projects): void {
    const list = projects === 'all' ? 'all' : [...projects];
    this.local.invalidate(list);
    void this.served()
      ?.call('transcripts', 'invalidate', { home: this.home, projects: list })
      .catch(() => (this.localStale = true));
  }

  /** What was read last for the session, without reading. */
  cachedTitle(sessionId: string): string | null | undefined {
    return this.titles.get(sessionId);
  }

  async readTitle(sessionId: string): Promise<string | null> {
    const title = await this.ask('title', { sessionId }, () => this.localTitles.read(this.home, sessionId));
    this.titles.set(sessionId, title);
    return title;
  }

  /**
   * Tells `changed` the project folders whose transcripts changed, as the host's watching (or the local one, while
   * the host does not watch) sees them; and 'all' whenever a host attaches, since the list is read afresh then.
   */
  watch(changed: (projects: Set<string> | 'all') => void): void {
    const host = this.host();
    if (!host) return this.watchHere(changed);
    host.on('svc-event', ({ service, event, data }) => {
      const { home, projects } = (data ?? {}) as { home?: string; projects?: string[] | 'all' };
      if (service !== 'transcripts' || event !== 'changed' || home !== this.home || !projects) return;
      changed(projects === 'all' ? 'all' : new Set(projects));
    });
    const attached = () => {
      const served = this.served();
      if (!served) return this.watchHere(changed);
      served.call('transcripts', 'watch', { home: this.home }).then(
        () => {
          this.localWatcher?.stop();
          this.localWatcher = null;
        },
        (error: unknown) => {
          this.log?.('warn', 'the pty host does not watch the transcripts; watching here', { error: String(error) });
          this.watchHere(changed);
        },
      );
    };
    host.on('attached', () => {
      attached();
      changed('all');
    });
    attached();
  }

  private watchHere(changed: (projects: Set<string> | 'all') => void): void {
    if (this.localWatcher) return;
    this.localWatcher = new ClaudeWatcher(this.home, { folders: ['projects'] });
    this.localWatcher.on('changed', ({ projects }) => {
      this.local.invalidate(projects);
      changed(projects);
    });
    this.localWatcher.start();
  }

  stop(): void {
    this.localWatcher?.stop();
    this.localWatcher = null;
  }
}
