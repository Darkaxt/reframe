// Files of the PTY host on disk: the profile's token (which only the user can read), the descriptors running hosts
// write, and the versioned copies of reframe's runtime the hosts run from (so removing a release folder never
// removes the files of a host that still runs).
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
// Electron's own fs treats app.asar as a folder; this one reads and copies it as the file it is (in Node, plain fs).
import rawFs from 'original-fs';
import os from 'node:os';
import path from 'node:path';
import type { HostDescriptor } from '../ptyhost/protocol';

/** The executable's name in a runtime copy: tells a host from a reframe in the process list. */
export const HOST_EXE = process.platform === 'win32' ? 'reframe-ptyhost.exe' : 'reframe-ptyhost';

/** The profile's token, made on first use, readable by the current user only. */
export function ensureToken(dir: string): string {
  const file = path.join(dir, 'token');
  try {
    const existing = fs.readFileSync(file, 'utf8').trim();
    if (/^[a-f0-9]{64}$/.test(existing)) return existing;
  } catch {
    // made below
  }
  fs.mkdirSync(dir, { recursive: true });
  const token = crypto.randomBytes(32).toString('hex');
  const temp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(temp, token, { mode: 0o600 });
  restrictToUser(temp);
  fs.renameSync(temp, file);
  return token;
}

/** Windows: no inherited access, full access for the current user alone. */
export function restrictToUser(file: string): void {
  if (process.platform !== 'win32') return;
  const name = os.userInfo().username;
  const user = process.env.USERDOMAIN ? `${process.env.USERDOMAIN}\\${name}` : name;
  // By its full path: the PATH reframe was started with may not have System32 in it.
  const icacls = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'icacls.exe');
  const result = spawnSync(icacls, [file, '/inheritance:r', '/grant:r', `${user}:F`], {
    windowsHide: true,
    stdio: 'ignore',
  });
  if (result.status !== 0) throw new Error(`could not restrict access to ${file}`);
}

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === 'EPERM';
  }
}

/** Descriptors of the profile's running hosts, newest first; those of hosts that are gone are removed. */
export function readDescriptors(dir: string, isAlive: (pid: number) => boolean = alive): HostDescriptor[] {
  const hosts = path.join(dir, 'hosts');
  let names: string[];
  try {
    names = fs.readdirSync(hosts);
  } catch {
    return [];
  }
  const found: HostDescriptor[] = [];
  for (const name of names) {
    if (!/^\d+\.json$/.test(name)) continue;
    const file = path.join(hosts, name);
    const pid = Number(name.slice(0, -5));
    if (!isAlive(pid)) {
      fs.rmSync(file, { force: true });
      continue;
    }
    try {
      const descriptor = JSON.parse(fs.readFileSync(file, 'utf8')) as HostDescriptor;
      if (descriptor.pid === pid) found.push(descriptor);
    } catch {
      // being written
    }
  }
  return found.sort((a, b) => b.startedAt - a.startedAt);
}

/** The folder name of a runtime copy: the version and the start of a hash of what the host runs. */
export function runtimeName(version: string, hash: string): string {
  return `${version}-${hash.slice(0, 12)}`;
}

/** Hashes what makes a runtime: the app archive's content and the runtime's (Electron's) version. */
export function runtimeHash(files: string[], runtimeVersion: string): string {
  const hash = crypto.createHash('sha256');
  hash.update(runtimeVersion);
  for (const file of files) {
    hash.update(path.basename(file));
    hash.update(rawFs.readFileSync(file));
  }
  return hash.digest('hex');
}

/**
 * Copies what a host needs out of an installed reframe (its executable, the libraries next to it, and the app) to
 * `<root>/<name>`, unless that is there already. Made under a temporary name first, so a copy is whole or absent.
 */
export function ensureRuntimeCopy(installDir: string, root: string, name: string, exeName: string): string {
  const target = path.join(root, name);
  if (fs.existsSync(path.join(target, HOST_EXE))) return target;
  fs.mkdirSync(root, { recursive: true });
  const temp = path.join(root, `${name}.tmp-${process.pid}`);
  fs.rmSync(temp, { recursive: true, force: true });
  fs.mkdirSync(path.join(temp, 'resources'), { recursive: true });
  for (const entry of fs.readdirSync(installDir, { withFileTypes: true })) {
    if (!entry.isFile()) continue;
    if (entry.name !== exeName && !/\.(dll|dat|bin|pak)$/i.test(entry.name)) continue;
    const to = entry.name === exeName ? HOST_EXE : entry.name;
    fs.copyFileSync(path.join(installDir, entry.name), path.join(temp, to));
  }
  for (const part of ['app.asar', 'app.asar.unpacked']) {
    const from = path.join(installDir, 'resources', part);
    if (rawFs.existsSync(from)) rawFs.cpSync(from, path.join(temp, 'resources', part), { recursive: true });
  }
  try {
    fs.renameSync(temp, target);
  } catch (error) {
    // Another reframe made it meanwhile.
    fs.rmSync(temp, { recursive: true, force: true });
    if (!fs.existsSync(path.join(target, HOST_EXE))) throw error;
  }
  return target;
}

/**
 * Removes runtime copies no host runs from any more: all but `keep`, where the executable can be deleted (Windows
 * refuses that while a process runs it). Returns the folders removed.
 */
export function collectRuntimes(root: string, keep: string): string[] {
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(root, { withFileTypes: true });
  } catch {
    return [];
  }
  const removed: string[] = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.name === keep) continue;
    const folder = path.join(root, entry.name);
    // Another reframe's copy that is still being made.
    const making = /\.tmp-(\d+)$/.exec(entry.name);
    if (making && alive(Number(making[1]))) continue;
    try {
      fs.rmSync(path.join(folder, HOST_EXE), { force: true });
    } catch {
      continue; // in use
    }
    try {
      fs.rmSync(folder, { recursive: true, force: true });
      removed.push(folder);
    } catch {
      // a file of it still open; next time
    }
  }
  return removed;
}
