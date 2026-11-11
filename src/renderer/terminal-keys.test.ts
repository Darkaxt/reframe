import { describe, expect, it } from 'vitest';
import { NEWLINE, copyPreview, dragWithoutShift, enterKey, type EnterState } from './terminal-keys';

const key = (type: string, mods: Partial<Record<'shiftKey' | 'ctrlKey' | 'altKey' | 'metaKey', boolean>> = {}) => ({
  type,
  key: 'Enter',
  shiftKey: false,
  ctrlKey: false,
  altKey: false,
  metaKey: false,
  ...mods,
});

describe('Enter in a terminal', () => {
  it('Shift+Enter types ESC CR and its keypress does not send a second CR', () => {
    const state: EnterState = { swallow: false };
    expect(NEWLINE).toBe('\x1b\r');
    expect(enterKey(key('keydown', { shiftKey: true }), false, state)).toBe('newline');
    expect(enterKey(key('keypress', { shiftKey: true }), false, state)).toBe('swallow');
    expect(enterKey(key('keyup', { shiftKey: true }), false, state)).toBeNull();
  });

  it('Enter or Shift+Enter with a selection copies and sends nothing', () => {
    for (const shiftKey of [false, true]) {
      const state: EnterState = { swallow: false };
      expect(enterKey(key('keydown', { shiftKey }), true, state)).toBe('copy');
      // The copy cleared the selection; the keypress is still dropped.
      expect(enterKey(key('keypress', { shiftKey }), false, state)).toBe('swallow');
    }
  });

  it('plain Enter and modified Enter are left to xterm', () => {
    const state: EnterState = { swallow: false };
    expect(enterKey(key('keydown'), false, state)).toBeNull();
    expect(enterKey(key('keypress'), false, state)).toBeNull();
    for (const mod of ['ctrlKey', 'altKey', 'metaKey'] as const)
      expect(enterKey(key('keydown', { [mod]: true, shiftKey: true }), true, state)).toBeNull();
    expect(enterKey({ ...key('keydown'), key: 'a' }, true, state)).toBeNull();
  });

  it('a keyup ends the swallowing (no keypress came)', () => {
    const state: EnterState = { swallow: false };
    enterKey(key('keydown', { shiftKey: true }), false, state);
    enterKey(key('keyup', { shiftKey: true }), false, state);
    expect(enterKey(key('keypress'), false, state)).toBeNull();
  });
});

describe('copy notice', () => {
  it('shows the copied text on one line, cut at 40 characters', () => {
    expect(copyPreview('  hello\n  world ')).toBe('hello world');
    expect(copyPreview('x'.repeat(50))).toBe(`${'x'.repeat(40)}…`);
  });
});

describe('select hint', () => {
  const move = (clientX: number, buttons = 1, shiftKey = false) => ({ clientX, clientY: 0, buttons, shiftKey });
  it('is for a left-button drag without Shift in a terminal that reads the mouse', () => {
    expect(dragWithoutShift({ x: 0, y: 0 }, move(10), true)).toBe(true);
    expect(dragWithoutShift({ x: 0, y: 0 }, move(10), false)).toBe(false);
    expect(dragWithoutShift({ x: 0, y: 0 }, move(10, 1, true), true)).toBe(false);
    expect(dragWithoutShift({ x: 0, y: 0 }, move(10, 0), true)).toBe(false);
    expect(dragWithoutShift({ x: 0, y: 0 }, move(2), true)).toBe(false);
    expect(dragWithoutShift(null, move(10), true)).toBe(false);
  });
});
