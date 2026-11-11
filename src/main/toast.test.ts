import { EventEmitter } from 'node:events';
import { describe, expect, it } from 'vitest';
import {
  NotificationKeeper,
  appUserModelId,
  managedShortcuts,
  sessionFromActivation,
  sessionToastXml,
  shortcutWrite,
  staleElectronShortcut,
  toastActivatorClsid,
} from './toast';

const exe = 'D:\\Tools\\Reframe\\releases\\0.10.1-claudegpt.14\\reframe.exe';
const managedEnv = {
  REFRAME_APP_ID: 'Darkaxt.Reframe.ClaudeGPT',
  REFRAME_APP_SHORTCUT:
    'C:\\Users\\u\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Reframe - ClaudeGPT.lnk',
  REFRAME_APP_LAUNCHER: 'D:\\Tools\\Reframe\\maintenance\\start-claudegpt.vbs',
  SystemRoot: 'C:\\Windows',
};

describe('app user model id', () => {
  it('uses the fixed managed id, whatever the release folder', () => {
    expect(appUserModelId(managedEnv, exe, false)).toBe('Darkaxt.Reframe.ClaudeGPT');
  });
  it('keeps the installer id and the executable path otherwise', () => {
    expect(appUserModelId({}, exe, true)).toBe('dev.reframe.app');
    expect(appUserModelId({}, exe, false)).toBe(exe);
    expect(appUserModelId({ REFRAME_APP_ID: 'bad id\\x' }, exe, false)).toBe(exe);
  });
});

describe('toast activator clsid', () => {
  it('is a stable braced GUID per app id', () => {
    const id = toastActivatorClsid('Darkaxt.Reframe.ClaudeGPT');
    expect(id).toMatch(/^\{[0-9A-F]{8}-[0-9A-F]{4}-5[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\}$/);
    expect(toastActivatorClsid('Darkaxt.Reframe.ClaudeGPT')).toBe(id);
    expect(toastActivatorClsid('dev.reframe.app')).not.toBe(id);
  });
});

describe('managed shortcuts', () => {
  const [start, root] = managedShortcuts(managedEnv, 'Darkaxt.Reframe.ClaudeGPT', exe);
  it('run the VBS launcher and carry the id and clsid', () => {
    expect(start!.file).toBe(managedEnv.REFRAME_APP_SHORTCUT);
    expect(root!.file).toBe('D:\\Tools\\Reframe\\Reframe.lnk');
    for (const spec of [start!, root!])
      expect(spec.options).toMatchObject({
        target: 'C:\\Windows\\System32\\wscript.exe',
        args: '//B //Nologo "D:\\Tools\\Reframe\\maintenance\\start-claudegpt.vbs"',
        cwd: 'D:\\Tools\\Reframe',
        icon: exe,
        appUserModelId: 'Darkaxt.Reframe.ClaudeGPT',
        toastActivatorClsid: toastActivatorClsid('Darkaxt.Reframe.ClaudeGPT'),
      });
  });
  it('are not made without the launcher variables', () => {
    expect(managedShortcuts({}, exe, exe)).toEqual([]);
    expect(
      managedShortcuts({ ...managedEnv, REFRAME_APP_LAUNCHER: 'start.vbs' }, managedEnv.REFRAME_APP_ID, exe),
    ).toEqual([]);
  });
  it('create a missing shortcut, only add the id to an existing one, and leave a current one alone', () => {
    expect(shortcutWrite(null, start!)).toEqual({ operation: 'create', options: start!.options });
    // The existing Start menu shortcut: same launcher, an older icon, no app id.
    const existing = { target: start!.options.target, args: start!.options.args, icon: 'D:\\old\\reframe.exe' };
    expect(shortcutWrite(existing, start!)).toEqual({
      operation: 'update',
      options: { appUserModelId: 'Darkaxt.Reframe.ClaudeGPT', toastActivatorClsid: start!.options.toastActivatorClsid },
    });
    const clsid = start!.options.toastActivatorClsid.toLowerCase().replace(/[{}]/g, '');
    expect(
      shortcutWrite({ ...existing, appUserModelId: 'Darkaxt.Reframe.ClaudeGPT', toastActivatorClsid: clsid }, start!),
    ).toBeNull();
  });
  it("removes Electron's own shortcut only when it carries another clsid", () => {
    const clsid = start!.options.toastActivatorClsid;
    expect(staleElectronShortcut(null, clsid)).toBe(false);
    expect(staleElectronShortcut({ toastActivatorClsid: '{00000000-0000-0000-0000-000000000000}' }, clsid)).toBe(true);
    expect(staleElectronShortcut({ toastActivatorClsid: clsid.toLowerCase() }, clsid)).toBe(false);
  });
});

describe('activation arguments', () => {
  it('round-trips the session id through the toast launch argument', () => {
    const id = 'a1b2&c<d>"e';
    const toast = sessionToastXml(id, 'Done: <x> & "y"', 'body');
    expect(toast).toContain('<text>Done: &lt;x&gt; &amp; &quot;y&quot;</text>');
    const launch = toast.match(/launch="([^"]*)"/)![1]!.replace(/&amp;/g, '&');
    expect(sessionFromActivation(launch)).toBe(id);
  });
  it('ignores activations that name no session', () => {
    expect(sessionFromActivation('')).toBeNull();
    expect(sessionFromActivation(undefined)).toBeNull();
    expect(sessionFromActivation('other=1')).toBeNull();
    expect(sessionFromActivation('reframe-session=%E0%A4%A')).toBeNull();
  });
});

class FakeNotification extends EventEmitter {
  closed = false;
  close() {
    this.closed = true;
    this.emit('close');
  }
}

describe('notification keeper', () => {
  it('holds a notification until click, close or failure', () => {
    const keeper = new NotificationKeeper<FakeNotification>();
    for (const event of ['click', 'close', 'failed']) {
      const n = new FakeNotification();
      keeper.keep('s', n);
      expect(keeper.size).toBe(1);
      n.emit(event);
      expect(keeper.size).toBe(0);
    }
  });
  it('replaces and closes the older notification of the same session', () => {
    const keeper = new NotificationKeeper<FakeNotification>();
    const first = new FakeNotification();
    const second = new FakeNotification();
    keeper.keep('s', first);
    keeper.keep('t', new FakeNotification());
    keeper.keep('s', second);
    expect(first.closed).toBe(true);
    expect(keeper.size).toBe(2);
    first.emit('click');
    expect(keeper.size).toBe(2);
    second.emit('click');
    expect(keeper.size).toBe(1);
  });
});
