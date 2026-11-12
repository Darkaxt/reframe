// The PTY host contract (docs/ptyhost-protocol.md), run against both hosts: each is started as reframe starts it
// (hidden, with a profile folder of its own) and driven over its pipe. The Node host runs from the build
// (out/main/ptyhost.js: `npm run build` first); the Rust host from REFRAME_RUST_HOST_EXE, copied with node-pty's
// ConPTY next to it as an installed reframe copies it. Windows only (ConPTY).
import { spawn as spawnProcess } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { Terminal } from '@xterm/headless';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { TranscriptIndex } from '../main/claude/discovery';
import { TranscriptTitles } from '../main/claude/titles';
import { adoptedClient, CLIENT_ADOPTION_SCHEMA } from '../main/client-adoption';
import { evaluateLaunchConfig } from '../main/launch-config';
import { userHasStatusLine } from '../main/quota';
import {
  encode,
  LineDecoder,
  PROTOCOL_VERSION,
  type ClientMessage,
  type HostDescriptor,
  type HostRequest,
} from './protocol';

const root = path.resolve(__dirname, '..', '..');
const child = path.join(root, 'test', 'ptyhost-conformance-child.mjs');
const conptyDir = path.join(root, 'node_modules', 'node-pty', 'prebuilds', 'win32-x64', 'conpty');
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Message = any;

interface Host {
  kind: 'node' | 'rust';
  dir: string;
  token: string;
  pid: number;
  pipe: string;
}

const alive = (pid: number): boolean => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function until<T>(what: string, check: () => T | undefined | null | false, timeout = 15_000): Promise<T> {
  const end = Date.now() + timeout;
  for (;;) {
    const value = check();
    if (value) return value;
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await pause(50);
  }
}

class Connection {
  readonly messages: Message[] = [];
  closed = false;
  private seen = 0;
  constructor(readonly socket: net.Socket) {
    const decoder = new LineDecoder();
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => this.messages.push(...decoder.push(chunk)));
    socket.on('close', () => (this.closed = true));
    socket.on('error', () => (this.closed = true));
  }

  send(message: ClientMessage | Record<string, unknown>): void {
    this.socket.write(encode(message as ClientMessage));
  }

  request(message: HostRequest): void {
    this.send(message);
  }

  /** The next message (after those already taken) that matches. */
  next(what: string, match: (m: Message) => boolean, timeout?: number): Promise<Message> {
    return until(
      what,
      () => {
        const index = this.messages.findIndex((m, i) => i >= this.seen && match(m));
        if (index < 0) return undefined;
        this.seen = index + 1;
        return this.messages[index];
      },
      timeout,
    );
  }

  viewMessages(viewId: number, type: string): Message[] {
    return this.messages
      .filter((m) => m.type === 'view' && m.viewId === viewId && m.message.type === type)
      .map((m) => m.message);
  }

  /** Everything a view got as live output so far. */
  viewData(viewId: number): string {
    return this.viewMessages(viewId, 'data')
      .map((m) => m.data)
      .join('');
  }

  close(): void {
    this.socket.destroy();
  }
}

const hosts: Host[] = [];
let scratch = '';
let rustExe = '';
let fakeCoordinatorExe = '';

beforeAll(() => {
  scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-conformance-'));
  const built = process.env.REFRAME_RUST_HOST_EXE;
  if (!built || !fs.existsSync(built)) throw new Error('REFRAME_RUST_HOST_EXE must name the built reframe-host.exe');
  if (!fs.existsSync(path.join(root, 'out', 'main', 'ptyhost.js'))) throw new Error('build first: npm run build');
  // Never the installed Coordinator: rust/reframe-services/examples/fake-coordinator.rs, built.
  fakeCoordinatorExe = process.env.REFRAME_FAKE_COORDINATOR_EXE ?? '';
  if (!fakeCoordinatorExe || !fs.existsSync(fakeCoordinatorExe))
    throw new Error('REFRAME_FAKE_COORDINATOR_EXE must name the built fake-coordinator.exe');
  const runtime = path.join(scratch, 'rust-runtime');
  fs.mkdirSync(runtime);
  rustExe = path.join(runtime, 'reframe-host.exe');
  fs.copyFileSync(built, rustExe);
  for (const file of ['conpty.dll', 'OpenConsole.exe']) {
    fs.copyFileSync(path.join(conptyDir, file), path.join(runtime, file));
  }
});

afterAll(async () => {
  for (const host of hosts) if (alive(host.pid)) process.kill(host.pid);
  await until('the hosts to exit', () => hosts.every((h) => !alive(h.pid))).catch(() => undefined);
  fs.rmSync(scratch, { recursive: true, force: true, maxRetries: 5 });
});

async function startHost(kind: 'node' | 'rust'): Promise<Host> {
  const dir = fs.mkdtempSync(path.join(scratch, `${kind}-`));
  const token = crypto.randomBytes(32).toString('hex');
  fs.writeFileSync(path.join(dir, 'token'), token);
  const env = { ...process.env, REFRAME_PTYHOST_DIR: dir, REFRAME_PTYHOST_VERSION: 'conformance' };
  const [exe, args] =
    kind === 'node' ? [process.execPath, [path.join(root, 'out', 'main', 'ptyhost.js')]] : [rustExe, []];
  const proc = spawnProcess(exe, args, { env, stdio: 'ignore', windowsHide: true, detached: true });
  proc.unref();
  const pid = proc.pid!;
  const host: Host = { kind, dir, token, pid, pipe: '' };
  hosts.push(host);
  const file = path.join(dir, 'hosts', `${pid}.json`);
  const descriptor = await until('the host descriptor', () => {
    try {
      return JSON.parse(fs.readFileSync(file, 'utf8')) as HostDescriptor;
    } catch {
      return undefined;
    }
  });
  expect(descriptor).toMatchObject({ pid, protocol: PROTOCOL_VERSION, version: 'conformance', kind, sessionPids: [] });
  host.pipe = descriptor.pipe;
  return host;
}

async function connect(host: Host): Promise<Connection> {
  const socket = net.connect(host.pipe);
  await new Promise<void>((resolve, reject) => {
    socket.once('connect', resolve);
    socket.once('error', reject);
  });
  return new Connection(socket);
}

function spawnChild(connection: Connection, sessionId: string, instance = 1, cols = 80, rows = 24): void {
  connection.request({
    type: 'spawn',
    sessionId,
    instance,
    file: process.execPath,
    args: [child],
    cwd: root,
    env: { ...(process.env as Record<string, string>) },
    cols,
    rows,
    meta: { launched: { claudeSessionId: `claude-${sessionId}` } },
  });
}

function type(connection: Connection, viewId: number, text: string): void {
  connection.request({ type: 'view-message', viewId, message: { type: 'input', data: `${text}\r` } });
}

/** The text of every line of a terminal of `cols`×`rows` that was written `data`, and its cursor column. */
async function render(cols: number, rows: number, data: string): Promise<{ lines: string[]; column: number }> {
  const terminal = new Terminal({ cols, rows, scrollback: 5000, allowProposedApi: true });
  await new Promise<void>((resolve) => terminal.write(data, resolve));
  const buffer = terminal.buffer.active;
  const lines: string[] = [];
  for (let i = 0; i < buffer.length; i++) lines.push(buffer.getLine(i)!.translateToString(true));
  while (lines.length && !lines[lines.length - 1]) lines.pop();
  const column = buffer.cursorX;
  terminal.dispose();
  return { lines, column };
}

function post(port: number, url: string, token: string, body: string): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: '127.0.0.1', port, path: url, method: 'POST', headers: { 'X-Reframe-Token': token } },
      (res) => {
        let text = '';
        res.setEncoding('utf8');
        res.on('data', (c: string) => (text += c));
        res.on('end', () => resolve({ status: res.statusCode ?? 0, body: text }));
      },
    );
    req.on('error', reject);
    req.end(body);
  });
}

// Transcript fixtures: a Claude Code folder of past sessions in three project folders, for the transcripts service.
const user = (content: string, cwd: string) => JSON.stringify({ type: 'user', cwd, message: { content } });

function writeTranscript(home: string, project: string, id: string, lines: string[], modifiedAt: number): void {
  const dir = path.join(home, 'projects', project);
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${id}.jsonl`);
  fs.writeFileSync(file, lines.join('\n'));
  fs.utimesSync(file, modifiedAt / 1000, modifiedAt / 1000);
}

function transcriptHome(kind: string): string {
  const home = fs.mkdtempSync(path.join(scratch, `claude-${kind}-`));
  const at = Date.now() - 60_000;
  const filler = JSON.stringify({ type: 'assistant', message: { content: 'x'.repeat(200) } });
  writeTranscript(
    home,
    'C--work-Alpha',
    'a1',
    [
      user('<command-name>/clear</command-name>', 'C:\\work\\Alpha'),
      user('first alpha prompt,\n  with   spaces', 'C:\\work\\Alpha'),
      JSON.stringify({ type: 'custom-title', customTitle: 'Alpha custom' }),
      JSON.stringify({ type: 'ai-title', aiTitle: 'Alpha AI' }),
      JSON.stringify({ type: 'custom-title', customTitle: ' Alpha renamed ' }),
      '{"type":"ai-title","aiTitle":"cut off whi',
    ],
    at + 1000,
  );
  writeTranscript(
    home,
    'C--work-Alpha',
    'a2',
    [`\uFEFF${user('the BOM line', 'C:\\bom')}`, user('after the BOM', 'C:\\work\\Alpha'), ''],
    at + 2000,
  );
  // Larger than both ends together: the middle is not read.
  const big = [user('the first big prompt', 'D:\\other\\Beta')];
  for (let i = 0; i < 2000; i++) big.push(filler);
  big.push(JSON.stringify({ type: 'ai-title', aiTitle: 'Big in the middle' }));
  for (let i = 0; i < 2000; i++) big.push(filler);
  big.push(JSON.stringify({ type: 'custom-title', customTitle: 'Big custom' }));
  big.push(JSON.stringify({ type: 'ai-title', aiTitle: 'Big at the end' }));
  big.push(JSON.stringify({ type: 'last-prompt', lastPrompt: 'the last big prompt' }));
  writeTranscript(home, 'D--other-Beta', 'b1', big, at + 3000);
  writeTranscript(
    home,
    'D--other-Beta',
    'b2',
    [
      user(
        'Привет ŞEHİR Straße and a title long enough to be cut at ninety characters, which this one is',
        'D:\\other\\Beta',
      ),
    ],
    at + 4000,
  );
  writeTranscript(home, 'D--other-Beta', 'b3', [user('<only a command>', 'D:\\other\\Beta')], at + 4500);
  writeTranscript(home, 'E--x-gamma', 'g1', [user('gamma one', '/home/me/gamma')], at + 5000);
  writeTranscript(
    home,
    'E--x-gamma',
    'g2',
    [user('gamma two', '/home/me/gamma'), JSON.stringify({ type: 'agent-name', agentName: 'Agent G' })],
    at + 6000,
  );
  fs.writeFileSync(path.join(home, 'projects', 'not-a-folder.txt'), '');
  return home;
}

// Fixtures of the quota, clients and launch services: adoption records (with and without a byte order mark, missing,
// corrupt, of another schema, with a changed binary), launch configs and Claude Code folders' settings.
const BOM = String.fromCharCode(0xfeff);
function readFixtures(kind: string) {
  const dir = fs.mkdtempSync(path.join(scratch, `reads-${kind}-`));
  const write = (name: string, content: string) => {
    const file = path.join(dir, name);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, content);
    return file;
  };
  const sha = (content: string) => crypto.createHash('sha256').update(content).digest('hex');
  const chatgpt = write('bin/chatgpt.exe', 'chatgpt client');
  const anthropic = write('bin/anthropic.exe', 'anthropic client');
  const helper = write('bin/helper.py', 'helper');
  const record = JSON.stringify({
    schema: CLIENT_ADOPTION_SCHEMA,
    version: '2.1.330',
    clients: {
      chatgpt: { binary: chatgpt, sha256: sha('chatgpt client').toUpperCase() },
      anthropic: { binary: anthropic, sha256: sha('anthropic client') },
    },
  });
  const plainRecord = write('adoption/plain.json', record);
  const bomRecord = write('adoption/bom.json', BOM + record);
  const corrupt = write('adoption/corrupt.json', '{"schema":"reframe.client-adoption.v1",');
  const other = write('adoption/other.json', JSON.stringify({ schema: 'something.else', clients: {} }));
  const changed = write(
    'adoption/changed.json',
    JSON.stringify({ schema: CLIENT_ADOPTION_SCHEMA, clients: { chatgpt: { binary: chatgpt, sha256: sha('x') } } }),
  );
  const relative = write(
    'adoption/relative.json',
    JSON.stringify({ schema: CLIENT_ADOPTION_SCHEMA, clients: { anthropic: { binary: 'bin\\a.exe', sha256: 'a' } } }),
  );
  const adoptions: [string, 'chatgpt' | 'anthropic'][] = [
    [plainRecord, 'chatgpt'],
    [bomRecord, 'anthropic'],
    [bomRecord, 'nobody' as 'chatgpt'],
    [path.join(dir, 'adoption', 'missing.json'), 'chatgpt'],
    [corrupt, 'chatgpt'],
    [other, 'chatgpt'],
    [changed, 'chatgpt'],
    [relative, 'anthropic'],
  ];
  const base = { userData: path.join(dir, 'profile'), runtime: path.join(dir, 'runtime'), claudeHome: dir };
  const history = { python: path.join(dir, 'python.exe'), helper, helperSha256: sha('helper') };
  const launchConfigs = [
    write(
      'launch/fixed.json',
      JSON.stringify({
        ...base,
        claudeBin: { path: chatgpt, sha256: sha('chatgpt client') },
        anthropicClaudeBin: { path: anthropic, sha256: '0'.repeat(64) },
        history,
      }),
    ),
    write(
      'launch/adopted.json',
      JSON.stringify({ ...base, clientAdoption: bomRecord, history: { ...history, python: 'py' } }),
    ),
    write(
      'launch/broken-adoption.json',
      JSON.stringify({ ...base, clientAdoption: changed, claudeBin: { path: 'rel' } }),
    ),
    write('launch/relative.json', JSON.stringify({ ...base, runtime: 'runtime' })),
    write('launch/bom.json', BOM + JSON.stringify(base)),
  ];
  const home = (name: string, settings?: string) => {
    const folder = path.join(dir, 'homes', name);
    fs.mkdirSync(folder, { recursive: true });
    if (settings !== undefined) fs.writeFileSync(path.join(folder, 'settings.json'), settings);
    return folder;
  };
  const claudeHomes = [
    home('none'),
    home('status-line', JSON.stringify({ statusLine: { type: 'command', command: 'x' } })),
    home('empty-status-line', JSON.stringify({ statusLine: '', theme: 'dark' })),
    home('corrupt', '{ "statusLine": '),
    home('null', 'null'),
  ];
  return { adoptions, launchConfigs, claudeHomes };
}

/** A fake LOCALAPPDATA with the fake Coordinator as its active release, told how to behave. */
function fakeCoordinator(behaviour: Record<string, unknown>): { localAppData: string; release: string } {
  const localAppData = fs.mkdtempSync(path.join(scratch, 'coordinator-'));
  const coordinator = path.join(localAppData, 'Tools', 'llm-coordinator');
  const release = path.join(coordinator, 'releases', 'fake-1.0');
  fs.mkdirSync(release, { recursive: true });
  fs.writeFileSync(path.join(coordinator, 'active.json'), JSON.stringify({ release: 'fake-1.0' }));
  fs.copyFileSync(fakeCoordinatorExe, path.join(release, 'LLMCoordinator.exe'));
  fs.writeFileSync(path.join(release, 'fake.json'), JSON.stringify(behaviour));
  return { localAppData, release };
}

describe.each(['node', 'rust'] as const)('the %s PTY host', (kind) => {
  const open: Connection[] = [];
  afterEach(() => {
    for (const c of open.splice(0)) c.close();
  });
  async function attach(host: Host): Promise<{ connection: Connection; welcome: Message }> {
    const connection = await connect(host);
    open.push(connection);
    connection.send({ type: 'hello', token: host.token, protocol: PROTOCOL_VERSION });
    const welcome = await connection.next('welcome', (m) => m.type === 'welcome' || m.type === 'refused');
    expect(welcome.type).toBe('welcome');
    return { connection, welcome };
  }
  async function stranger(host: Host, hello: Record<string, unknown>): Promise<Message> {
    const connection = await connect(host);
    open.push(connection);
    connection.send(hello);
    const answer = await connection.next('refused', (m) => m.type === 'refused');
    await until('the refused connection to close', () => connection.closed);
    return answer;
  }

  it('welcomes the profile token and refuses others', async () => {
    const host = await startHost(kind);
    expect(await stranger(host, { type: 'hello', token: 'f'.repeat(64), protocol: PROTOCOL_VERSION })).toEqual({
      type: 'refused',
      reason: 'token',
      protocol: PROTOCOL_VERSION,
    });
    expect(await stranger(host, { type: 'hello', token: host.token, protocol: PROTOCOL_VERSION + 1 })).toEqual({
      type: 'refused',
      reason: 'protocol',
      protocol: PROTOCOL_VERSION,
    });
    expect(await stranger(host, { type: 'shutdown' })).toMatchObject({ reason: 'token' });
    expect(alive(host.pid)).toBe(true);

    const { welcome } = await attach(host);
    expect(welcome).toMatchObject({
      type: 'welcome',
      protocol: PROTOCOL_VERSION,
      version: 'conformance',
      pid: host.pid,
      kind,
      sessions: [],
    });
    expect(welcome.hooks.port).toBeGreaterThan(0);
    expect(welcome.hooks.token).toMatch(/^[0-9a-f]{32,}$/);
  });

  it('spawns a session, echoes its input, resizes it and reports its exit', async () => {
    const host = await startHost(kind);
    const { connection } = await attach(host);
    connection.request({ type: 'view-open', viewId: 1, sessionId: 's1' });
    spawnChild(connection, 's1', 1, 90, 30);
    const spawned = await connection.next('spawned', (m) => m.type === 'spawned');
    expect(spawned).toMatchObject({ sessionId: 's1', instance: 1 });
    expect(alive(spawned.pid)).toBe(true);
    await until('the waiting view to get its snapshot', () => connection.viewMessages(1, 'snapshot').length);
    await until('READY', () => connection.viewData(1).includes('READY 90x30'));
    const descriptor = JSON.parse(fs.readFileSync(path.join(host.dir, 'hosts', `${host.pid}.json`), 'utf8'));
    expect(descriptor.sessionPids).toEqual([spawned.pid]);

    type(connection, 1, 'hello there');
    await until('the echo', () => connection.viewData(1).includes('ECHO: hello there'));
    connection.request({ type: 'write', sessionId: 's1', data: 'written\r' });
    await until('the written echo', () => connection.viewData(1).includes('ECHO: written'));

    connection.request({ type: 'view-message', viewId: 1, message: { type: 'resize', cols: 100, rows: 40 } });
    await pause(300);
    type(connection, 1, 'size');
    await until('the new size', () => connection.viewData(1).includes('SIZE 100x40'));

    type(connection, 1, 'exit 7');
    const exit = await connection.next('exit', (m) => m.type === 'exit');
    expect(exit).toEqual({ type: 'exit', sessionId: 's1', instance: 1, pid: spawned.pid, exitCode: 7 });
    expect(await until('the view to get the exit', () => connection.viewMessages(1, 'exit')[0])).toEqual({
      type: 'exit',
      exitCode: 7,
    });
  });

  it('answers spawn-failed for a program that is not there', async () => {
    const host = await startHost(kind);
    const { connection } = await attach(host);
    connection.request({
      type: 'spawn',
      sessionId: 'missing',
      instance: 3,
      file: path.join(root, 'no-such-program.exe'),
      args: [],
      cwd: root,
      env: { ...(process.env as Record<string, string>) },
      cols: 80,
      rows: 24,
    });
    const failed = await connection.next('spawn-failed', (m) => m.type === 'spawn-failed');
    expect(failed).toMatchObject({ sessionId: 'missing', instance: 3 });
    expect(failed.message).toContain('File not found');
    expect(alive(host.pid)).toBe(true);
  });

  it('repaints a view that opens later as the live output painted it', async () => {
    const host = await startHost(kind);
    const { connection } = await attach(host);
    connection.request({ type: 'view-open', viewId: 1, sessionId: 's1' });
    spawnChild(connection, 's1', 1, 60, 12);
    await until('READY', () => connection.viewData(1).includes('READY'));
    type(connection, 1, 'lines 30');
    await until('the lines', () => connection.viewData(1).includes('line 29'));
    type(connection, 1, 'color');
    await until('the colors', () => connection.viewData(1).includes('inverse'));
    type(connection, 1, 'a line long enough to wrap at sixty columns, and then some more of it');
    await until('the long echo', () => connection.viewData(1).includes('some more of it'));
    await pause(500);

    const first = connection.viewMessages(1, 'snapshot')[0];
    const live = await render(60, 12, first.data + connection.viewData(1));
    connection.request({ type: 'view-open', viewId: 2, sessionId: 's1' });
    const snapshot = (await connection.next('the second snapshot', (m) => m.type === 'view' && m.viewId === 2)).message;
    expect(snapshot).toMatchObject({ type: 'snapshot', cols: 60, rows: 12 });
    const repainted = await render(60, 12, snapshot.data);
    expect(repainted.lines.slice(-12)).toEqual(live.lines.slice(-12));
    expect(repainted.lines).toContain('line 0');
    expect(repainted.lines).toContain('line 29');
    expect(repainted.column).toBe(live.column);
    // The colored line keeps its attributes: bold red, then underlined 256-color pink.
    const terminal = new Terminal({ cols: 60, rows: 12, scrollback: 5000, allowProposedApi: true });
    await new Promise<void>((resolve) => terminal.write(snapshot.data, resolve));
    const buffer = terminal.buffer.active;
    const row = [...Array(buffer.length).keys()].find((i) =>
      buffer.getLine(i)!.translateToString(true).startsWith('red under'),
    )!;
    const red = buffer.getLine(row)!.getCell(0)!;
    expect([!!red.isBold(), red.isFgPalette(), red.getFgColor()]).toEqual([true, true, 1]);
    const pink = buffer.getLine(row)!.getCell(4)!;
    expect([!!pink.isUnderline(), pink.getFgColor()]).toEqual([true, 200]);
    terminal.dispose();

    connection.request({ type: 'view-close', viewId: 2 });
    type(connection, 1, 'after');
    await until('the echo after', () => connection.viewData(1).includes('ECHO: after'));
    expect(connection.messages.filter((m) => m.type === 'view' && m.viewId === 2)).toHaveLength(1);
  });

  it('keeps sessions for the next reframe, which replaces the attached one', async () => {
    const host = await startHost(kind);
    const first = await attach(host);
    first.connection.request({ type: 'view-open', viewId: 4, sessionId: 'kept' });
    spawnChild(first.connection, 'kept', 2);
    const spawned = await first.connection.next('spawned', (m) => m.type === 'spawned');
    await until('READY', () => first.connection.viewData(4).includes('READY'));
    first.connection.close();
    await pause(300);

    const second = await attach(host);
    expect(second.welcome.sessions).toEqual([
      {
        sessionId: 'kept',
        instance: 2,
        pid: spawned.pid,
        exitCode: null,
        meta: { launched: { claudeSessionId: 'claude-kept' } },
      },
    ]);
    second.connection.request({ type: 'view-open', viewId: 5, sessionId: 'kept' });
    const snapshot = await second.connection.next('the snapshot', (m) => m.type === 'view' && m.viewId === 5);
    expect(snapshot.message.data).toContain('READY');

    const third = await attach(host);
    expect(await second.connection.next('replaced', (m) => m.type === 'replaced')).toEqual({ type: 'replaced' });
    await until('the replaced connection to close', () => second.connection.closed);
    expect(third.welcome.sessions).toHaveLength(1);

    third.connection.request({ type: 'view-open', viewId: 6, sessionId: 'kept' });
    await third.connection.next('the snapshot', (m) => m.type === 'view' && m.viewId === 6);
    third.connection.request({ type: 'dispose', sessionId: 'kept', instance: 1 });
    await pause(300);
    expect(alive(spawned.pid)).toBe(true);
    third.connection.request({ type: 'dispose', sessionId: 'kept', instance: 2 });
    expect(await third.connection.next('view-closed', (m) => m.type === 'view-closed')).toEqual({
      type: 'view-closed',
      viewId: 6,
    });
    await until('the session process to end', () => !alive(spawned.pid));
  });

  it('ends its sessions and exits on shutdown', async () => {
    const host = await startHost(kind);
    const { connection } = await attach(host);
    spawnChild(connection, 'a');
    spawnChild(connection, 'b');
    const a = await connection.next('spawned a', (m) => m.type === 'spawned');
    const b = await connection.next('spawned b', (m) => m.type === 'spawned');
    connection.request({ type: 'shutdown' });
    await until('the host to exit', () => !alive(host.pid));
    await until('the sessions to end', () => !alive(a.pid) && !alive(b.pid));
    expect(fs.existsSync(path.join(host.dir, 'hosts', `${host.pid}.json`))).toBe(false);
  });

  it('passes hooks to the attached reframe and holds them without one', async () => {
    const host = await startHost(kind);
    const first = await attach(host);
    const { port, token } = first.welcome.hooks;
    spawnChild(first.connection, 'h');
    await first.connection.next('spawned', (m) => m.type === 'spawned');

    expect(await post(port, '/hook/h/Stop', token, '{"x":1}')).toEqual({ status: 200, body: '{}' });
    expect(await first.connection.next('hook', (m) => m.type === 'hook')).toEqual({
      type: 'hook',
      message: { sessionId: 'h', event: 'Stop', payload: { x: 1 } },
    });
    expect((await post(port, '/hook/h/Stop', 'nope', '{}')).status).toBe(403);
    expect((await post(port, '/hook/h/Other', token, '{}')).status).toBe(404);
    expect((await post(port, '/hook/%E0%A4%A/Stop', token, '{}')).status).toBe(400);

    first.connection.close();
    await pause(300);
    expect((await post(port, '/hook/a%20b/UserPromptSubmit', token, '{"prompt":"p"}')).status).toBe(200);
    const second = await attach(host);
    expect(await second.connection.next('the held hook', (m) => m.type === 'hook')).toEqual({
      type: 'hook',
      message: { sessionId: 'a b', event: 'UserPromptSubmit', payload: { prompt: 'p' } },
    });
    second.connection.request({ type: 'shutdown' });
    await until('the host to exit', () => !alive(host.pid));
  });

  it('exits when idle with no reframe attached', async () => {
    const host = await startHost(kind);
    const { connection } = await attach(host);
    connection.close();
    await until('the idle host to exit', () => !alive(host.pid), 25_000);
    expect(fs.existsSync(path.join(host.dir, 'hosts', `${host.pid}.json`))).toBe(false);
  });

  describe('the transcripts service', () => {
    /** A request of the service, and its answer. */
    async function svc(connection: Connection, id: number, method: string, params: Record<string, unknown>) {
      connection.send({ type: 'svc', id, service: 'transcripts', method, params });
      return connection.next(`svc-result ${id}`, (m) => m.type === 'svc-result' && m.id === id);
    }
    /** What reframe's own TypeScript answers, as it comes over the pipe. */
    const plain = (value: unknown) => JSON.parse(JSON.stringify(value ?? null)) as unknown;

    it('lists, finds and titles past sessions as reframe reads them', async () => {
      const home = transcriptHome(kind);
      const host = await startHost(kind);
      const { connection, welcome } = await attach(host);
      expect(welcome.services).toEqual({ transcripts: 1, quota: 1, clients: 1, launch: 1 });
      const index = new TranscriptIndex(home);
      let id = 0;
      const lists = [
        { limit: 50, exclude: [] },
        { limit: 2, exclude: [] },
        { limit: 3, exclude: ['a1'], folder: 'c:\\WORK\\alpha\\' },
        { limit: 50, exclude: [], folder: 'D:\\elsewhere\\Beta' },
        { limit: 50, exclude: [], query: '  ŞEHİR ' },
        { limit: 50, exclude: [], query: 'straße' },
        { limit: 50, exclude: [], query: 'STRASSE' },
        { limit: 50, exclude: [], query: 'привет' },
        { limit: 50, exclude: [], query: 'work\\alpha' },
      ];
      for (const options of lists) {
        const expected = await index.list({ ...options, exclude: new Set(options.exclude) });
        const answer = await svc(connection, ++id, 'list', { home, ...options });
        expect(answer, JSON.stringify(options)).toEqual({ type: 'svc-result', id, ok: true, result: plain(expected) });
      }
      // What the fixtures are there for.
      const all = (await index.list({ limit: 50, exclude: new Set() })).items.map((i) => i.sessionId);
      expect(all).toEqual(['g2', 'g1', 'b2', 'b1', 'a2', 'a1']);
      expect((await index.list({ limit: 2, exclude: new Set() })).hasMore).toBe(true);

      for (const sessionId of ['a1', 'a2', 'b1', 'b3', 'missing']) {
        const info = await svc(connection, ++id, 'info', { home, sessionId });
        expect(info.result, sessionId).toEqual(plain(await index.find(sessionId)));
        const title = await svc(connection, ++id, 'title', { home, sessionId });
        expect(title.result, sessionId).toEqual(await new TranscriptTitles().read(home, sessionId));
      }
      expect((await svc(connection, ++id, 'title', { home, sessionId: 'a1' })).result).toBe('Alpha renamed');
      expect((await svc(connection, ++id, 'title', { home, sessionId: 'b1' })).result).toBe('Big at the end');
      expect((await svc(connection, ++id, 'info', { home, sessionId: 'b1' })).result.title).toBe('Big at the end');
      const times = await svc(connection, id + 1, 'times', { home });
      expect(times.result).toEqual(Object.fromEntries(await index.modifiedTimes()));
    });

    it('refuses what it does not know', async () => {
      const host = await startHost(kind);
      const { connection } = await attach(host);
      const error = async (id: number, message: Record<string, unknown>) =>
        (await svc(connection, id, message.method as string, message.params as Record<string, unknown>)).error?.code;
      expect(await error(1, { method: 'nope', params: { home: scratch } })).toBe('unknown-method');
      expect(await error(2, { method: 'list', params: { home: scratch } })).toBe('bad-params');
      expect(await error(3, { method: 'title', params: { home: scratch } })).toBe('bad-params');
      expect(await error(4, { method: 'info', params: {} })).toBe('bad-params');
      expect(await error(5, { method: 'watch', params: { home: path.join(scratch, 'not-there') } })).toBe('io');
      connection.send({ type: 'svc', id: 6, service: 'overview', method: 'list', params: {} });
      expect(await connection.next('svc-result 6', (m) => m.type === 'svc-result' && m.id === 6)).toMatchObject({
        ok: false,
        error: { code: 'unknown-method' },
      });
      expect(alive(host.pid)).toBe(true);
    });

    it('watches transcripts and tells the project folders that changed', async () => {
      const home = transcriptHome(kind);
      const host = await startHost(kind);
      const { connection } = await attach(host);
      const before = await svc(connection, 1, 'list', { home, limit: 50, exclude: [] });
      expect(before.result.items).toHaveLength(6);
      expect(await svc(connection, 2, 'watch', { home })).toMatchObject({ ok: true });
      await pause(1500);
      const project = 'C--work-Alpha';
      writeTranscript(home, project, 'a9', [user('a new one', 'C:\\work\\Alpha')], Date.now());
      const changed = await connection.next(
        'transcripts.changed',
        (m) => m.type === 'svc-event' && Array.isArray(m.data.projects) && m.data.projects.includes(project),
      );
      expect(changed).toEqual({
        type: 'svc-event',
        service: 'transcripts',
        event: 'changed',
        data: { home, projects: [project] },
      });
      const after = await svc(connection, 3, 'list', { home, limit: 50, exclude: [] });
      expect(after.result.items[0]).toMatchObject({ sessionId: 'a9', title: 'a new one' });
    });
  });

  describe('the quota, clients and launch services', () => {
    async function svc(connection: Connection, id: number, service: string, method: string, params: unknown) {
      connection.send({ type: 'svc', id, service, method, params });
      return connection.next(`svc-result ${id}`, (m) => m.type === 'svc-result' && m.id === id);
    }
    const plain = (value: unknown) => JSON.parse(JSON.stringify(value ?? null)) as unknown;

    it('verifies adopted clients and launch configs as reframe does', async () => {
      const dir = readFixtures(kind);
      const host = await startHost(kind);
      const { connection } = await attach(host);
      let id = 0;
      for (const [record, provider] of dir.adoptions) {
        const answer = await svc(connection, ++id, 'clients', 'adopted', { record, provider });
        expect(answer.result, `${path.basename(record)} ${provider}`).toEqual(plain(adoptedClient(record, provider)));
      }
      for (const file of dir.launchConfigs) {
        const answer = await svc(connection, ++id, 'launch', 'evaluate', { file });
        expect(answer.result, path.basename(file)).toEqual(plain(evaluateLaunchConfig(file)));
      }
      for (const claudeHome of dir.claudeHomes) {
        const answer = await svc(connection, ++id, 'quota', 'statusLine', { claudeHome });
        expect(answer.result, path.basename(claudeHome)).toBe(userHasStatusLine(claudeHome));
      }
      // What the fixtures are there for.
      const kinds = dir.adoptions.map(([record, provider]) => {
        const r = adoptedClient(record, provider);
        return r.ok ? 'ok' : r.reason.replace(/.*(cannot be read|not a client|no adopted|changed).*/, '$1');
      });
      expect(kinds).toEqual([
        'ok',
        'ok',
        'no adopted',
        'cannot be read',
        'cannot be read',
        'not a client',
        'changed',
        'no adopted',
      ]);
      const plans = dir.launchConfigs.map(evaluateLaunchConfig);
      expect(plans.map((p) => p !== null)).toEqual([true, true, true, false, false]);
      expect(Object.keys(plans[0]!.refused)).toEqual(['anthropic']);
      expect(plans[1]!.env.REFRAME_CLIENT_ADOPTION).toBeTruthy();
      expect(plans[2]!.problems.length).toBeGreaterThan(1);
      expect(dir.claudeHomes.map(userHasStatusLine)).toEqual([false, true, false, true, true]);

      // A client that changed after it was verified (the hash is kept by size and modification time).
      const [record] = dir.adoptions[0]!;
      const before = await svc(connection, ++id, 'clients', 'adopted', { record, provider: 'chatgpt' });
      expect(before.result.ok).toBe(true);
      fs.writeFileSync(before.result.path, 'changed client');
      fs.utimesSync(before.result.path, new Date(), new Date(Date.now() + 5000));
      const after = await svc(connection, id + 1, 'clients', 'adopted', { record, provider: 'chatgpt' });
      expect(after.result).toEqual(plain(adoptedClient(record, 'chatgpt')));
      expect(after.result.reason).toMatch(/changed \(sha256 /);
    });

    it('asks a Coordinator for the Codex quota, and kills one that does not answer in time', async () => {
      const host = await startHost(kind);
      const { connection } = await attach(host);
      const none = fs.mkdtempSync(path.join(scratch, 'no-coordinator-'));
      for (const [n, localAppData] of [none, '', null].entries()) {
        const answer = await svc(connection, n + 1, 'quota', 'codex', { localAppData });
        expect(answer.result).toEqual({ coordinator: false, usage: null });
      }
      const usage = { usage: { buckets: [{ id: 'codex', windowMinutes: 300, points: [{ t: 5, remaining: 70 }] }] } };
      const cases: [Record<string, unknown>, unknown][] = [
        [{ stdout: JSON.stringify(usage) }, usage],
        [{ stdout: ` ${JSON.stringify(usage)}\n` }, usage],
        [{ stdout: JSON.stringify(usage), exitCode: 3 }, null],
        [{ stdout: 'not json' }, null],
        [{}, null],
      ];
      let id = 10;
      for (const [behaviour, expected] of cases) {
        const { localAppData, release } = fakeCoordinator(behaviour);
        const answer = await svc(connection, ++id, 'quota', 'codex', { localAppData });
        expect(answer.result, JSON.stringify(behaviour)).toEqual({ coordinator: true, usage: expected });
        const asked = JSON.parse(fs.readFileSync(path.join(release, 'asked.json'), 'utf8'));
        expect(asked).toMatchObject({
          args: ['--native-client-stdin-active'],
          stdin: JSON.stringify({ method: 'usage.snapshot', hours: 24 }),
        });
      }
      const slow = fakeCoordinator({ sleepMs: 30_000, stdout: JSON.stringify(usage) });
      const started = Date.now();
      const answer = await svc(connection, id + 1, 'quota', 'codex', {
        localAppData: slow.localAppData,
        timeoutMs: 700,
      });
      expect(answer.result).toEqual({ coordinator: true, usage: null });
      expect(Date.now() - started).toBeLessThan(10_000);
      const { pid } = JSON.parse(fs.readFileSync(path.join(slow.release, 'asked.json'), 'utf8'));
      await until('the slow Coordinator to be killed', () => !alive(pid), 5000);
    });

    it('refuses what it does not know', async () => {
      const host = await startHost(kind);
      const { connection } = await attach(host);
      const error = async (id: number, service: string, method: string, params: unknown) =>
        (await svc(connection, id, service, method, params)).error?.code;
      expect(await error(1, 'quota', 'nope', {})).toBe('unknown-method');
      expect(await error(2, 'quota', 'statusLine', {})).toBe('bad-params');
      expect(await error(3, 'quota', 'codex', { localAppData: 5 })).toBe('bad-params');
      expect(await error(4, 'clients', 'adopted', { record: 'C:\\r.json' })).toBe('bad-params');
      expect(await error(5, 'launch', 'evaluate', {})).toBe('bad-params');
      expect(alive(host.pid)).toBe(true);
    });
  });
});
