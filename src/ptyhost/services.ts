// The Node host's services (docs/ptyhost-protocol.md, "Services"): the same `transcripts`, `quota`, `clients`,
// `launch` and `state` services as the Rust host's (rust/reframe-host/src/transcripts.rs, services.rs), made of reframe's own
// modules. The conformance tests hold both to them.
import fs from 'node:fs';
import { TranscriptIndex } from '../main/claude/discovery';
import { TranscriptTitles } from '../main/claude/titles';
import { ClaudeWatcher } from '../main/claude/watcher';
import { adoptedClient } from '../main/client-adoption';
import { evaluateLaunchConfig } from '../main/launch-config';
import { coordinatorExecutable, readCodexUsage, userHasStatusLine } from '../main/quota';
import { StateConflict, StateFiles } from '../main/state/files';
import type { HostServices, SvcErrorCode, SvcEvent } from './protocol';

export const SERVICES: Required<HostServices> = {
  transcripts: 1,
  quota: 1,
  clients: 1,
  launch: 1,
  sessions: 1,
  state: 1,
};
/** Requests being worked on at once; up to MAX_WAITING more wait their turn (a burst on reframe's start, say), and
 * beyond that they are answered `busy`. */
const MAX_IN_FLIGHT = 8;
const MAX_WAITING = 64;

export class SvcError extends Error {
  constructor(
    readonly code: SvcErrorCode,
    message: string,
  ) {
    super(message);
  }
}

interface Home {
  index: TranscriptIndex;
  titles: TranscriptTitles;
  watcher?: ClaudeWatcher;
}

const text = (params: Record<string, unknown>, key: string): string | undefined => {
  const value = params[key];
  if (value === undefined || value === null) return undefined;
  if (typeof value !== 'string') throw new SvcError('bad-params', `${key} must be a string`);
  return value;
};

const required = (params: Record<string, unknown>, key: string): string => {
  const value = text(params, key);
  if (!value) throw new SvcError('bad-params', `${key} is required`);
  return value;
};

export class TranscriptService {
  private homes = new Map<string, Home>();
  private limit = new Limit();

  /** `emit`: a service event for the attached reframe (dropped without one). */
  constructor(private emit: (event: Omit<SvcEvent, 'type'>) => void) {}

  private home(home: string): Home {
    let entry = this.homes.get(home);
    if (!entry) this.homes.set(home, (entry = { index: new TranscriptIndex(home), titles: new TranscriptTitles() }));
    return entry;
  }

  async call(method: string, params: Record<string, unknown>): Promise<unknown> {
    const home = this.home(required(params, 'home'));
    switch (method) {
      case 'list': {
        const { limit } = params;
        if (typeof limit !== 'number') throw new SvcError('bad-params', 'limit must be a number');
        const { exclude } = params;
        if (exclude !== undefined && exclude !== null && !Array.isArray(exclude))
          throw new SvcError('bad-params', 'exclude must be a list');
        const options = {
          query: text(params, 'query'),
          limit,
          exclude: new Set((exclude ?? []).filter((e): e is string => typeof e === 'string')),
          folder: text(params, 'folder'),
        };
        return this.busy(() => home.index.list(options));
      }
      case 'info': {
        const sessionId = required(params, 'sessionId');
        return this.busy(() => home.index.find(sessionId));
      }
      case 'times':
        return this.busy(async () => Object.fromEntries(await home.index.modifiedTimes()));
      case 'title': {
        const sessionId = required(params, 'sessionId');
        return this.busy(() => home.titles.read(required(params, 'home'), sessionId));
      }
      case 'invalidate': {
        const { projects } = params;
        if (projects === 'all') home.index.invalidate('all');
        else if (Array.isArray(projects)) home.index.invalidate(projects.filter((p) => typeof p === 'string'));
        else throw new SvcError('bad-params', 'projects must be a list or "all"');
        return null;
      }
      case 'watch':
        this.watch(required(params, 'home'), home);
        return null;
      default:
        throw new SvcError('unknown-method', `transcripts.${method}`);
    }
  }

  private busy<T>(work: () => Promise<T>): Promise<T> {
    return this.limit.run('transcript', work);
  }

  /** The transcripts only; reframe watches the session registry itself. */
  private watch(path: string, home: Home): void {
    if (home.watcher) return;
    if (!fs.existsSync(path)) throw new SvcError('io', `${path} is not there`);
    home.watcher = new ClaudeWatcher(path, { folders: ['projects'] });
    home.watcher.on('changed', ({ projects }) => {
      home.index.invalidate(projects);
      this.emit({
        service: 'transcripts',
        event: 'changed',
        data: { home: path, projects: projects === 'all' ? 'all' : [...projects].sort() },
      });
    });
    home.watcher.start();
  }
}

/** `quota`, `clients` and `launch`: asked when reframe needs them (no timers here); at most 8 at once. */
export class ReadService {
  private limit = new Limit();

  async call(service: string, method: string, params: Record<string, unknown>): Promise<unknown> {
    switch (`${service}.${method}`) {
      case 'quota.codex': {
        const localAppData = text(params, 'localAppData');
        const { timeoutMs } = params;
        if (timeoutMs !== undefined && timeoutMs !== null && typeof timeoutMs !== 'number')
          throw new SvcError('bad-params', 'timeoutMs must be a number');
        return this.busy(service, async () => {
          const exe = coordinatorExecutable(localAppData ?? '');
          return exe
            ? { coordinator: true, usage: await readCodexUsage(exe, timeoutMs ?? undefined) }
            : { coordinator: false, usage: null };
        });
      }
      case 'quota.statusLine': {
        const claudeHome = required(params, 'claudeHome');
        return this.busy(service, async () => userHasStatusLine(claudeHome));
      }
      case 'clients.adopted': {
        const record = required(params, 'record');
        const provider = required(params, 'provider') as Parameters<typeof adoptedClient>[1];
        return this.busy(service, async () => adoptedClient(record, provider));
      }
      case 'launch.evaluate': {
        const file = required(params, 'file');
        return this.busy(service, async () => evaluateLaunchConfig(file));
      }
      default:
        throw new SvcError('unknown-method', `${service}.${method}`);
    }
  }

  private busy<T>(service: string, work: () => Promise<T>): Promise<T> {
    return this.limit.run(service, work);
  }
}

/** Runs at most `running` at once, queues up to `waiting` more in order, and refuses the rest (`busy`). */
export class Limit {
  private active = 0;
  private queue: (() => void)[] = [];

  constructor(
    private running = MAX_IN_FLIGHT,
    private waiting = MAX_WAITING,
  ) {}

  async run<T>(what: string, work: () => Promise<T>): Promise<T> {
    if (this.active >= this.running) {
      if (this.queue.length >= this.waiting) throw new SvcError('busy', `too many ${what} requests at once`);
      await new Promise<void>((resolve) => this.queue.push(resolve));
    } else {
      this.active++;
    }
    try {
      return await work();
    } finally {
      // The place goes to the next one waiting, if any.
      const next = this.queue.shift();
      if (next) next();
      else this.active--;
    }
  }
}

/** `state`: reframe's state.json, read and written one at a time, in the order asked (src/main/state/files.ts). */
export class StateService {
  private files = new StateFiles();
  private limit = new Limit(1);

  async call(method: string, params: Record<string, unknown>): Promise<unknown> {
    const file = required(params, 'file');
    switch (method) {
      case 'read':
        return this.limit.run('state', async () => this.files.read(file));
      case 'write': {
        const { content, revision, backup } = params;
        if (typeof content !== 'string') throw new SvcError('bad-params', 'content must be a string');
        if (typeof revision !== 'number' || !Number.isInteger(revision) || revision < 0)
          throw new SvcError('bad-params', 'revision must be a number');
        if (typeof backup !== 'boolean') throw new SvcError('bad-params', 'backup must be true or false');
        return this.limit.run('state', async () => {
          try {
            return { revision: this.files.write(file, content, revision, backup) };
          } catch (error) {
            if (error instanceof StateConflict) throw new SvcError('conflict', error.message);
            throw new SvcError('io', String(error));
          }
        });
      }
      default:
        throw new SvcError('unknown-method', `state.${method}`);
    }
  }
}
