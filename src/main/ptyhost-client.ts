// Main-process side of the PTY host. The host is a process of its own that outlives reframe: on start, reframe
// attaches to the profile's running host (and takes over its sessions) or starts one. Should the host die (it runs
// native code: node-pty and ConPTY), its sessions are gone, but a new host is started right away so they can be
// started again; after a few deaths in a row it is left down instead of being restarted in a loop.
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { MessageChannelMain, type MessagePortMain, type WebContents } from 'electron';
import { IPC, type TerminalViewMessage } from '@shared/ipc';
import {
  compatible,
  encode,
  hostKind,
  LineDecoder,
  PROTOCOL_VERSION,
  type HostDescriptor,
  type HostEvent,
  type HostKind,
  type HostRequest,
  type HostSession,
  type ServerMessage,
  type Welcome,
} from '../ptyhost/protocol';
import { ensureToken, readDescriptors } from './ptyhost-files';
import { isAlive, killTree } from './process';
import { win32 } from './win32';

/** Starts the host so that it outlives reframe (see Win32.spawnDetached; elsewhere a detached child). */
function spawnDetached(file: string, args: string[], env: Record<string, string>, cwd: string): number {
  const windows = win32();
  if (windows) return windows.spawnDetached(file, args, env, cwd);
  const child = spawn(file, args, { cwd, env, detached: true, stdio: 'ignore' });
  child.unref();
  return child.pid!;
}
import type { LogFn } from './log';

type Events = {
  event: [HostEvent];
  /** The host died; `restarting`: a new one is being started (false once it died too often). */
  crashed: [code: number, restarting: boolean];
};

/** How the host is started: an executable run as Node with the host's script (Node host), or on its own (Rust). */
export interface HostRuntime {
  exe: string;
  script?: string;
  kind: HostKind;
}

export interface PtyHostOptions {
  /** The profile's host folder: token, descriptors, log. */
  dir: string;
  version: string;
  /** The host new sessions go to; a running host of the other kind keeps its sessions and is left alone. */
  kind?: HostKind;
  /** Prepares the runtime a new host runs from (copied out of the release, for an installed reframe). */
  runtime: () => HostRuntime;
  log?: LogFn;
}

/** Deaths within this time count as a row, and how many of them are restarted. */
const RESTART_WINDOW_MS = 60_000;
const MAX_RESTARTS = 3;
const CONNECT_TIMEOUT_MS = 5_000;
const LAUNCH_TIMEOUT_MS = 20_000;

/** How reframe leaves its host: sessions keep running (the next reframe takes them over), or they end with it. */
export type QuitMode = 'keep' | 'end';

export class PtyHostClient extends EventEmitter<Events> {
  private socket: net.Socket | null = null;
  private hostPid: number | undefined;
  /** Requests made while a host is being started or attached; sent once it is there. */
  private queue: HostRequest[] = [];
  private connecting = false;
  private stopped = false;
  private down = false;
  private restarts: number[] = [];
  private views = new Map<number, MessagePortMain>();
  private nextView = 0;
  /** The hook endpoint of the attached host. */
  hooks = { port: 0, token: '' };
  /** Sessions the host already ran when reframe attached (a reframe before this one started them). */
  adopted: HostSession[] = [];
  /**
   * Hosts of the profile this reframe does not take over: of a protocol it cannot talk, or of the other kind than the
   * configured one. Left alone, they end with their last session.
   */
  foreign: HostDescriptor[] = [];

  constructor(private options: PtyHostOptions) {
    super();
  }

  private log(level: 'info' | 'warn' | 'error', message: string, details?: Record<string, unknown>): void {
    this.options.log?.(level, message, details);
  }

  /** Attaches to the profile's compatible host, or starts one. */
  async start(): Promise<void> {
    this.stopped = false;
    const token = ensureToken(this.options.dir);
    const hosts = readDescriptors(this.options.dir);
    const kind = this.options.kind ?? 'node';
    const ours = (h: HostDescriptor) => compatible(h) && hostKind(h) === kind;
    this.foreign = hosts.filter((h) => !ours(h));
    for (const host of hosts.filter(ours)) {
      const welcome = await this.connect(host.pipe, token).catch((error: unknown) => {
        this.log('warn', 'could not attach to a running pty host', { pid: host.pid, error: String(error) });
        return null;
      });
      if (welcome) {
        this.adopted = welcome.sessions;
        this.log('info', 'attached to the running pty host', {
          pid: welcome.pid,
          version: welcome.version,
          kind: hostKind(welcome),
          sessions: welcome.sessions.length,
        });
        return;
      }
    }
    if (this.foreign.length) {
      this.log('info', 'pty hosts of another reframe keep their sessions', {
        hosts: this.foreign.map((h) => ({ pid: h.pid, version: h.version, protocol: h.protocol, kind: hostKind(h) })),
      });
    }
    await this.launch(token);
  }

  /** Starts a new host and attaches to it. */
  private async launch(token = ensureToken(this.options.dir)): Promise<void> {
    const runtime = this.options.runtime();
    const env: Record<string, string> = {
      ...(process.env as Record<string, string>),
      REFRAME_PTYHOST_DIR: this.options.dir,
      REFRAME_PTYHOST_VERSION: this.options.version,
    };
    if (runtime.script) env.ELECTRON_RUN_AS_NODE = '1';
    else delete env.ELECTRON_RUN_AS_NODE;
    const pid = spawnDetached(runtime.exe, runtime.script ? [runtime.script] : [], env, this.options.dir);
    const descriptorFile = path.join(this.options.dir, 'hosts', `${pid}.json`);
    const deadline = Date.now() + LAUNCH_TIMEOUT_MS;
    while (!fs.existsSync(descriptorFile)) {
      if (!isAlive(pid)) throw new Error('the pty host exited on start (see host.log in its folder)');
      if (Date.now() > deadline) {
        await killTree(pid);
        throw new Error('the pty host did not start in time');
      }
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    const descriptor = JSON.parse(fs.readFileSync(descriptorFile, 'utf8')) as HostDescriptor;
    const welcome = await this.connect(descriptor.pipe, token);
    this.adopted = [];
    this.log('info', 'pty host started', { pid: welcome.pid, exe: runtime.exe, kind: runtime.kind });
  }

  private connect(pipe: string, token: string): Promise<Extract<Welcome, { type: 'welcome' }>> {
    this.connecting = true;
    return new Promise<Extract<Welcome, { type: 'welcome' }>>((resolve, reject) => {
      const socket = net.connect(pipe);
      const decoder = new LineDecoder();
      let welcomed = false;
      const timer = setTimeout(() => {
        socket.destroy();
        reject(new Error('no answer from the pty host'));
      }, CONNECT_TIMEOUT_MS);
      socket.setEncoding('utf8');
      socket.on('connect', () => socket.write(encode({ type: 'hello', token, protocol: PROTOCOL_VERSION })));
      socket.on('error', (error) => {
        if (welcomed) return;
        clearTimeout(timer);
        reject(error);
      });
      socket.on('data', (chunk: string) => {
        for (const message of decoder.push(chunk) as ServerMessage[]) {
          if (welcomed) {
            this.received(message);
            continue;
          }
          clearTimeout(timer);
          if (message.type !== 'welcome') {
            socket.destroy();
            reject(new Error(`the pty host refused: ${message.type === 'refused' ? message.reason : message.type}`));
            return;
          }
          welcomed = true;
          this.socket = socket;
          this.hostPid = message.pid;
          this.hooks = message.hooks;
          this.connecting = false;
          socket.on('close', () => this.closed(socket));
          for (const request of this.queue.splice(0)) socket.write(encode(request));
          resolve(message);
        }
      });
      socket.on('close', () => {
        if (!welcomed) {
          clearTimeout(timer);
          reject(new Error('the pty host closed the connection'));
        }
      });
    }).finally(() => {
      if (!this.socket) this.connecting = false;
    });
  }

  private received(message: ServerMessage): void {
    if (message.type === 'view') {
      this.views.get(message.viewId)?.postMessage(message.message);
      return;
    }
    if (message.type === 'view-closed') {
      this.views.get(message.viewId)?.close();
      this.views.delete(message.viewId);
      return;
    }
    if (message.type === 'replaced' || message.type === 'welcome' || message.type === 'refused') return;
    this.emit('event', message);
  }

  /** The connection is gone: reframe let it go, or the host died. */
  private closed(socket: net.Socket): void {
    if (this.socket !== socket) return;
    this.socket = null;
    for (const port of this.views.values()) port.close();
    this.views.clear();
    if (this.stopped) return;
    const pid = this.hostPid;
    this.hostPid = undefined;
    // A host that is still there but no longer answers is no better than a dead one; its sessions are ended below.
    if (pid && isAlive(pid)) void killTree(pid);
    const now = Date.now();
    this.restarts = this.restarts.filter((at) => now - at < RESTART_WINDOW_MS);
    const restarting = this.restarts.length < MAX_RESTARTS;
    this.down = !restarting;
    // Told first, so the sessions of the dead host are done with before anything starts in the new one.
    this.emit('crashed', 1, restarting);
    if (!restarting) return;
    this.restarts.push(now);
    this.connecting = true;
    this.launch().catch((error: unknown) => {
      this.connecting = false;
      this.down = true;
      this.queue = [];
      this.log('error', 'could not start a new pty host', { error: String(error) });
    });
  }

  /** Leaves the host: with `keep` its sessions go on (and it waits for the next reframe), with `end` it exits. */
  // Resolves once the pipe is closed (or after a second): exiting the process with a pipe still closing crashes it.
  stop(mode: QuitMode = 'end'): Promise<void> {
    this.stopped = true;
    const socket = this.socket;
    this.socket = null;
    for (const port of this.views.values()) port.close();
    this.views.clear();
    if (!socket || socket.destroyed) return Promise.resolve();
    const closed = new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        socket.destroy();
        resolve();
      }, 1000);
      socket.once('close', () => {
        clearTimeout(timer);
        resolve();
      });
    });
    if (mode === 'end') socket.end(encode({ type: 'shutdown' }));
    else socket.end();
    return closed;
  }

  get running(): boolean {
    return !this.down && (this.socket !== null || this.connecting);
  }

  /** The host's process id (the end-to-end tests end it to see what a crash does). */
  get pid(): number | undefined {
    return this.hostPid;
  }

  spawn(request: Omit<Extract<HostRequest, { type: 'spawn' }>, 'type'>): void {
    this.send({ type: 'spawn', ...request });
  }

  write(sessionId: string, data: string): void {
    this.send({ type: 'write', sessionId, data });
  }

  dispose(sessionId: string, instance: number): void {
    this.send({ type: 'dispose', sessionId, instance });
  }

  /** Connects a window to a session's terminal through a fresh MessagePort pair, relayed to the host. */
  attach(sessionId: string, contents: WebContents): void {
    if (!this.running) return;
    const viewId = ++this.nextView;
    const { port1, port2 } = new MessageChannelMain();
    this.views.set(viewId, port1);
    port1.on('message', (event) => {
      this.send({ type: 'view-message', viewId, message: event.data as TerminalViewMessage });
    });
    port1.on('close', () => {
      if (this.views.delete(viewId)) this.send({ type: 'view-close', viewId });
    });
    port1.start();
    this.send({ type: 'view-open', viewId, sessionId });
    contents.postMessage(IPC.terminalPort, { sessionId }, [port2]);
  }

  private send(request: HostRequest): void {
    if (this.socket) this.socket.write(encode(request));
    else if (this.connecting) this.queue.push(request);
  }
}
