// Windows routes a toast click to the app whose AppUserModelID it was shown under, through a Start menu shortcut
// carrying that id (and the toast activator CLSID for clicks from Action Center). A portable build's id used to be
// its own executable path, which changes with every release folder; the managed launcher gives a fixed one instead.
import crypto from 'node:crypto';
import path from 'node:path';

/** The AppUserModelID: the managed launcher's fixed REFRAME_APP_ID, else the installer's id or the executable path. */
export function appUserModelId(env: NodeJS.ProcessEnv, execPath: string, installed: boolean): string {
  const fixed = env.REFRAME_APP_ID?.trim();
  if (fixed && /^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$/.test(fixed)) return fixed;
  return installed ? 'dev.reframe.app' : execPath;
}

/** A stable toast activator CLSID for an app id (name-based, so every release of one build shares it). */
export function toastActivatorClsid(appId: string): string {
  const h = crypto.createHash('sha1').update(`reframe-toast:${appId}`).digest('hex');
  const variant = ((parseInt(h[16]!, 16) & 0x3) | 0x8).toString(16);
  const guid = `${h.slice(0, 8)}-${h.slice(8, 12)}-5${h.slice(13, 16)}-${variant}${h.slice(17, 20)}-${h.slice(20, 32)}`;
  return `{${guid.toUpperCase()}}`;
}

export interface ShortcutSpec {
  file: string;
  options: {
    target: string;
    args: string;
    cwd: string;
    description: string;
    icon: string;
    iconIndex: number;
    appUserModelId: string;
    toastActivatorClsid: string;
  };
}

/**
 * The shortcuts that start the managed launcher (REFRAME_APP_LAUNCHER, a .vbs) and carry the app id and activator
 * CLSID: the Start menu one (REFRAME_APP_SHORTCUT), through which Windows routes toast clicks, and Reframe.lnk in the
 * installation folder (the launcher's grandparent). Empty without those variables.
 */
export function managedShortcuts(env: NodeJS.ProcessEnv, appId: string, execPath: string): ShortcutSpec[] {
  const file = env.REFRAME_APP_SHORTCUT;
  const launcher = env.REFRAME_APP_LAUNCHER;
  if (!env.REFRAME_APP_ID || appId !== env.REFRAME_APP_ID.trim()) return [];
  if (!file || !launcher || !path.win32.isAbsolute(file) || !path.win32.isAbsolute(launcher)) return [];
  if (!/\.lnk$/i.test(file) || !/\.vbs$/i.test(launcher)) return [];
  const systemRoot = env.SystemRoot || env.SYSTEMROOT || 'C:\\Windows';
  const installRoot = path.win32.dirname(path.win32.dirname(launcher));
  const spec = (lnk: string): ShortcutSpec => ({
    file: lnk,
    options: {
      target: path.win32.join(systemRoot, 'System32', 'wscript.exe'),
      args: `//B //Nologo "${launcher}"`,
      cwd: installRoot,
      description: path.win32.basename(file, path.win32.extname(file)),
      icon: execPath,
      iconIndex: 0,
      appUserModelId: appId,
      toastActivatorClsid: toastActivatorClsid(appId),
    },
  });
  return [spec(file), spec(path.win32.join(installRoot, 'Reframe.lnk'))];
}

const sameClsid = (a: string | undefined, b: string) =>
  (a ?? '').replace(/[{}]/g, '').toLowerCase() === b.replace(/[{}]/g, '').toLowerCase();

/**
 * What to write for a wanted shortcut: the whole shortcut when there is none (null), only the app id and CLSID when
 * an existing one lacks them (its target, arguments and icon stay as they are), nothing when it already carries both.
 */
export function shortcutWrite(
  existing: Partial<ShortcutSpec['options']> | null,
  wanted: ShortcutSpec,
): { operation: 'create' | 'update'; options: Partial<ShortcutSpec['options']> } | null {
  const w = wanted.options;
  if (!existing) return { operation: 'create', options: w };
  if (existing.appUserModelId === w.appUserModelId && sameClsid(existing.toastActivatorClsid, w.toastActivatorClsid))
    return null;
  return {
    operation: 'update',
    options: { appUserModelId: w.appUserModelId, toastActivatorClsid: w.toastActivatorClsid },
  };
}

/**
 * Electron keeps its own Start menu shortcut (<app name>.lnk, straight to reframe.exe) for toasts and adopts the
 * CLSID it finds there. One left by an older release carries another CLSID, so it goes before Electron looks.
 */
export function staleElectronShortcut(existing: Partial<ShortcutSpec['options']> | null, clsid: string): boolean {
  return !!existing && !sameClsid(existing.toastActivatorClsid, clsid);
}

const SESSION_ARG = 'reframe-session=';

const xml = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');

/**
 * A toast whose activation arguments name the session, so a click from Action Center still finds it. No image: the
 * icon is inside app.asar, which Windows cannot read; the toast shows the shortcut's icon instead.
 */
export function sessionToastXml(sessionId: string, title: string, body: string): string {
  return (
    `<toast launch="${xml(SESSION_ARG + encodeURIComponent(sessionId))}" activationType="foreground">` +
    `<visual><binding template="ToastGeneric"><text>${xml(title)}</text><text>${xml(body)}</text>` +
    `</binding></visual></toast>`
  );
}

/** The session a toast activation names, or null. */
export function sessionFromActivation(args: string | undefined): string | null {
  const match = (args ?? '').match(/(?:^|[\s&;])reframe-session=([^\s&;]+)/);
  if (!match) return null;
  try {
    return decodeURIComponent(match[1]!) || null;
  } catch {
    return null;
  }
}

interface Shown {
  on(event: 'click' | 'close' | 'failed', listener: () => void): unknown;
  close(): void;
}

/**
 * Holds each shown notification until it is clicked, closed or failed, or a newer one for the same key replaces it.
 * Electron's Notification is otherwise garbage-collected after show(), and its 'click' listener goes with it.
 */
export class NotificationKeeper<T extends Shown> {
  private readonly held = new Map<string, T>();

  keep(key: string, notification: T): void {
    const previous = this.held.get(key);
    this.held.set(key, notification);
    if (previous && previous !== notification) previous.close();
    const release = () => {
      if (this.held.get(key) === notification) this.held.delete(key);
    };
    notification.on('click', release);
    notification.on('close', release);
    notification.on('failed', release);
  }

  get size(): number {
    return this.held.size;
  }
}
