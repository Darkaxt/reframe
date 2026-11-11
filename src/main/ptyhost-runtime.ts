// Where a new PTY host runs from. A development build runs it in place. An installed reframe on Windows copies its
// runtime to a versioned folder of its own first (REFRAME_PTYHOST_ROOT, set by the managed launcher, or the
// profile's), since the host outlives this reframe and its release folder may be removed (by an update) while the
// host still runs. Copies no host runs from any more are removed.
import path from 'node:path';
import { app } from 'electron';
import type { HostRuntime } from './ptyhost-client';
import { collectRuntimes, ensureRuntimeCopy, HOST_EXE, runtimeHash, runtimeName } from './ptyhost-files';
import type { LogFn } from './log';

export function hostRuntime(userData: string, log: LogFn): HostRuntime {
  const script = path.join(__dirname, 'ptyhost.js');
  // Windows is where a release folder is replaced while the host runs from it (and where files in use cannot be
  // removed); elsewhere the host runs in place, as a development build's does.
  if (!app.isPackaged || process.platform !== 'win32') return { exe: process.execPath, script };
  const root = process.env.REFRAME_PTYHOST_ROOT || path.join(userData, 'ptyhost', 'runtimes');
  const name = runtimeName(
    app.getVersion(),
    runtimeHash([path.join(process.resourcesPath, 'app.asar')], process.versions.electron),
  );
  const copy = ensureRuntimeCopy(path.dirname(process.execPath), root, name, path.basename(process.execPath));
  const removed = collectRuntimes(root, name);
  if (removed.length) log('info', 'unused pty host runtimes removed', { removed });
  return {
    exe: path.join(copy, HOST_EXE),
    script: path.join(copy, 'resources', path.relative(process.resourcesPath, script)),
  };
}
