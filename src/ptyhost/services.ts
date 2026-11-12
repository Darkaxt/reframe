// The Node host's services (docs/ptyhost-protocol.md, "Services"): the same `transcripts`, `quota`, `clients` and
// `launch` services as the Rust host's (rust/reframe-host/src/transcripts.rs, services.rs), made of reframe's own
// modules. The conformance tests hold both to them.
import fs from 'node:fs';
import { TranscriptIndex } from '../main/claude/discovery';
import { TranscriptTitles } from '../main/claude/titles';
import { ClaudeWatcher } from '../main/claude/watcher';
import { adoptedClient } from '../main/client-adoption';
import { evaluateLaunchConfig } from '../main/launch-config';
import { coordinatorExecutable, readCodexUsage, userHasStatusLine } from '../main/quota';
import type { HostServices, SvcErrorCode, SvcEvent } from './protocol';

export const SERVICES: Required<HostServices> = { transcripts: 1, quota: 1, clients: 1, launch: 1, sessions: 1 };
/** Requests being worked on at once; more are answered `busy`. */
const MAX_IN_FLIGHT = 8;

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
  private inFlight = 0;

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

  private async busy<T>(work: () => Promise<T>): Promise<T> {
    if (this.inFlight >= MAX_IN_FLIGHT) throw new SvcError('busy', 'too many transcript requests at once');
    this.inFlight++;
    try {
      return await work();
    } finally {
      this.inFlight--;
    }
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
  private inFlight = 0;

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

  private async busy<T>(service: string, work: () => Promise<T>): Promise<T> {
    if (this.inFlight >= MAX_IN_FLIGHT) throw new SvcError('busy', `too many ${service} requests at once`);
    this.inFlight++;
    try {
      return await work();
    } finally {
      this.inFlight--;
    }
  }
}
