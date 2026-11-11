import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Electron's fs without its app-archive handling; in Node, plain fs.
vi.mock('original-fs', async () => await import('node:fs'));
import {
  collectRuntimes,
  ensureRuntimeCopy,
  ensureToken,
  HOST_EXE,
  readDescriptors,
  runtimeHash,
  runtimeName,
} from './ptyhost-files';

let dir: string;
beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-ptyhost-'));
});
afterEach(() => fs.rmSync(dir, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 }));

describe('profile token', () => {
  it('is made once and then read back', () => {
    const token = ensureToken(dir);
    expect(token).toMatch(/^[a-f0-9]{64}$/);
    expect(ensureToken(dir)).toBe(token);
    expect(fs.readdirSync(dir)).toEqual(['token']);
  });

  it('replaces a broken token file', () => {
    fs.writeFileSync(path.join(dir, 'token'), 'nope');
    expect(ensureToken(dir)).toMatch(/^[a-f0-9]{64}$/);
  });

  it.runIf(process.platform === 'win32')('is readable by the current user only', async () => {
    ensureToken(dir);
    const { spawnSync } = await import('node:child_process');
    const acl = spawnSync('icacls', [path.join(dir, 'token')], { encoding: 'utf8' }).stdout;
    const entries = acl.split('\n').filter((line) => line.includes(':('));
    expect(entries).toHaveLength(1);
    expect(entries[0]).toContain(os.userInfo().username);
    expect(acl).not.toMatch(/\(I\)/); // nothing inherited
  });
});

describe('host descriptors', () => {
  it('lists running hosts newest first and removes those of hosts that are gone', () => {
    const hosts = path.join(dir, 'hosts');
    fs.mkdirSync(hosts);
    const write = (pid: number, startedAt: number) =>
      fs.writeFileSync(
        path.join(hosts, `${pid}.json`),
        JSON.stringify({ pid, pipe: `p${pid}`, protocol: 1, version: 'v', startedAt, sessionPids: [] }),
      );
    write(1, 100);
    write(2, 300);
    write(3, 200);
    fs.writeFileSync(path.join(hosts, 'notes.txt'), '');
    const found = readDescriptors(dir, (pid) => pid !== 3);
    expect(found.map((d) => d.pid)).toEqual([2, 1]);
    expect(fs.readdirSync(hosts).sort()).toEqual(['1.json', '2.json', 'notes.txt']);
  });

  it('is empty without a hosts folder', () => {
    expect(readDescriptors(dir)).toEqual([]);
  });
});

describe('runtime copies', () => {
  const install = () => {
    const from = path.join(dir, 'release');
    fs.mkdirSync(path.join(from, 'resources', 'app.asar.unpacked', 'node_modules'), { recursive: true });
    fs.mkdirSync(path.join(from, 'data'));
    fs.mkdirSync(path.join(from, 'locales'));
    for (const name of ['reframe.exe', 'ffmpeg.dll', 'icudtl.dat', 'v8_context_snapshot.bin', 'resources.pak']) {
      fs.writeFileSync(path.join(from, name), name);
    }
    fs.writeFileSync(path.join(from, 'reframe-config.json'), '{}');
    fs.writeFileSync(path.join(from, 'resources', 'app.asar'), 'app');
    fs.writeFileSync(path.join(from, 'resources', 'app.asar.unpacked', 'node_modules', 'pty.node'), 'pty');
    fs.writeFileSync(path.join(from, 'data', 'state.json'), '{}');
    return from;
  };

  it('copies the runtime (not the profile or config) under a versioned name, once', () => {
    const from = install();
    const root = path.join(dir, 'ptyhost');
    const name = runtimeName('1.2.3', runtimeHash([path.join(from, 'resources', 'app.asar')], '40.0.0'));
    expect(name).toMatch(/^1\.2\.3-[a-f0-9]{12}$/);
    const copy = ensureRuntimeCopy(from, root, name, 'reframe.exe');
    expect(copy).toBe(path.join(root, name));
    expect(fs.readdirSync(copy).sort()).toEqual(
      [HOST_EXE, 'ffmpeg.dll', 'icudtl.dat', 'resources', 'resources.pak', 'v8_context_snapshot.bin'].sort(),
    );
    expect(fs.readFileSync(path.join(copy, 'resources', 'app.asar.unpacked', 'node_modules', 'pty.node'), 'utf8')).toBe(
      'pty',
    );
    fs.writeFileSync(path.join(copy, 'marker'), '');
    ensureRuntimeCopy(from, root, name, 'reframe.exe');
    expect(fs.existsSync(path.join(copy, 'marker'))).toBe(true);
    expect(fs.readdirSync(root)).toEqual([name]);
  });

  it('names another app archive or runtime version differently', () => {
    const from = install();
    const asar = path.join(from, 'resources', 'app.asar');
    const a = runtimeHash([asar], '40.0.0');
    expect(runtimeHash([asar], '41.0.0')).not.toBe(a);
    fs.writeFileSync(asar, 'app2');
    expect(runtimeHash([asar], '40.0.0')).not.toBe(a);
  });

  it('removes copies other than the current one', () => {
    const root = path.join(dir, 'ptyhost');
    for (const name of ['1.0.0-aaaaaaaaaaaa', '1.0.1-bbbbbbbbbbbb', '1.0.2-cccccccccccc.tmp-999999']) {
      fs.mkdirSync(path.join(root, name), { recursive: true });
      fs.writeFileSync(path.join(root, name, HOST_EXE), '');
    }
    const removed = collectRuntimes(root, '1.0.1-bbbbbbbbbbbb');
    expect(removed.map((r) => path.basename(r)).sort()).toEqual([
      '1.0.0-aaaaaaaaaaaa',
      '1.0.2-cccccccccccc.tmp-999999',
    ]);
    expect(fs.readdirSync(root)).toEqual(['1.0.1-bbbbbbbbbbbb']);
  });

  it.runIf(process.platform === 'win32')('keeps a copy whose executable runs', async () => {
    const root = path.join(dir, 'ptyhost');
    const running = path.join(root, '1.0.0-aaaaaaaaaaaa');
    fs.mkdirSync(running, { recursive: true });
    fs.copyFileSync(process.execPath, path.join(running, HOST_EXE));
    const { spawn } = await import('node:child_process');
    const child = spawn(path.join(running, HOST_EXE), ['-e', 'setTimeout(() => {}, 20000)'], { stdio: 'ignore' });
    await new Promise((resolve) => child.once('spawn', resolve));
    try {
      expect(collectRuntimes(root, 'other')).toEqual([]);
      expect(fs.existsSync(path.join(running, HOST_EXE))).toBe(true);
    } finally {
      child.kill();
      await new Promise((resolve) => child.once('exit', resolve));
    }
    // Once it is gone (Windows lets go of the file a moment after the process), the copy goes too.
    await vi.waitFor(() => expect(collectRuntimes(root, 'other')).toEqual([running]), { timeout: 10_000 });
  });
});
