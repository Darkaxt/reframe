// The main side of the PTY host against a stand-in host on a real pipe: attaching (with the profile's token and
// protocol version), taking over its sessions, leaving hosts of other protocol versions alone, and the two ways of
// quitting.
import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Electron's fs without its app-archive handling; in Node, plain fs.
vi.mock('original-fs', async () => await import('node:fs'));
import type { SessionRecord } from '@shared/state';
import {
  encode,
  LineDecoder,
  pipeName,
  PROTOCOL_VERSION,
  checkHello,
  type HostSession,
  type HostDescriptor,
} from '../ptyhost/protocol';
import { ensureToken } from './ptyhost-files';

vi.mock('electron', () => ({ MessageChannelMain: class {} }));
const { PtyHostClient } = await import('./ptyhost-client');
const { SessionManager } = await import('./sessions');

let dir: string;
let servers: net.Server[] = [];
beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-ptyclient-'));
});
afterEach(async () => {
  for (const server of servers) await new Promise((resolve) => server.close(resolve));
  servers = [];
  fs.rmSync(dir, { recursive: true, force: true });
});

/** A host that answers like the real one, and records what it was sent. */
function fakeHost(pid: number, sessions: HostSession[], protocol = PROTOCOL_VERSION) {
  const received: unknown[] = [];
  const pipe = pipeName(dir, pid) + '-test';
  const server = net.createServer((socket) => {
    const decoder = new LineDecoder();
    let welcomed = false;
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      for (const message of decoder.push(chunk)) {
        if (welcomed) {
          received.push(message);
          continue;
        }
        const answer = checkHello(message, fs.readFileSync(path.join(dir, 'token'), 'utf8'));
        if (answer !== 'ok') return socket.end(encode({ type: 'refused', reason: answer, protocol }));
        welcomed = true;
        socket.write(
          encode({ type: 'welcome', protocol, version: 'test', pid, hooks: { port: 1234, token: 'hook' }, sessions }),
        );
      }
    });
  });
  servers.push(server);
  const descriptor: HostDescriptor = { pid, pipe, protocol, version: 'test', startedAt: pid, sessionPids: [] };
  fs.mkdirSync(path.join(dir, 'hosts'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'hosts', `${pid}.json`), JSON.stringify(descriptor));
  const listening = new Promise<void>((resolve) => server.listen(pipe, resolve));
  return { received, listening, descriptor };
}

const noRuntime = () => {
  throw new Error('no new host in this test');
};

describe('PtyHostClient', () => {
  it('attaches to the running host of the profile and takes its sessions and hook endpoint', async () => {
    ensureToken(dir);
    const sessions: HostSession[] = [{ sessionId: 's1', instance: 7, pid: 4242, exitCode: null }];
    const host = fakeHost(process.pid, sessions);
    await host.listening;
    const client = new PtyHostClient({ dir, version: 'test', runtime: noRuntime });
    await client.start();
    expect(client.adopted).toEqual(sessions);
    expect(client.hooks).toEqual({ port: 1234, token: 'hook' });
    expect(client.pid).toBe(process.pid);
    expect(client.running).toBe(true);
    await client.stop('keep');
  });

  it('leaves a host of another protocol version alone, and does not attach with a wrong token', async () => {
    ensureToken(dir);
    const old = fakeHost(process.ppid, [], PROTOCOL_VERSION - 1);
    await old.listening;
    const client = new PtyHostClient({ dir, version: 'test', runtime: noRuntime });
    await expect(client.start()).rejects.toThrow('no new host');
    expect(client.foreign.map((h) => h.pid)).toEqual([process.ppid]);
    expect(old.received).toEqual([]);
  });

  it('is refused by a host of the profile with another token', async () => {
    ensureToken(dir);
    const host = fakeHost(process.pid, []);
    await host.listening;
    // The host compares with the token file; this client was given another one.
    const real = fs.readFileSync(path.join(dir, 'token'), 'utf8');
    const other = path.join(dir, 'other');
    fs.mkdirSync(path.join(other, 'hosts'), { recursive: true });
    fs.copyFileSync(path.join(dir, 'hosts', `${process.pid}.json`), path.join(other, 'hosts', `${process.pid}.json`));
    fs.writeFileSync(
      path.join(other, 'token'),
      real.replace(/./, (c) => (c === 'a' ? 'b' : 'a')),
    );
    const stranger = new PtyHostClient({ dir: other, version: 'test', runtime: noRuntime });
    await expect(stranger.start()).rejects.toThrow('no new host');
    expect(host.received).toEqual([]);
  });

  it('quits keeping the sessions (the host stays), or ending them (the host is told to shut down)', async () => {
    ensureToken(dir);
    for (const [mode, expected] of [
      ['keep', []],
      ['end', [{ type: 'shutdown' }]],
    ] as const) {
      const host = fakeHost(process.pid, []);
      await host.listening;
      const client = new PtyHostClient({ dir, version: 'test', runtime: noRuntime });
      await client.start();
      client.write('s1', 'x');
      await client.stop(mode);
      await vi.waitFor(() => expect(host.received.length).toBeGreaterThanOrEqual(1 + expected.length));
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(host.received).toEqual([{ type: 'write', sessionId: 's1', data: 'x' }, ...expected]);
      for (const server of servers.splice(0)) await new Promise((resolve) => server.close(resolve));
    }
  });
});

describe('SessionManager.adopt', () => {
  const record = (id: string, lifecycle: SessionRecord['lifecycle'] = 'open') =>
    ({ id, lifecycle, claudeSessionId: `c-${id}`, cwd: dir, launch: {}, lastActiveAt: 0 }) as unknown as SessionRecord;

  it('takes over the open sessions it shows, and ends the others', async () => {
    const host = Object.assign(new EventEmitter(), {
      running: true,
      hooks: { port: 1, token: 't' },
      adopted: [
        { sessionId: 'shown', instance: 5, pid: 101, exitCode: null },
        { sessionId: 'hidden', instance: 6, pid: 102, exitCode: null },
        { sessionId: 'unknown', instance: 9, pid: 103, exitCode: null },
        { sessionId: 'exited', instance: 2, pid: 104, exitCode: 0 },
      ] as HostSession[],
      // Ctrl+C ends a fake session.
      write: vi.fn((sessionId: string, data: string) => {
        const hosted = host.adopted.find((s) => s.sessionId === sessionId);
        if (data === '' && hosted) {
          const exit = { type: 'exit', sessionId, instance: hosted.instance, pid: hosted.pid, exitCode: 0 };
          setImmediate(() => host.emit('event', exit)); // over the pipe, later
        }
      }),
      dispose: vi.fn(),
      spawn: vi.fn(),
    });
    const manager = new SessionManager({
      host: host as never,
      claudeHome: dir,
      settingsDir: dir,
      resolveClaude: () => null,
      env: {},
    });
    const ended: string[] = [];
    manager.on('ended', (id) => ended.push(id));
    const records = { shown: record('shown'), hidden: record('hidden'), exited: record('exited') };
    const taken = manager.adopt(records, (id) => id !== 'hidden');
    expect([...taken]).toEqual(['shown']);
    expect(manager.keepsRunning('shown')).toBe(true);
    expect(manager.status('shown')).toMatchObject({ pid: 101, instance: 5 });
    expect(ended).toEqual(['exited']);
    // The others are ended (Ctrl+C, then disposed), and a new start numbers after the host's instances.
    await vi.waitFor(() =>
      expect(host.dispose.mock.calls.map((c) => c[0]).sort()).toEqual(['exited', 'hidden', 'unknown']),
    );
    expect(host.write).toHaveBeenCalledWith('hidden', '\x03');
    // A host event for the adopted instance reaches it.
    host.emit('event', { type: 'exit', sessionId: 'shown', instance: 5, pid: 101, exitCode: 0 });
    await new Promise((resolve) => setImmediate(resolve));
    expect(manager.isRunning('shown')).toBe(false);
    expect(ended).toEqual(['exited', 'shown']);
    manager.dispose();
  });
});
