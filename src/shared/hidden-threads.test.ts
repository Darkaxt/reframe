import { describe, expect, it } from 'vitest';
import { hiddenIds, hideThread, restoreThreads, withoutHidden } from './hidden-threads';
import { appStateSchema, defaultState, type HiddenThread } from './state';

const thread = (claudeSessionId: string, hiddenAt = 1): HiddenThread => ({
  claudeSessionId,
  label: `Thread ${claudeSessionId}`,
  cwd: 'C:/work',
  hiddenAt,
});

describe('hidden threads', () => {
  it('hides a thread once, keeping the newest label and time', () => {
    const once = hideThread([], thread('a'));
    expect(hideThread(once, thread('a', 2))).toEqual([thread('a', 2)]);
    expect(hideThread(undefined, thread('b'))).toEqual([thread('b')]);
    expect([...hiddenIds(hideThread(once, thread('b')))]).toEqual(['a', 'b']);
  });

  it('restores some threads or all of them', () => {
    const hidden = [thread('a'), thread('b'), thread('c')];
    expect(restoreThreads(hidden, ['b']).map((h) => h.claudeSessionId)).toEqual(['a', 'c']);
    expect(restoreThreads(hidden, ['unknown'])).toEqual(hidden);
    expect(restoreThreads(hidden, 'all')).toEqual([]);
    expect(restoreThreads(undefined, ['a'])).toEqual([]);
  });

  it('filters items by their Claude session id', () => {
    const items = [{ id: 'a' }, { id: 'b' }];
    expect(withoutHidden(items, hiddenIds([thread('a')]), (i) => i.id)).toEqual([{ id: 'b' }]);
    expect(withoutHidden(items, new Set(), (i) => i.id)).toEqual(items);
  });

  it('is persisted in the state, and older state files get none', () => {
    const { hiddenThreads: _hidden, ...old } = defaultState();
    expect(appStateSchema.parse(old).hiddenThreads).toEqual([]);
    expect(defaultState().hiddenThreads).toEqual([]);
    const saved = { ...defaultState(), hiddenThreads: [{ ...thread('a'), distro: 'Ubuntu' }] };
    expect(appStateSchema.parse(JSON.parse(JSON.stringify(saved))).hiddenThreads).toEqual(saved.hiddenThreads);
    expect(appStateSchema.safeParse({ ...saved, hiddenThreads: [{ claudeSessionId: 'x' }] }).success).toBe(false);
  });
});
