import { describe, expect, it, vi } from 'vitest';

vi.mock('electron', () => ({ app: { isPackaged: false } }));
vi.mock('original-fs', async () => await import('node:fs'));
const { configuredHostKind } = await import('./ptyhost-runtime');

describe('configuredHostKind', () => {
  it('is the Node host unless REFRAME_PTYHOST=rust on Windows', () => {
    expect(configuredHostKind({}, 'win32')).toBe('node');
    expect(configuredHostKind({ REFRAME_PTYHOST: 'node' }, 'win32')).toBe('node');
    expect(configuredHostKind({ REFRAME_PTYHOST: 'rust' }, 'win32')).toBe('rust');
    expect(configuredHostKind({ REFRAME_PTYHOST: ' Rust ' }, 'win32')).toBe('rust');
    expect(configuredHostKind({ REFRAME_PTYHOST: 'rust' }, 'linux')).toBe('node');
  });

  it('follows the ptyHost preference on Windows unless REFRAME_PTYHOST overrides it', () => {
    expect(configuredHostKind({}, 'win32', 'rust')).toBe('rust');
    expect(configuredHostKind({}, 'win32', 'node')).toBe('node');
    expect(configuredHostKind({ REFRAME_PTYHOST: 'node' }, 'win32', 'rust')).toBe('node');
    expect(configuredHostKind({ REFRAME_PTYHOST: 'rust' }, 'win32', 'node')).toBe('rust');
    expect(configuredHostKind({ REFRAME_PTYHOST: ' ' }, 'win32', 'rust')).toBe('rust');
    expect(configuredHostKind({}, 'linux', 'rust')).toBe('node');
    expect(configuredHostKind({}, 'darwin', 'rust')).toBe('node');
  });
});
