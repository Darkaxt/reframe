import { describe, expect, it } from 'vitest';
import { cycle, nextShown } from './view-mode';

describe('nextShown', () => {
  it('keeps the selected thread while it is there', () => {
    expect(nextShown(['a', 'b'], ['b', 'a'], 'b')).toBe('b');
  });
  it('shows a thread that just came', () => {
    expect(nextShown(['a', 'b'], ['a', 'b', 'c'], 'a')).toBe('c');
  });
  it('shows the next thread when the shown one closes, else the last', () => {
    expect(nextShown(['a', 'b', 'c'], ['a', 'c'], 'b')).toBe('c');
    expect(nextShown(['a', 'b', 'c'], ['a', 'b'], 'c')).toBe('b');
  });
  it('starts with the first thread', () => {
    expect(nextShown([], ['a', 'b'], null)).toBe('a');
    expect(nextShown(['a'], [], 'a')).toBeNull();
  });
});

describe('cycle', () => {
  it('goes round both ways', () => {
    expect(cycle(['a', 'b', 'c'], 'c', 1)).toBe('a');
    expect(cycle(['a', 'b', 'c'], 'a', -1)).toBe('c');
    expect(cycle(['a', 'b'], null, 1)).toBe('a');
    expect(cycle([], 'a', 1)).toBeNull();
  });
});
