// The Node host's services (docs/ptyhost-protocol.md, "Services"): the same `transcripts`, `quota`, `clients`,
// `launch`, `state`, `cleanup`, `wsl` and `history` services as the Rust host's (rust/reframe-host/src/transcripts.rs, services.rs), made of reframe's own
// modules. The conformance tests hold both to them.
import fs from 'node:fs';
import path from 'node:path';
import { TranscriptIndex } from '../main/claude/discovery';
import { TranscriptTitles } from '../main/claude/titles';
import { ClaudeWatcher } from '../main/claude/watcher';
import { isArchiveRoot } from '../main/archive-root';
import { cleanUp, endOrphans } from '../main/cleanup';
import { adoptedClient } from '../main/client-adoption';
import { prepareInput, retireInput, runHistoryHelper, type HelperRun, type RetireJob } from '../main/history-helper';
import { evaluateLaunchConfig } from '../main/launch-config';
import { realPlanFs, spawnPlan, spawnPlanInputProblem, type SpawnPlanInput } from '../main/claude/spawn-plan';
import { coordinatorExecutable, readCodexUsage, userHasStatusLine } from '../main/quota';
import { StateConflict, StateFiles } from '../main/state/files';
import { linuxPathOf, parseWslPath, wslWindowsPath } from '@shared/wsl';
import { WslDistros, type WslCommand } from '../main/wsl/distros';
import type { HostServices, SvcErrorCode, SvcEvent } from './protocol';

export const SERVICES: Required<HostServices> = {
  transcripts: 1,
  quota: 1,
  clients: 1,
  launch: 2,
  sessions: 2,
  state: 1,
  observer: 1,
  cleanup: 1,
  wsl: 1,
  history: 1,
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
      case 'launch.spawnPlan': {
        const problem = spawnPlanInputProblem(params);
        if (problem) throw new SvcError('bad-params', problem);
        return this.busy(service, async () => {
          const input = { ...params } as unknown as SpawnPlanInput & { adoption?: unknown };
          // An adoption record given instead of its answer is read here, as clients.adopted reads it.
          if (input.adopted == null && typeof input.adoption === 'string' && input.adoption) {
            input.adopted = adoptedClient(input.adoption, input.session.launch?.provider ?? 'chatgpt');
          }
          return spawnPlan(input, realPlanFs);
        });
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

const absolute = (params: Record<string, unknown>, key: string): string => {
  const value = required(params, key);
  if (!path.win32.isAbsolute(value)) throw new SvcError('bad-params', `${key} must be an absolute path`);
  return value;
};

const pidOf = (params: Record<string, unknown>, key = 'pid'): number => {
  const value = params[key];
  if (typeof value !== 'number' || !Number.isInteger(value) || value <= 0)
    throw new SvcError('bad-params', `${key} must be a process id`);
  return value;
};

/** A distribution's name: a plain name (never a path that would reach past the share). */
const distroOf = (params: Record<string, unknown>): string => {
  const name = required(params, 'distro');
  if (/[\\/]/.test(name) || name === '.' || name === '..')
    throw new SvcError('bad-params', "distro must be a distribution's name");
  return name;
};

const list = (params: Record<string, unknown>, key: string): unknown[] => {
  const value = params[key];
  if (!Array.isArray(value)) throw new SvcError('bad-params', `${key} must be a list`);
  return value;
};

const strings = (params: Record<string, unknown>, key: string): string[] => {
  const value = list(params, key);
  if (value.some((v) => typeof v !== 'string')) throw new SvcError('bad-params', `${key} must be a list of strings`);
  return value as string[];
};

/** A wsl.exe given instead of the environment's: `{ file, prefixArgs, env }` as `wslCommand` gives it. */
function wslWhere(params: Record<string, unknown>): { command?: WslCommand; share?: string } {
  const share = text(params, 'share');
  const command = params.command;
  if (command === undefined || command === null) return { share };
  const c = command as Record<string, unknown>;
  const ok =
    typeof c === 'object' &&
    typeof c.file === 'string' &&
    !!c.file &&
    Array.isArray(c.prefixArgs) &&
    c.prefixArgs.every((a) => typeof a === 'string') &&
    (c.env === undefined || (typeof c.env === 'object' && c.env !== null && !Array.isArray(c.env)));
  if (!ok) throw new SvcError('bad-params', 'command must be { file, prefixArgs, env }');
  const env = Object.fromEntries(
    Object.entries((c.env ?? {}) as Record<string, unknown>).filter(
      (e): e is [string, string] => typeof e[1] === 'string',
    ),
  );
  return { share, command: { file: c.file as string, prefixArgs: c.prefixArgs as string[], env } };
}

/** history_backend.py and the folder it runs in, as reframe gives them; the Python is the host's own pinned one. */
function helperRun(params: Record<string, unknown>): HelperRun {
  const backend = absolute(params, 'backend');
  if (path.win32.basename(backend).toLowerCase() !== 'history_backend.py')
    throw new SvcError('bad-params', 'backend must be history_backend.py');
  return { backend, cwd: absolute(params, 'cwd'), env: process.env };
}

function archiveRoot(params: Record<string, unknown>): string {
  const root = required(params, 'root');
  if (!isArchiveRoot(root, 'win32'))
    throw new SvcError('bad-params', 'root must be an absolute folder on a non-C drive');
  return root;
}

/** A failed helper run (or a helper that cannot be run): its message as an `io` error. */
const helperFailed = (error: unknown): never => {
  throw new SvcError('io', error instanceof Error ? error.message : String(error));
};

/**
 * `cleanup`, `wsl` and `history` (docs/ptyhost-protocol.md): reframe's own cleanup.ts, wsl/distros.ts and
 * history-helper.ts, run in the host. At most 8 at once (`wsl` and `history` start processes).
 */
export class JobService {
  private limit = new Limit();
  private distros = new Map<string, WslDistros>();

  async call(service: string, method: string, params: Record<string, unknown>): Promise<unknown> {
    switch (`${service}.${method}`) {
      case 'cleanup.files': {
        const drops = absolute(params, 'drops');
        const hooks = absolute(params, 'hooks');
        const ids = new Set(strings(params, 'sessionIds'));
        const { now } = params;
        if (now !== undefined && now !== null && typeof now !== 'number')
          throw new SvcError('bad-params', 'now must be a number');
        return this.busy(service, () => cleanUp(drops, hooks, ids, now ?? undefined));
      }
      case 'cleanup.orphans': {
        const claudeHome = absolute(params, 'claudeHome');
        const sessions = list(params, 'sessions').map((s) => {
          const record = (s && typeof s === 'object' ? s : {}) as Record<string, unknown>;
          return { pid: pidOf(record), claudeSessionId: required(record, 'claudeSessionId') };
        });
        const hosted = new Set(list(params, 'hosted').map((pid) => pidOf({ pid })));
        return this.busy(service, async () => ({ killed: await endOrphans(claudeHome, sessions, hosted) }));
      }
      case 'wsl.list': {
        const wsl = this.wsl(params);
        return this.busy(service, () => wsl.list());
      }
      case 'wsl.running': {
        const wsl = this.wsl(params);
        return this.busy(service, () => wsl.running(0));
      }
      case 'wsl.info': {
        const distro = distroOf(params);
        const wsl = this.wsl(params);
        return this.busy(service, () => wsl.info(distro));
      }
      case 'wsl.isClaude': {
        const distro = distroOf(params);
        const pid = pidOf(params);
        const wsl = this.wsl(params);
        return this.busy(service, () => wsl.isClaude(distro, pid));
      }
      case 'wsl.stopClaude': {
        const distro = distroOf(params);
        const pid = pidOf(params);
        const wsl = this.wsl(params);
        return this.busy(service, () => wsl.stopClaude(distro, pid));
      }
      case 'wsl.parsePath':
        return parseWslPath(required(params, 'path'), text(params, 'share'));
      case 'wsl.windowsPath':
        return wslWindowsPath(required(params, 'distro'), required(params, 'path'), required(params, 'share'));
      case 'wsl.linuxPath':
        return linuxPathOf(
          required(params, 'path'),
          required(params, 'distro'),
          required(params, 'mountRoot'),
          text(params, 'share'),
        );
      case 'history.prepare': {
        const run = helperRun(params);
        const home = required(params, 'home');
        const input = prepareInput(archiveRoot(params), strings(params, 'ids'));
        return this.busy(
          service,
          async () => (await runHistoryHelper(run, 'prepare', home, [], input).catch(helperFailed)).results,
        );
      }
      case 'history.retire': {
        const run = helperRun(params);
        const home = required(params, 'home');
        const jobs = list(params, 'jobs') as RetireJob[];
        const valid = jobs.every(
          (j) =>
            !!j &&
            typeof j === 'object' &&
            (j.mode === 'archive' || j.mode === 'delete') &&
            !!j.prepared &&
            typeof j.prepared === 'object' &&
            !!j.metadata &&
            typeof j.metadata === 'object',
        );
        if (!valid) throw new SvcError('bad-params', 'jobs must be { prepared, mode: archive | delete, metadata }');
        return this.busy(
          service,
          async () => (await runHistoryHelper(run, 'retire', home, [], retireInput(jobs)).catch(helperFailed)).results,
        );
      }
      case 'history.recover': {
        const run = helperRun(params);
        const home = required(params, 'home');
        const root = archiveRoot(params);
        return this.busy(service, () =>
          runHistoryHelper(run, 'recover', home, ['--operation', root]).catch(helperFailed),
        );
      }
      case 'history.restore': {
        const run = helperRun(params);
        const home = required(params, 'home');
        const extra = ['id', 'operation', 'archive', 'sha256'].flatMap((key) => [`--${key}`, required(params, key)]);
        return this.busy(service, async () => {
          await runHistoryHelper(run, 'restore', home, extra).catch(helperFailed);
          return null;
        });
      }
      default:
        throw new SvcError('unknown-method', `${service}.${method}`);
    }
  }

  /** One WslDistros per wsl.exe and share, which keeps what each distribution answered as reframe's does. */
  private wsl(params: Record<string, unknown>): WslDistros {
    const where = wslWhere(params);
    const key = JSON.stringify([where.share ?? null, where.command ?? null]);
    let distros = this.distros.get(key);
    if (!distros) this.distros.set(key, (distros = new WslDistros(() => {}, where)));
    return distros;
  }

  private busy<T>(service: string, work: () => Promise<T>): Promise<T> {
    return this.limit.run(service, work);
  }
}
