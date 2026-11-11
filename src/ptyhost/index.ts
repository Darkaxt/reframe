// PTY host. Owns every claude process and a headless xterm mirror per session. Windows are only views: they get a
// serialized snapshot of the screen, then live output. Sessions survive views coming and going, and reframe itself:
// the host is a process of its own (reframe's executable run as Node), reached over a named pipe of the profile, so
// reframe can quit (or be replaced by a newer version) while the sessions go on. It also receives the sessions'
// Claude Code hooks, and hands them to whichever reframe is attached. With no reframe attached and no session
// running it exits.
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import * as pty from 'node-pty';
import { Terminal } from '@xterm/headless';
import { SerializeAddon } from '@xterm/addon-serialize';
import { Unicode11Addon } from '@xterm/addon-unicode11';
import type { TerminalViewMessage } from '@shared/ipc';
import { HookServer, type HookMessage } from '../main/hooks/server';
import {
  checkHello,
  encode,
  LineDecoder,
  pipeName,
  PROTOCOL_VERSION,
  type HostDescriptor,
  type HostEvent,
  type HostRequest,
  type HostSession,
  type ServerMessage,
  type SessionMeta,
} from './protocol';

interface View {
  sessionId: string;
  live: boolean;
  pending: string[];
}

interface Session {
  instance: number;
  proc: pty.IPty;
  mirror: Terminal;
  serializer: SerializeAddon;
  views: Set<number>;
  exitCode: number | null;
  meta?: SessionMeta;
}

/** Without a reframe and a running session for this long, the host goes. */
const IDLE_EXIT_MS = 10_000;
/** Hook events kept while no reframe is attached (the next one gets them). */
const MAX_HELD_HOOKS = 200;

const dir = process.env.REFRAME_PTYHOST_DIR!;
const version = process.env.REFRAME_PTYHOST_VERSION ?? 'unknown';
const token = fs.readFileSync(path.join(dir, 'token'), 'utf8').trim();
const descriptorFile = path.join(dir, 'hosts', `${process.pid}.json`);
const pipe = pipeName(dir, process.pid);

const sessions = new Map<string, Session>();
const views = new Map<number, View>();
let client: net.Socket | null = null;
const held: HookMessage[] = [];
let idleSince = Date.now();

function log(message: string, details: Record<string, unknown> = {}): void {
  try {
    fs.appendFileSync(
      path.join(dir, 'host.log'),
      `${JSON.stringify({ at: new Date().toISOString(), pid: process.pid, message, ...details })}\n`,
    );
  } catch {
    // nowhere to say it
  }
}

process.on('uncaughtException', (error) => {
  log('uncaught exception', { error: error.stack ?? String(error) });
  emit({ type: 'error', message: error.stack ?? String(error) });
});

function send(message: ServerMessage): void {
  if (client && !client.destroyed) client.write(encode(message));
}

function emit(event: HostEvent): void {
  send(event);
}

function writeDescriptor(): void {
  const descriptor: HostDescriptor = {
    pid: process.pid,
    pipe,
    protocol: PROTOCOL_VERSION,
    version,
    startedAt: startedAt,
    sessionPids: [...sessions.values()].filter((s) => s.exitCode === null).map((s) => s.proc.pid),
  };
  const temp = `${descriptorFile}.tmp`;
  fs.writeFileSync(temp, JSON.stringify(descriptor));
  fs.renameSync(temp, descriptorFile);
}
const startedAt = Date.now();

function spawn(request: Extract<HostRequest, { type: 'spawn' }>): void {
  const { sessionId, instance } = request;
  dispose(sessionId, null);
  const mirror = new Terminal({ cols: request.cols, rows: request.rows, scrollback: 5000, allowProposedApi: true });
  const serializer = new SerializeAddon();
  mirror.loadAddon(serializer);
  mirror.loadAddon(new Unicode11Addon());
  mirror.unicode.activeVersion = '11';

  let proc: pty.IPty;
  try {
    proc = pty.spawn(request.file, request.args, {
      name: 'xterm-256color',
      cols: request.cols,
      rows: request.rows,
      cwd: request.cwd,
      env: request.env,
      // Windows: the ConPTY that comes with node-pty (as in Windows Terminal and VS Code). The one built into
      // Windows crashed this process now and then when several sessions ended at once (access violation).
      useConptyDll: true,
    });
  } catch (error) {
    mirror.dispose();
    emit({ type: 'spawn-failed', sessionId, instance, message: String(error) });
    return;
  }

  const session: Session = { instance, proc, mirror, serializer, views: new Set(), exitCode: null, meta: request.meta };
  sessions.set(sessionId, session);
  writeDescriptor();
  emit({ type: 'spawned', sessionId, instance, pid: proc.pid });
  // Views that came before their session's process (one in WSL starts once its distribution answered).
  for (const [viewId, view] of views) if (view.sessionId === sessionId) attach(viewId, view);

  proc.onData((data) => {
    mirror.write(data);
    for (const viewId of session.views) {
      const view = views.get(viewId);
      if (!view) continue;
      if (view.live) send({ type: 'view', viewId, message: { type: 'data', data } });
      else view.pending.push(data);
    }
  });
  proc.onExit(({ exitCode }) => {
    session.exitCode = exitCode;
    for (const viewId of session.views) send({ type: 'view', viewId, message: { type: 'exit', exitCode } });
    emit({ type: 'exit', sessionId, instance, pid: proc.pid, exitCode });
    if (sessions.get(sessionId) === session) writeDescriptor();
  });
}

function openView(viewId: number, sessionId: string): void {
  const view: View = { sessionId, live: false, pending: [] };
  views.set(viewId, view);
  if (sessions.has(sessionId)) attach(viewId, view);
}

function attach(viewId: number, view: View): void {
  const session = sessions.get(view.sessionId)!;
  session.views.add(viewId);
  // The empty write is a marker: its callback runs once everything received so far is parsed, so the
  // snapshot is consistent and everything after it is in view.pending.
  session.mirror.write('', () => {
    if (views.get(viewId) !== view || !session.views.has(viewId)) return;
    send({
      type: 'view',
      viewId,
      message: {
        type: 'snapshot',
        data: session.serializer.serialize(),
        cols: session.mirror.cols,
        rows: session.mirror.rows,
      },
    });
    for (const data of view.pending) send({ type: 'view', viewId, message: { type: 'data', data } });
    view.pending = [];
    view.live = true;
    if (session.exitCode !== null)
      send({ type: 'view', viewId, message: { type: 'exit', exitCode: session.exitCode } });
  });
}

function viewMessage(viewId: number, message: TerminalViewMessage): void {
  const view = views.get(viewId);
  const session = view && sessions.get(view.sessionId);
  if (!session || !session.views.has(viewId) || session.exitCode !== null) return;
  if (message.type === 'input') session.proc.write(message.data);
  if (message.type === 'resize' && message.cols > 1 && message.rows > 1) {
    session.proc.resize(message.cols, message.rows);
    session.mirror.resize(message.cols, message.rows);
  }
}

function closeView(viewId: number): void {
  const view = views.get(viewId);
  if (!view) return;
  views.delete(viewId);
  sessions.get(view.sessionId)?.views.delete(viewId);
}

/** Forgets a session; with an instance, only if it is still that one (a newer start must survive). */
function dispose(sessionId: string, instance: number | null): void {
  const session = sessions.get(sessionId);
  if (!session || (instance !== null && session.instance !== instance)) return;
  sessions.delete(sessionId);
  for (const viewId of session.views) {
    send({ type: 'view-closed', viewId });
    views.delete(viewId);
  }
  if (session.exitCode === null) {
    try {
      session.proc.kill();
    } catch {
      // already gone
    }
  }
  session.mirror.dispose();
  writeDescriptor();
}

function handle(request: HostRequest): void {
  switch (request.type) {
    case 'spawn':
      spawn(request);
      break;
    case 'write': {
      const session = sessions.get(request.sessionId);
      if (session?.exitCode === null) session.proc.write(request.data);
      break;
    }
    case 'dispose':
      dispose(request.sessionId, request.instance);
      break;
    case 'view-open':
      openView(request.viewId, request.sessionId);
      break;
    case 'view-message':
      viewMessage(request.viewId, request.message);
      break;
    case 'view-close':
      closeView(request.viewId);
      break;
    case 'shutdown':
      log('shutdown requested', { sessions: sessions.size });
      for (const id of [...sessions.keys()]) dispose(id, null);
      exit();
      break;
  }
}

function connected(socket: net.Socket): void {
  const decoder = new LineDecoder();
  let welcomed = false;
  socket.setEncoding('utf8');
  socket.on('error', () => {});
  socket.on('data', (chunk: string) => {
    for (const message of decoder.push(chunk)) {
      if (!welcomed) {
        const answer = checkHello(message, token);
        if (answer !== 'ok') {
          log('connection refused', { reason: answer });
          socket.end(encode({ type: 'refused', reason: answer, protocol: PROTOCOL_VERSION }));
          return;
        }
        welcomed = true;
        // One reframe per profile: a new connection is the reframe now (the old one quit or is about to).
        if (client && client !== socket) {
          client.end(encode({ type: 'replaced' }));
          for (const viewId of [...views.keys()]) closeView(viewId);
        }
        client = socket;
        log('reframe attached', { sessions: sessions.size });
        socket.write(
          encode({
            type: 'welcome',
            protocol: PROTOCOL_VERSION,
            version,
            pid: process.pid,
            hooks: { port: hooks.port, token: hooks.token },
            sessions: [...sessions].map(([sessionId, s]): HostSession => ({
              sessionId,
              instance: s.instance,
              pid: s.proc.pid,
              exitCode: s.exitCode,
              meta: s.meta,
            })),
          }),
        );
        for (const message of held.splice(0)) send({ type: 'hook', message });
        continue;
      }
      if (client !== socket) return;
      handle(message as HostRequest);
    }
  });
  socket.on('close', () => {
    if (client !== socket) return;
    client = null;
    // Views belong to the reframe that opened them.
    for (const viewId of [...views.keys()]) closeView(viewId);
    idleSince = Date.now();
    log('reframe detached', { sessions: sessions.size });
  });
}

function exit(): void {
  try {
    fs.rmSync(descriptorFile, { force: true });
  } catch {
    // left for the next reframe to clear
  }
  log('host exits');
  process.exit(0);
}

const hooks = new HookServer();
hooks.on('hook', (message) => {
  if (client) send({ type: 'hook', message });
  else if (message.event !== 'StatusLine') {
    held.push(message);
    if (held.length > MAX_HELD_HOOKS) held.shift();
  }
});

void hooks.start().then(() => {
  fs.mkdirSync(path.dirname(descriptorFile), { recursive: true });
  const server = net.createServer(connected);
  server.on('error', (error) => {
    log('pipe failed', { error: String(error) });
    process.exit(1);
  });
  server.listen(pipe, () => {
    writeDescriptor();
    log('host started', { version, pipe, hookPort: hooks.port });
  });
  setInterval(() => {
    const running = [...sessions.values()].some((s) => s.exitCode === null);
    if (client || running) idleSince = Date.now();
    else if (Date.now() - idleSince > IDLE_EXIT_MS) exit();
  }, 1000);
});
