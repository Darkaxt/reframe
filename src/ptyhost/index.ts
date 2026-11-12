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
import { ReadService, SERVICES, StateService, SvcError, TranscriptService } from './services';
import { SessionStatusService } from './sessions';

/** A view of a session's terminal, opened by one connection (the primary or an observer) under its own id. */
interface View {
  owner: net.Socket;
  viewId: number;
  sessionId: string;
  live: boolean;
  pending: string[];
}

interface Session {
  instance: number;
  proc: pty.IPty;
  mirror: Terminal;
  serializer: SerializeAddon;
  /** Keys of `views`. */
  views: Set<string>;
  exitCode: number | null;
  meta?: SessionMeta;
  /** Claude Code's session id as its hooks last gave it (also for a session the `sessions` service does not follow). */
  hookClaudeId?: string;
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
/** Every open view, by `viewKey` (each connection numbers its own views). */
const views = new Map<string, View>();
/** The attached reframe (the primary client): the one whose requests change anything. */
let client: net.Socket | null = null;
/** Read-only clients (`role: "observer"` in the hello): they never replace the primary (docs/ptyhost-protocol.md). */
const observers = new Set<net.Socket>();
const socketIds = new WeakMap<net.Socket, number>();
let nextSocketId = 0;
const viewKey = (owner: net.Socket, viewId: number) => `${socketIds.get(owner)}:${viewId}`;
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

function sendTo(socket: net.Socket, message: ServerMessage): void {
  if (!socket.destroyed) socket.write(encode(message));
}

/** To the primary and every observer. */
function broadcast(message: ServerMessage): void {
  send(message);
  for (const observer of observers) sendTo(observer, message);
}

function sendView(view: View, message: Extract<ServerMessage, { type: 'view' }>['message']): void {
  sendTo(view.owner, { type: 'view', viewId: view.viewId, message });
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
    kind: 'node',
    observer: 1,
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
    broadcast({ type: 'spawn-failed', sessionId, instance, message: String(error) });
    return;
  }

  const session: Session = { instance, proc, mirror, serializer, views: new Set(), exitCode: null, meta: request.meta };
  sessions.set(sessionId, session);
  writeDescriptor();
  statuses.spawned(sessionId, instance, proc.pid, request.meta);
  broadcast({ type: 'spawned', sessionId, instance, pid: proc.pid });
  // Views that came before their session's process (one in WSL starts once its distribution answered).
  for (const [key, view] of views) if (view.sessionId === sessionId) attach(key, view);

  proc.onData((data) => {
    mirror.write(data);
    for (const key of session.views) {
      const view = views.get(key);
      if (!view) continue;
      if (view.live) sendView(view, { type: 'data', data });
      else view.pending.push(data);
    }
  });
  proc.onExit(({ exitCode }) => {
    session.exitCode = exitCode;
    for (const key of session.views) {
      const view = views.get(key);
      if (view) sendView(view, { type: 'exit', exitCode });
    }
    broadcast({ type: 'exit', sessionId, instance, pid: proc.pid, exitCode });
    statuses.exited(sessionId, instance);
    if (sessions.get(sessionId) === session) writeDescriptor();
  });
}

function openView(owner: net.Socket, viewId: number, sessionId: string): void {
  const key = viewKey(owner, viewId);
  closeView(key);
  const view: View = { owner, viewId, sessionId, live: false, pending: [] };
  views.set(key, view);
  if (sessions.has(sessionId)) attach(key, view);
}

function attach(key: string, view: View): void {
  const session = sessions.get(view.sessionId)!;
  session.views.add(key);
  // The empty write is a marker: its callback runs once everything received so far is parsed, so the
  // snapshot is consistent and everything after it is in view.pending.
  session.mirror.write('', () => {
    if (views.get(key) !== view || !session.views.has(key)) return;
    sendView(view, {
      type: 'snapshot',
      data: session.serializer.serialize(),
      cols: session.mirror.cols,
      rows: session.mirror.rows,
    });
    for (const data of view.pending) sendView(view, { type: 'data', data });
    view.pending = [];
    view.live = true;
    if (session.exitCode !== null) sendView(view, { type: 'exit', exitCode: session.exitCode });
  });
}

/** Input and resizes: only the primary's views (an observer's are read-only). */
function viewMessage(viewId: number, message: TerminalViewMessage): void {
  const key = client ? viewKey(client, viewId) : '';
  const view = views.get(key);
  const session = view && sessions.get(view.sessionId);
  if (!session || !session.views.has(key) || session.exitCode !== null) return;
  if (message.type === 'input') session.proc.write(message.data);
  if (message.type === 'resize' && message.cols > 1 && message.rows > 1) {
    session.proc.resize(message.cols, message.rows);
    session.mirror.resize(message.cols, message.rows);
  }
}

function closeView(key: string): void {
  const view = views.get(key);
  if (!view) return;
  views.delete(key);
  sessions.get(view.sessionId)?.views.delete(key);
}

/** Views belong to the connection that opened them. */
function closeViewsOf(owner: net.Socket): void {
  for (const [key, view] of [...views]) if (view.owner === owner) closeView(key);
}

/** Forgets a session; with an instance, only if it is still that one (a newer start must survive). */
function dispose(sessionId: string, instance: number | null): void {
  const session = sessions.get(sessionId);
  if (!session || (instance !== null && session.instance !== instance)) return;
  sessions.delete(sessionId);
  statuses.disposed(sessionId, instance);
  for (const key of session.views) {
    const view = views.get(key);
    if (view) sendTo(view.owner, { type: 'view-closed', viewId: view.viewId });
    views.delete(key);
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

const transcripts = new TranscriptService((event) => send({ type: 'svc-event', ...event }));
const reads = new ReadService();
const state = new StateService();
const statuses = new SessionStatusService(
  (data) => broadcast({ type: 'svc-event', service: 'sessions', event: 'status', data }),
  () => client !== null,
);

/** What an observer may ask: reads only. */
const OBSERVER_METHODS = new Set([
  'sessions.list',
  'observer.primary',
  'transcripts.list',
  'transcripts.info',
  'transcripts.times',
  'transcripts.title',
]);

/** A service request: answered to the connection that asked, while it is still the primary (or that observer). */
function svc(asker: net.Socket, request: Extract<HostRequest, { type: 'svc' }>): void {
  const observer = asker !== client;
  const answer = (message: ServerMessage) => {
    if (observer ? observers.has(asker) : client === asker) sendTo(asker, message);
  };
  const { id } = request;
  const name = `${request.service}.${request.method}`;
  const call =
    observer && !OBSERVER_METHODS.has(name)
      ? Promise.reject(new SvcError('forbidden', `${name} is not for an observer`))
      : request.service === 'observer'
        ? request.method === 'primary'
          ? Promise.resolve({ primary: client !== null })
          : Promise.reject(new SvcError('unknown-method', name))
        : request.service === 'sessions'
          ? request.method === 'list'
            ? Promise.resolve(statuses.list(!observer))
            : Promise.reject(new SvcError('unknown-method', `sessions.${request.method}`))
          : request.service === 'transcripts'
            ? transcripts.call(request.method, request.params ?? {})
            : request.service === 'state'
              ? state.call(request.method, request.params ?? {})
              : Object.hasOwn(SERVICES, request.service)
                ? reads.call(request.service, request.method, request.params ?? {})
                : Promise.reject(new SvcError('unknown-method', `${request.service}.${request.method}`));
  call.then(
    (result) => answer({ type: 'svc-result', id, ok: true, result: result ?? null }),
    (error: unknown) =>
      answer({
        type: 'svc-result',
        id,
        ok: false,
        error:
          error instanceof SvcError
            ? { code: error.code, message: error.message }
            : { code: 'io', message: String(error) },
      }),
  );
}

/** An observer's request: views (read-only) and the services it may ask; anything else is ignored. */
function handleObserver(socket: net.Socket, request: HostRequest): void {
  if (request.type === 'svc') svc(socket, request);
  else if (request.type === 'view-open') openView(socket, request.viewId, request.sessionId);
  else if (request.type === 'view-close') closeView(viewKey(socket, request.viewId));
}

function handle(request: HostRequest): void {
  switch (request.type) {
    case 'svc':
      svc(client!, request);
      break;
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
      openView(client!, request.viewId, request.sessionId);
      break;
    case 'view-message':
      viewMessage(request.viewId, request.message);
      break;
    case 'view-close':
      closeView(viewKey(client!, request.viewId));
      break;
    case 'shutdown':
      log('shutdown requested', { sessions: sessions.size });
      for (const id of [...sessions.keys()]) dispose(id, null);
      exit();
      break;
  }
}

/** The welcome's sessions, with Claude Code's session id where the host knows it. */
function welcomeSessions(): HostSession[] {
  return [...sessions].map(([sessionId, s]): HostSession => {
    const claudeSessionId =
      statuses.claudeSessionId(sessionId) ??
      s.hookClaudeId ??
      s.meta?.launched?.claudeSessionId ??
      s.meta?.wsl?.claudeSessionId;
    return {
      sessionId,
      instance: s.instance,
      pid: s.proc.pid,
      exitCode: s.exitCode,
      meta: s.meta,
      ...(claudeSessionId ? { claudeSessionId } : {}),
    };
  });
}

/** Tells the observers whether a primary is attached (after it changed). */
function primaryChanged(): void {
  for (const observer of observers)
    sendTo(observer, { type: 'svc-event', service: 'observer', event: 'primary', data: { primary: client !== null } });
}

function connected(socket: net.Socket): void {
  const decoder = new LineDecoder();
  let welcomed = false;
  let observer = false;
  socketIds.set(socket, ++nextSocketId);
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
        observer = (message as { role?: unknown }).role === 'observer';
        if (observer) {
          // An observer replaces nobody and is told no hooks: it reads (docs/ptyhost-protocol.md, "Observers").
          observers.add(socket);
          log('observer attached', { sessions: sessions.size });
        } else {
          // One reframe per profile: a new connection is the reframe now (the old one quit or is about to).
          if (client && client !== socket) {
            client.end(encode({ type: 'replaced' }));
            closeViewsOf(client);
          }
          client = socket;
          log('reframe attached', { sessions: sessions.size });
        }
        socket.write(
          encode({
            type: 'welcome',
            protocol: PROTOCOL_VERSION,
            version,
            pid: process.pid,
            hooks: { port: hooks.port, token: hooks.token },
            sessions: welcomeSessions(),
            kind: 'node',
            services: SERVICES,
            role: observer ? 'observer' : 'primary',
            primary: client !== null,
          }),
        );
        if (!observer) {
          for (const message of held.splice(0)) send({ type: 'hook', message });
          primaryChanged();
        }
        continue;
      }
      if (observer) {
        if (observers.has(socket)) handleObserver(socket, message as HostRequest);
        continue;
      }
      if (client !== socket) return;
      handle(message as HostRequest);
    }
  });
  socket.on('close', () => {
    if (observers.delete(socket)) {
      closeViewsOf(socket);
      log('observer detached', { sessions: sessions.size });
      return;
    }
    closeViewsOf(socket);
    if (client !== socket) return;
    client = null;
    idleSince = Date.now();
    log('reframe detached', { sessions: sessions.size });
    primaryChanged();
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
  const claudeId = (message.payload as { session_id?: unknown } | undefined)?.session_id;
  const session = sessions.get(message.sessionId);
  if (session && typeof claudeId === 'string' && claudeId) session.hookClaudeId = claudeId;
  statuses.hook(message);
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
