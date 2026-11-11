// Where a new PTY host runs from. A development build runs it in place. An installed reframe on Windows copies its
// runtime to a versioned folder of its own first (REFRAME_PTYHOST_ROOT, set by the managed launcher, or the
// profile's), since the host outlives this reframe and its release folder may be removed (by an update) while the
// host still runs. Copies no host runs from any more are removed.
//
// Two hosts speak the same protocol (docs/ptyhost-protocol.md): the Node host (reframe's executable run as Node with
// ptyhost.js) and the Rust host (resources/reframe-host/reframe-host.exe), chosen with REFRAME_PTYHOST=rust.
import fs from 'node:fs';
import path from 'node:path';
import { app } from 'electron';
import type { HostKind } from '../ptyhost/protocol';
import type { HostRuntime } from './ptyhost-client';
import {
  collectRuntimes,
  ensureRuntimeCopy,
  ensureRustHostCopy,
  HOST_EXE,
  runtimeHash,
  runtimeName,
  RUST_HOST_EXE,
  RUST_HOST_FILES,
} from './ptyhost-files';
import type { LogFn } from './log';

/** The host the profile's new sessions go to: `REFRAME_PTYHOST=rust` opts into the Rust host (Windows only). */
export function configuredHostKind(env: NodeJS.ProcessEnv = process.env, platform = process.platform): HostKind {
  return platform === 'win32' && env.REFRAME_PTYHOST?.trim().toLowerCase() === 'rust' ? 'rust' : 'node';
}

/** The folder the Rust host's files come from: the release's resources, or (development) REFRAME_RUST_HOST_DIR. */
export function rustHostSource(): string {
  if (process.env.REFRAME_RUST_HOST_DIR) return process.env.REFRAME_RUST_HOST_DIR;
  return path.join(
    app.isPackaged ? process.resourcesPath : path.join(app.getAppPath(), 'rust', 'dist'),
    'reframe-host',
  );
}

/** Whether this reframe can start a Rust host (its files are there). */
export function rustHostAvailable(): boolean {
  const source = rustHostSource();
  return RUST_HOST_FILES.every((file) => fs.existsSync(path.join(source, file)));
}

export function hostRuntime(userData: string, log: LogFn, kind: HostKind = 'node'): HostRuntime {
  const script = path.join(__dirname, 'ptyhost.js');
  const packagedWindows = app.isPackaged && process.platform === 'win32';
  const root = process.env.REFRAME_PTYHOST_ROOT || path.join(userData, 'ptyhost', 'runtimes');
  if (kind === 'rust') {
    const source = rustHostSource();
    if (!packagedWindows) return { exe: path.join(source, RUST_HOST_EXE), kind };
    const name = `${app.getVersion()}-rust-${runtimeHash(
      RUST_HOST_FILES.map((file) => path.join(source, file)),
      'rust',
    ).slice(0, 12)}`;
    const copy = ensureRustHostCopy(source, root, name);
    collect(root, name, log);
    return { exe: path.join(copy, RUST_HOST_EXE), kind };
  }
  // Windows is where a release folder is replaced while the host runs from it (and where files in use cannot be
  // removed); elsewhere the host runs in place, as a development build's does.
  if (!packagedWindows) return { exe: process.execPath, script, kind };
  const name = runtimeName(
    app.getVersion(),
    runtimeHash([path.join(process.resourcesPath, 'app.asar')], process.versions.electron),
  );
  const copy = ensureRuntimeCopy(path.dirname(process.execPath), root, name, path.basename(process.execPath));
  collect(root, name, log);
  return {
    exe: path.join(copy, HOST_EXE),
    script: path.join(copy, 'resources', path.relative(process.resourcesPath, script)),
    kind,
  };
}

function collect(root: string, keep: string, log: LogFn): void {
  const removed = collectRuntimes(root, keep);
  if (removed.length) log('info', 'unused pty host runtimes removed', { removed });
}
