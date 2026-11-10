import { spawn, execFile } from 'node:child_process';
import { createRequire } from 'node:module';
import { promisify } from 'node:util';
import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { setup, root, cleanup } from './helpers';

const execute = promisify(execFile);
function start(env: Record<string, string>, args: string[] = []) {
  const binary = process.env.REFRAME_E2E_APP || (createRequire(__filename)('electron') as string);
  const launchEnv = { ...env };
  delete launchEnv.ELECTRON_RUN_AS_NODE;
  const child = spawn(binary, process.env.REFRAME_E2E_APP ? args : [root, ...args], {
    cwd: root,
    env: launchEnv,
    windowsHide: true,
    stdio: 'ignore',
  });
  const exited = new Promise<number | null>((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', resolve);
  });
  return { child, exited, binary };
}
function saved(profile: string) {
  fs.mkdirSync(profile, { recursive: true });
  return new Promise<void>((resolve) => {
    const read = () => {
      try {
        if (JSON.parse(fs.readFileSync(path.join(profile, 'state.json'), 'utf8')).windows?.length) {
          watcher.close();
          resolve();
        }
      } catch {
        /* Atomic save has not completed. */
      }
    };
    const watcher = fs.watch(profile, read);
    read();
  });
}

test('quit exits without an instance, respects profile identity and normally closes the owning instance', async () => {
  test.setTimeout(0);
  const fixture = setup();
  let primary: ReturnType<typeof start> | undefined;
  try {
    expect(await start(fixture.env, ['--quit']).exited).toBe(0);
    expect(fs.existsSync(path.join(fixture.env.REFRAME_USER_DATA!, 'state.json'))).toBe(false);
    const ready = saved(fixture.env.REFRAME_USER_DATA!);
    primary = start(fixture.env);
    await Promise.race([
      ready,
      primary.exited.then(() => {
        throw Error('Primary exited before its durable startup state');
      }),
    ]);
    const foreign = { ...fixture.env, REFRAME_USER_DATA: path.join(fixture.dir, 'other-profile') };
    expect(await start(foreign, ['--quit']).exited).toBe(0);
    expect(primary.child.exitCode).toBeNull();
    const stopping = start(fixture.env, ['--quit']);
    expect(await stopping.exited).toBe(0);
    expect(await primary.exited).toBe(0);
    const state = JSON.parse(fs.readFileSync(path.join(fixture.env.REFRAME_USER_DATA!, 'state.json'), 'utf8'));
    expect(state.windows.length).toBeGreaterThan(0);
  } finally {
    if (primary && primary.child.exitCode === null) {
      await start(fixture.env, ['--quit']).exited;
      await primary.exited;
    }
    await cleanup(fixture.dir);
  }
});

test('installed maintenance stop helper verifies the bound native instance and observes owned exits', async () => {
  test.setTimeout(0);
  test.skip(
    process.platform !== 'win32' || !process.env.REFRAME_E2E_APP,
    'Native packaged Windows helper qualification',
  );
  const fixture = setup();
  let primary: ReturnType<typeof start> | undefined;
  try {
    const profile = fixture.env.REFRAME_USER_DATA!;
    const ready = saved(profile);
    primary = start(fixture.env);
    await Promise.race([
      ready,
      primary.exited.then(() => {
        throw Error('Native fixture exited before startup');
      }),
    ]);
    const active = path.join(fixture.dir, 'active.json');
    await execute(
      'pwsh.exe',
      [
        '-NoLogo',
        '-NoProfile',
        '-File',
        path.join(root, 'maintenance', 'windows', 'write-stop-fixture.ps1'),
        '-PrimaryPid',
        String(primary.child.pid),
        '-Executable',
        primary.binary,
        '-Profile',
        profile,
        '-Runtime',
        path.join(fixture.dir, 'stop-runtime'),
        '-ActivePath',
        active,
      ],
      { windowsHide: true },
    );
    await execute(
      'pwsh.exe',
      [
        '-NoLogo',
        '-NoProfile',
        '-File',
        path.join(path.dirname(primary.binary), 'resources', 'maintenance', 'windows', 'stop-managed.ps1'),
        '-ActivePath',
        active,
      ],
      { windowsHide: true },
    );
    expect(await primary.exited).toBe(0);
    const receipt = JSON.parse(fs.readFileSync(path.join(fixture.dir, 'stop-runtime', 'last-stop.json'), 'utf8'));
    expect(receipt).toMatchObject({ status: 'owned_processes_exited', profile });
    expect(receipt.identities.map((entry: { pid: number }) => entry.pid)).toContain(primary.child.pid);
  } finally {
    if (primary && primary.child.exitCode === null) {
      await start(fixture.env, ['--quit']).exited;
      await primary.exited;
    }
    await cleanup(fixture.dir);
  }
});
