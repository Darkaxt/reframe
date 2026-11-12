// The Node host's `sessions` service (docs/ptyhost-protocol.md, "Services"): each session's status from its Claude
// Code hooks and its session registry entry, as SessionManager (src/main/sessions.ts) works it out in-process, with
// reframe's own registry readers. The Rust host's is rust/reframe-sessions; the conformance tests hold both to
// SessionManager. What depends on reframe (who ended a session, how an early exit counts, whether to show a
// notification) stays there: the host tells the status and what to announce, never text.
import fs from 'node:fs';
import path from 'node:path';
import type { HookMessage } from '../main/hooks/server';
import {
  findRegistryEntry,
  isRunningEntry,
  readRegistryEntry,
  readRegistryEntryAsync,
  type RegistryEntry,
} from '../main/claude/registry';
import { processStart } from '../main/process';
import { looksLikeClaude } from '../main/wsl/distros';
import type { SessionMeta } from './protocol';

export type HostStatus = 'starting' | 'idle' | 'busy' | 'waiting' | 'ended';
export type NotifyKind = 'done' | 'waiting';

/** A `sessions.status` event's data, and an item of `sessions.list`. */
export interface SessionStatusData {
  sessionId: string;
  instance: number;
  status: HostStatus;
  registry?: RegistryEntry;
  notify?: NotifyKind;
  /** Claude Code's session id as last known: the registry entry's, a hook's, or the spawn's meta (since .25). */
  claudeSessionId?: string;
  /** The host's status revision when this changed (since .25): higher than any before, one counter for every session. */
  revision: number;
}

/** `sessions.list`: every followed session, and the revision of the newest change in it. */
export interface SessionStatusList {
  sessions: SessionStatusData[];
  revision: number;
}

type Lookup =
  | { kind: 'direct'; home: string }
  | { kind: 'launched'; home: string; claudeSessionId: string }
  | { kind: 'wsl'; home: string; share: string; distro: string; claudeSessionId: string };

/** Where a session's registry entry is, from its spawn's meta; null: reframe follows the session itself. */
export function lookupOf(meta: SessionMeta | undefined): Lookup | null {
  const text = (v: unknown) => (typeof v === 'string' && v ? v : undefined);
  if (meta?.wsl) {
    const { home, share, distro, claudeSessionId } = meta.wsl;
    const [h, s, d, c] = [text(home), text(share), text(distro), text(claudeSessionId)];
    return h && s && d && c ? { kind: 'wsl', home: h, share: s, distro: d, claudeSessionId: c } : null;
  }
  const home = text(meta?.claudeHome);
  if (!home) return null;
  const launched = text(meta?.launched?.claudeSessionId);
  return launched ? { kind: 'launched', home, claudeSessionId: launched } : { kind: 'direct', home };
}

interface Tracked {
  sessionId: string;
  instance: number;
  pid: number;
  lookup: Lookup;
  /** Claude's own process id once its entry was found (through cmd.exe: claude's; in WSL: the Linux one). */
  found?: number;
  status: HostStatus;
  registryStatus?: string;
  last?: RegistryEntry;
  startedAt: number;
  procStart: string | null;
  reading: boolean;
  /** Announced while no reframe was attached: the next one gets it with the statuses. */
  held?: NotifyKind;
  claudeSessionId?: string;
  /** The revision of its last change. */
  revision: number;
}

const POLL_MS = 1000;
const same = (a: RegistryEntry | undefined, b: RegistryEntry) => JSON.stringify(a) === JSON.stringify(b);

function start(pid: number): string | null {
  try {
    return processStart(pid);
  } catch {
    return null;
  }
}

export class SessionStatusService {
  private tracked = new Map<string, Tracked>();
  /** Bumped by every change of any session's status data (docs/ptyhost-protocol.md, "sessions"). */
  private revision = 0;
  private poller: NodeJS.Timeout;

  /** `emit`: a status for the attached reframe; `attached`: whether one is. */
  constructor(
    private emit: (data: SessionStatusData) => void,
    private attached: () => boolean,
  ) {
    this.poller = setInterval(() => this.poll(), POLL_MS);
    this.poller.unref();
  }

  spawned(sessionId: string, instance: number, pid: number, meta: SessionMeta | undefined): void {
    this.tracked.delete(sessionId);
    const lookup = lookupOf(meta);
    if (!lookup) return;
    this.tracked.set(sessionId, {
      sessionId,
      instance,
      pid,
      lookup,
      status: 'starting',
      startedAt: Date.now(),
      procStart: start(pid),
      reading: false,
      claudeSessionId: lookup.kind === 'direct' ? undefined : lookup.claudeSessionId,
      revision: ++this.revision,
    });
  }

  /** Claude Code's session id of a followed session, as last known. */
  claudeSessionId(sessionId: string): string | undefined {
    return this.tracked.get(sessionId)?.claudeSessionId;
  }

  exited(sessionId: string, instance: number): void {
    const t = this.tracked.get(sessionId);
    if (!t || t.instance !== instance || t.status === 'ended') return;
    t.status = 'ended';
    this.tell(t);
  }

  disposed(sessionId: string, instance: number | null): void {
    const t = this.tracked.get(sessionId);
    if (t && (instance === null || t.instance === instance)) this.tracked.delete(sessionId);
  }

  /** `take`: the primary asks, and gets what was announced while none was attached (an observer does not). */
  list(take = true): SessionStatusList {
    const sessions = [...this.tracked.values()].map((t) => {
      const notify = take ? t.held : undefined;
      if (take) t.held = undefined;
      return this.data(t, notify);
    });
    return { sessions, revision: this.revision };
  }

  hook({ sessionId, event, payload }: HookMessage): void {
    const t = this.tracked.get(sessionId);
    if (!t || t.status === 'ended') return;
    const claudeId = payload?.session_id;
    let idChanged = false;
    if (typeof claudeId === 'string' && claudeId && claudeId !== t.claudeSessionId) {
      t.claudeSessionId = claudeId;
      idChanged = true;
    }
    let told = false;
    if (event === 'UserPromptSubmit' && this.set(t, 'busy')) {
      this.tell(t);
      told = true;
    }
    if (event === 'Stop') {
      this.set(t, 'idle');
      this.tell(t, 'done');
      told = true;
    }
    // idle_prompt repeats "waiting for input" a minute after Stop; that was already announced.
    if (event === 'Notification' && payload.notification_type !== 'idle_prompt') {
      this.set(t, 'waiting');
      this.tell(t, 'waiting');
      told = true;
    }
    if (idChanged && !told) this.tell(t);
  }

  private data(t: Tracked, notify?: NotifyKind): SessionStatusData {
    return {
      sessionId: t.sessionId,
      instance: t.instance,
      status: t.status,
      ...(t.last ? { registry: t.last } : {}),
      ...(notify ? { notify } : {}),
      ...(t.claudeSessionId ? { claudeSessionId: t.claudeSessionId } : {}),
      revision: t.revision,
    };
  }

  /** A change: a new revision, told to whoever is attached (observers too); an announcement waits for the primary. */
  private tell(t: Tracked, notify?: NotifyKind): void {
    t.revision = ++this.revision;
    if (notify && !this.attached()) t.held = notify;
    this.emit(this.data(t, notify));
  }

  private set(t: Tracked, status: HostStatus): boolean {
    if (t.status === status) return false;
    t.status = status;
    return true;
  }

  private poll(): void {
    for (const t of this.tracked.values()) {
      if (t.reading || t.status === 'ended') continue;
      t.reading = true;
      void this.read(t)
        .catch(() => null)
        .then((entry) => {
          t.reading = false;
          if (this.tracked.get(t.sessionId) !== t || t.status === 'ended' || !entry) return;
          this.follow(t, entry);
        });
    }
  }

  private async read(t: Tracked): Promise<RegistryEntry | null> {
    const { lookup } = t;
    if (lookup.kind === 'direct') return readRegistryEntry(lookup.home, t.pid);
    const known = t.found ? await readRegistryEntryAsync(lookup.home, t.found) : null;
    if (known) return known;
    const alive =
      lookup.kind === 'launched'
        ? async (e: RegistryEntry) => isRunningEntry(e, start)
        : async (e: RegistryEntry) =>
            looksLikeClaude(
              await fs.promises
                .readFile(path.win32.join(lookup.share, lookup.distro, 'proc', String(e.pid), 'cmdline'), 'utf8')
                .catch(() => ''),
            );
    const found = await findRegistryEntry(lookup.home, lookup.claudeSessionId, alive);
    t.found = found?.pid;
    return found;
  }

  private stale(t: Tracked, entry: RegistryEntry): boolean {
    if (t.lookup.kind !== 'direct') return false;
    return entry.procStart && t.procStart
      ? entry.procStart !== t.procStart
      : entry.startedAt !== undefined && entry.startedAt < t.startedAt - 2_000;
  }

  /** SessionManager.followRegistry, for the status. */
  private follow(t: Tracked, entry: RegistryEntry): void {
    if (this.stale(t, entry)) return;
    const entryChanged = !same(t.last, entry);
    t.last = entry;
    if (entry.sessionId) t.claudeSessionId = entry.sessionId;
    let changed = false;
    let notify: NotifyKind | undefined;
    // Follow the registry only when it changes, so it does not undo a fresher hook-based status.
    if (entry.status && entry.status !== t.registryStatus) {
      const before = t.registryStatus;
      t.registryStatus = entry.status;
      changed = this.set(t, entry.status);
      // Without hooks (WSL), the registry is also what tells that Claude is done or needs the user.
      if (t.lookup.kind === 'wsl' && entry.status === 'waiting') notify = 'waiting';
      if (t.lookup.kind === 'wsl' && entry.status === 'idle' && before === 'busy') notify = 'done';
    } else if (t.status === 'starting') {
      changed = this.set(t, entry.status ?? 'idle');
    }
    if (entryChanged || changed || notify) this.tell(t, notify);
  }
}
