import { describe, expect, it } from 'vitest';
import { isArchiveRoot } from './archive-root';

describe('isArchiveRoot', () => {
  it('rejects the Windows system drive however it is spelled', () => {
    for (const root of ['C:\\archives', 'c:/archives', 'C:/', '\\\\?\\C:\\archives'])
      expect(isArchiveRoot(root, 'win32')).toBe(false);
  });
  it('accepts other absolute Windows folders and rejects relative ones', () => {
    expect(isArchiveRoot('D:\\Archives', 'win32')).toBe(true);
    expect(isArchiveRoot('E:/Archives', 'win32')).toBe(true);
    expect(isArchiveRoot('\\\\server\\share\\a', 'win32')).toBe(true);
    expect(isArchiveRoot('archives', 'win32')).toBe(false);
  });
  it('only needs an absolute path elsewhere', () => {
    expect(isArchiveRoot('/home/me/archives', 'linux')).toBe(true);
    expect(isArchiveRoot('archives', 'linux')).toBe(false);
  });
});
