import { describe, expect, it } from 'vitest';
import { sessionLabel } from './labels';

describe('sessionLabel', () => {
  it("prefers the typed name, then Claude Code's title, then the folder", () => {
    expect(sessionLabel('Mine', 'AI title', 'C:\\work\\Public')).toBe('Mine');
    expect(sessionLabel(undefined, 'AI title', 'C:\\work\\Public')).toBe('AI title');
    expect(sessionLabel('  ', null, 'C:\\work\\Public')).toBe('Public');
    expect(sessionLabel(undefined, undefined, '/home/me/shop/')).toBe('shop');
  });
});
