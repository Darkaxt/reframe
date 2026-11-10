import { describe, expect, it } from 'vitest';
import { appStateSchema, clampSidebarWidth, SIDEBAR_WIDTH } from './state';

describe('sidebar width', () => {
  it('is clamped and rounded, odd values fall back to the normal width', () => {
    expect(clampSidebarWidth(10)).toBe(SIDEBAR_WIDTH.min);
    expect(clampSidebarWidth(5000)).toBe(SIDEBAR_WIDTH.max);
    expect(clampSidebarWidth(321.6)).toBe(322);
    expect(clampSidebarWidth(Number.NaN)).toBe(SIDEBAR_WIDTH.normal);
  });

  it('an out-of-range saved width does not set the state file aside', () => {
    const parsed = appStateSchema.safeParse({
      version: 1,
      windows: [],
      sessions: {},
      recentFolders: [],
      preferences: { nameFromFolder: true, sidebarWidth: 99999 },
    });
    expect(parsed.success).toBe(true);
  });
});
