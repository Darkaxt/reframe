// Messages between main and the PTY host. Every start of a session gets a new `instance` number, so that
// events of a process that is still going away are never mistaken for those of its successor.
//
// The host is a process of its own that outlives reframe (so a new reframe can take its sessions over). It listens
// on a named pipe of the profile; each line on it is one JSON message. A connection starts with `hello`, carrying
// the profile's token and the protocol version; the host answers `welcome` (with what it runs) or `refused`.
import crypto from 'node:crypto';
import type { TerminalHostMessage, TerminalViewMessage } from '@shared/ipc';
import type { HookMessage } from '../main/hooks/server';

/** Bumped whenever a message changes incompatibly; a reframe talks only to hosts with its own version. */
export const PROTOCOL_VERSION = 1;

/** Which implementation a host is: the Node host (this folder) or the Rust one (rust/reframe-host). */
export type HostKind = 'node' | 'rust';

/** The kind of a host from its descriptor or welcome; hosts from before 0.10.1-claudegpt.18 do not say: Node. */
export function hostKind(host: { kind?: HostKind }): HostKind {
  return host.kind ?? 'node';
}

/** What a reframe puts with a session for itself, and gets back when it takes the session over. */
export interface SessionMeta {
  wsl?: { distro: string; home: string; claudeSessionId: string };
  launched?: { claudeSessionId: string };
}

export type HostRequest =
  | {
      type: 'spawn';
      sessionId: string;
      instance: number;
      file: string;
      /** A string is the whole command line as it is (Windows; for cmd.exe, which parses its own). */
      args: string[] | string;
      cwd: string;
      env: Record<string, string>;
      cols: number;
      rows: number;
      meta?: SessionMeta;
    }
  | { type: 'write'; sessionId: string; data: string }
  /** Kills the process if still running and forgets the session, if it is still that instance. */
  | { type: 'dispose'; sessionId: string; instance: number }
  /** A view of a session's terminal opens (it gets a snapshot, then live output), takes input, or goes. */
  | { type: 'view-open'; viewId: number; sessionId: string }
  | { type: 'view-message'; viewId: number; message: TerminalViewMessage }
  | { type: 'view-close'; viewId: number }
  /** "End sessions and quit": the host ends whatever still runs and exits. */
  | { type: 'shutdown' }
  /** A request of a service the welcome advertises; answered by `svc-result` with the same id. */
  | SvcRequest;

/** Services a host can offer (`services` in its welcome, with each one's version); added without a protocol bump. */
export interface HostServices {
  /** Past sessions, titles and transcript watching (docs/ptyhost-protocol.md, "Services"). */
  transcripts?: number;
}

export interface SvcRequest {
  type: 'svc';
  id: number;
  service: string;
  method: string;
  params: Record<string, unknown>;
}

export type SvcErrorCode = 'unknown-method' | 'bad-params' | 'io' | 'busy';

export type SvcResult =
  | { type: 'svc-result'; id: number; ok: true; result: unknown }
  | { type: 'svc-result'; id: number; ok: false; error: { code: SvcErrorCode; message: string } };

/** Something a service tells unasked, while a reframe is attached (not held for the next one). */
export interface SvcEvent {
  type: 'svc-event';
  service: string;
  event: string;
  data: unknown;
}

export type HostEvent =
  | { type: 'spawned'; sessionId: string; instance: number; pid: number }
  | { type: 'spawn-failed'; sessionId: string; instance: number; message: string }
  | { type: 'exit'; sessionId: string; instance: number; pid: number; exitCode: number }
  /** An error nobody handled; the host keeps running (ending it would end every session). */
  | { type: 'error'; message: string }
  /** A Claude Code hook (or status line) of one of the host's sessions. */
  | { type: 'hook'; message: HookMessage };

export interface HostSession {
  sessionId: string;
  instance: number;
  pid: number;
  /** Null while it runs. */
  exitCode: number | null;
  meta?: SessionMeta;
}

export interface Hello {
  type: 'hello';
  token: string;
  protocol: number;
}

export type Welcome =
  | {
      type: 'welcome';
      protocol: number;
      version: string;
      pid: number;
      /** The hook endpoint of the host: the sessions' hook settings point there, whichever reframe is attached. */
      hooks: { port: number; token: string };
      sessions: HostSession[];
      kind?: HostKind;
      /** Absent from hosts before 0.10.1-claudegpt.20: they offer none. */
      services?: HostServices;
    }
  | { type: 'refused'; reason: 'token' | 'protocol'; protocol: number };

export type ClientMessage = Hello | HostRequest;
export type ServerMessage =
  | Welcome
  | HostEvent
  | { type: 'view'; viewId: number; message: TerminalHostMessage }
  /** The view's session is gone (disposed): the view is closed. */
  | { type: 'view-closed'; viewId: number }
  /** Another reframe of the profile connected; this connection is closed. */
  | { type: 'replaced' }
  | SvcResult
  | SvcEvent;

/** What a host writes next to itself, so a reframe finds it (and knows its sessions even if it cannot talk to it). */
export interface HostDescriptor {
  pid: number;
  pipe: string;
  protocol: number;
  version: string;
  startedAt: number;
  /** Process ids of its running sessions. */
  sessionPids: number[];
  kind?: HostKind;
}

/** The host's answer to a hello: welcome only with the profile's token and the same protocol version. */
export function checkHello(hello: unknown, token: string): 'ok' | 'token' | 'protocol' {
  const h = hello as Partial<Hello> | null;
  if (!h || h.type !== 'hello' || typeof h.token !== 'string' || !sameToken(h.token, token)) return 'token';
  return h.protocol === PROTOCOL_VERSION ? 'ok' : 'protocol';
}

/** Whether a reframe can talk to a host from its descriptor (a dead host's descriptor is stale, not compatible). */
export function compatible(descriptor: Pick<HostDescriptor, 'protocol'>): boolean {
  return descriptor.protocol === PROTOCOL_VERSION;
}

export function sameToken(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}

/** The pipe of a host: one per profile and host process. */
export function pipeName(profileDir: string, pid: number): string {
  const profile = crypto.createHash('sha256').update(profileDir.toLowerCase()).digest('hex').slice(0, 16);
  return process.platform === 'win32'
    ? `\\\\.\\pipe\\reframe-ptyhost-${profile}-${pid}`
    : `/tmp/reframe-ptyhost-${profile}-${pid}.sock`;
}

/** Splits a stream into JSON lines; a line that is not JSON is dropped. */
export class LineDecoder {
  private buffer = '';

  push(chunk: string): unknown[] {
    this.buffer += chunk;
    const lines = this.buffer.split('\n');
    this.buffer = lines.pop()!;
    const out: unknown[] = [];
    for (const line of lines) {
      if (!line) continue;
      try {
        out.push(JSON.parse(line));
      } catch {
        // not one of ours
      }
    }
    return out;
  }
}

export function encode(message: ClientMessage | ServerMessage): string {
  return `${JSON.stringify(message)}\n`;
}
