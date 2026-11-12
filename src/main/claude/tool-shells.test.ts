import { execFileSync, spawn } from 'node:child_process';
import { describe, expect, it } from 'vitest';
import { win32 } from '../win32';
import { backgroundRecheckMs, filetimeMs, hasToolShells, TOOL_SHELL_MIN_AGE_MS } from './tool-shells';

describe('hasToolShells', () => {
  const now = 100_000;
  it('counts lasting tool shells only', () => {
    expect(hasToolShells([{ exe: 'bash.exe', start: now - 6_000 }], 0, now)).toBe(true);
    expect(hasToolShells([{ exe: 'PowerShell.EXE', start: now - 60_000 }], null, now)).toBe(true);
    for (const exe of ['sh.exe', 'pwsh.exe', 'cmd.exe'])
      expect(hasToolShells([{ exe, start: now - TOOL_SHELL_MIN_AGE_MS }], null, now)).toBe(true);
  });

  it('leaves out young shells (status line, hooks), other children and unknown starts', () => {
    expect(hasToolShells([{ exe: 'bash.exe', start: now - 1_000 }], null, now)).toBe(false);
    expect(hasToolShells([{ exe: 'node.exe', start: 0 }], null, now)).toBe(false);
    expect(hasToolShells([{ exe: 'cmd.exe', start: null }], null, now)).toBe(false);
    expect(hasToolShells([], null, now)).toBe(false);
  });

  it('leaves out a child of an earlier process with the same id', () => {
    expect(hasToolShells([{ exe: 'cmd.exe', start: 10 }], 50, now)).toBe(false);
  });

  it('reads FILETIMEs and the recheck interval', () => {
    expect(filetimeMs('116444736000000000')).toBe(0);
    expect(filetimeMs('116444736000010000')).toBe(1);
    expect(backgroundRecheckMs({})).toBe(20_000);
    expect(backgroundRecheckMs({ REFRAME_PTYHOST_BACKGROUND_MS: '500' })).toBe(500);
    expect(backgroundRecheckMs({ REFRAME_PTYHOST_BACKGROUND_MS: 'x' })).toBe(20_000);
  });
});

describe.runIf(process.platform === 'win32')('childProcesses', () => {
  it('lists the direct children of a process with their start', async () => {
    const child = spawn('cmd.exe', ['/d', '/c', 'ping -n 30 127.0.0.1 >nul'], { stdio: 'ignore', windowsHide: true });
    try {
      await new Promise((resolve) => child.once('spawn', resolve));
      const found = win32()!
        .childProcesses(process.pid)
        .find((c) => c.pid === child.pid);
      expect(found).toMatchObject({ exe: 'cmd.exe' });
      expect(Math.abs(filetimeMs(found!.start!) - Date.now())).toBeLessThan(30_000);
    } finally {
      execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
    }
  });
});
