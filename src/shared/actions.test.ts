import { describe, expect, it } from 'vitest';
import {
  addTile,
  applyLayoutAction,
  dockSessions,
  dropSession,
  isCollapsed,
  isDropTarget,
  isLayoutAction,
  isWindowEmpty,
  putNode,
  settleGrid,
  viewModeOf,
  takeNode,
  windowGroups,
  windowHasSession,
  windowSessions,
  windowsToRestore,
} from './actions';
import { addToGroup, groupOf, groupTile, leaves, tileKey } from './layout';
import { emptyWindow, windowStateSchema } from './state';

const withTiles = (...ids: string[]) => ids.reduce((w, id) => addTile(w, id), emptyWindow('w1'));

describe('collapse and expand', () => {
  it('moves a tile into the dock and back to its place', () => {
    const win = withTiles('a', 'b', 'c');
    const collapsed = applyLayoutAction(win, { type: 'tile/collapse', sessionId: 'b' });
    expect(leaves(collapsed.layout)).toEqual(['a', 'c']);
    expect(collapsed.dock.flatMap(dockSessions)).toEqual(['b']);
    expect(windowHasSession(collapsed, 'b')).toBe(true);

    const expanded = applyLayoutAction(collapsed, { type: 'tile/expand', sessionId: 'b' });
    expect(leaves(expanded.layout)).toEqual(['a', 'b', 'c']);
    expect(expanded.dock).toEqual([]);
  });

  it('ignores unknown sessions', () => {
    const win = withTiles('a');
    expect(applyLayoutAction(win, { type: 'tile/collapse', sessionId: 'x' })).toBe(win);
    expect(applyLayoutAction(win, { type: 'tile/expand', sessionId: 'x' })).toBe(win);
  });
});

describe('dropSession', () => {
  it('removes a session from layout and dock', () => {
    let win = withTiles('a', 'b');
    win = applyLayoutAction(win, { type: 'tile/collapse', sessionId: 'b' });
    win = dropSession(dropSession(win, 'b'), 'a');
    expect(win.layout).toBeNull();
    expect(win.dock).toEqual([]);
  });
});

describe('isWindowEmpty', () => {
  it('counts collapsed tiles as content', () => {
    expect(isWindowEmpty(emptyWindow('w'))).toBe(true);
    const collapsed = applyLayoutAction(withTiles('a'), { type: 'tile/collapse', sessionId: 'a' });
    expect(collapsed.layout).toBeNull();
    expect(isWindowEmpty(collapsed)).toBe(false);
    expect(isWindowEmpty(dropSession(collapsed, 'a'))).toBe(true);
  });
});

describe('windowsToRestore', () => {
  it('leaves empty windows out, and keeps the first one only when all are empty', () => {
    const tiles = addTile(emptyWindow('tiles'), 'a');
    const collapsed = applyLayoutAction(addTile(emptyWindow('collapsed'), 'b'), {
      type: 'tile/collapse',
      sessionId: 'b',
    });
    expect(windowsToRestore([emptyWindow('x'), tiles, emptyWindow('y'), collapsed])).toEqual([tiles, collapsed]);
    expect(windowsToRestore([emptyWindow('x'), emptyWindow('y')])).toEqual([emptyWindow('x')]);
  });
});

describe('isDropTarget', () => {
  it('accepts a node key with a side', () => {
    expect(isDropTarget({ key: tileKey('a'), side: 'bottom' })).toBe(true);
    expect(isDropTarget({ key: 1, side: 'bottom' })).toBe(false);
    expect(isDropTarget(null)).toBe(false);
  });
});

describe('isLayoutAction', () => {
  it('accepts well-formed actions only', () => {
    expect(isLayoutAction({ type: 'node/move', key: tileKey('a'), target: { key: tileKey('b'), side: 'left' } })).toBe(
      true,
    );
    expect(isLayoutAction({ type: 'node/move', key: 'a', target: { key: 'x', side: 'middle' } })).toBe(false);
    expect(isLayoutAction({ type: 'group/rename', groupId: 'g', name: 'x' })).toBe(true);
    expect(isLayoutAction({ type: 'group/collapse', groupId: 7 })).toBe(false);
    expect(isLayoutAction({ type: 'split/resize', splitId: 's', sizes: [50, '50'] })).toBe(false);
    expect(isLayoutAction({ type: 'tile/delete', sessionId: 'a' })).toBe(false);
    expect(isLayoutAction(null)).toBe(false);
  });
});

describe('groups in a window', () => {
  const grouped = () => {
    let win = withTiles('a', 'b', 'c');
    win = { ...win, layout: groupTile(win.layout, 'a', { id: 'g', name: 'G', color: '#f00' }) };
    return { ...win, layout: addToGroup(win.layout, 'b', 'g') };
  };

  it('collapses a whole group into one dock entry and restores it', () => {
    const collapsed = applyLayoutAction(grouped(), { type: 'group/collapse', groupId: 'g' });
    expect(leaves(collapsed.layout)).toEqual(['c']);
    expect(collapsed.dock).toHaveLength(1);
    expect(windowSessions(collapsed).sort()).toEqual(['a', 'b', 'c']);
    expect(isCollapsed(collapsed, 'a')).toBe(true);
    expect(windowGroups(collapsed).map((g) => g.id)).toEqual(['g']);

    const expanded = applyLayoutAction(collapsed, { type: 'group/expand', groupId: 'g' });
    expect(leaves(expanded.layout)).toEqual(['a', 'b', 'c']);
    expect(groupOf(expanded.layout, tileKey('b'))?.id).toBe('g');
    expect(expanded.dock).toEqual([]);
  });

  it('collapsing the last tile of a group takes the group along', () => {
    let win = withTiles('a', 'b');
    win = { ...win, layout: groupTile(win.layout, 'a', { id: 'g', name: 'G', color: '#f00' }) };
    const collapsed = applyLayoutAction(win, { type: 'tile/collapse', sessionId: 'a' });
    expect(collapsed.dock).toMatchObject([{ kind: 'group', node: { id: 'g' } }]);
  });

  it('renames a group, visible or collapsed', () => {
    const renamed = applyLayoutAction(grouped(), { type: 'group/rename', groupId: 'g', name: '  API  ' });
    expect(windowGroups(renamed)[0]!.name).toBe('API');
    const collapsed = applyLayoutAction(renamed, { type: 'group/collapse', groupId: 'g' });
    const again = applyLayoutAction(collapsed, { type: 'group/rename', groupId: 'g', name: 'Backend' });
    expect(windowGroups(again)[0]!.name).toBe('Backend');
    expect(applyLayoutAction(again, { type: 'group/rename', groupId: 'g', name: '   ' })).toBe(again);
    const long = applyLayoutAction(again, { type: 'group/rename', groupId: 'g', name: 'x'.repeat(500) });
    expect(windowGroups(long)[0]!.name).toHaveLength(80);
  });

  it('drops a session out of a collapsed group, and the group once it is empty', () => {
    const collapsed = applyLayoutAction(grouped(), { type: 'group/collapse', groupId: 'g' });
    const one = dropSession(collapsed, 'a');
    expect(windowSessions(one).sort()).toEqual(['b', 'c']);
    expect(dropSession(one, 'b').dock).toEqual([]);
  });

  it('takes a collapsed tile out of the side bar, and one tile out of a collapsed group', () => {
    const docked = applyLayoutAction(withTiles('a', 'b'), { type: 'tile/collapse', sessionId: 'b' });
    const { win: rest, node } = takeNode(docked, tileKey('b'));
    expect(node).toEqual({ kind: 'tile', sessionId: 'b' });
    expect(rest.dock).toEqual([]);

    const collapsed = applyLayoutAction(grouped(), { type: 'group/collapse', groupId: 'g' });
    const taken = takeNode(collapsed, tileKey('a'));
    expect(taken.node).toEqual({ kind: 'tile', sessionId: 'a' });
    expect(taken.win.dock).toMatchObject([
      { kind: 'group', node: { id: 'g', child: { kind: 'tile', sessionId: 'b' } } },
    ]);
    // The last one takes the group along.
    expect(takeNode(taken.win, tileKey('b')).win.dock).toEqual([]);
  });

  it('moves a collapsed tile next to a target, or to the end without one', () => {
    const docked = applyLayoutAction(withTiles('a', 'b', 'c'), { type: 'tile/collapse', sessionId: 'b' });
    const next = applyLayoutAction(docked, {
      type: 'node/move',
      key: tileKey('b'),
      target: { key: tileKey('a'), side: 'bottom' },
    });
    expect(next.dock).toEqual([]);
    expect(windowSessions(next).sort()).toEqual(['a', 'b', 'c']);
    const atEnd = applyLayoutAction(docked, { type: 'node/move', key: tileKey('b'), target: null });
    expect(atEnd.dock).toEqual([]);
    expect(leaves(atEnd.layout)).toContain('b');
    // One that is shown already stays where it is without a target.
    expect(applyLayoutAction(docked, { type: 'node/move', key: tileKey('a'), target: null })).toBe(docked);
    expect(isLayoutAction({ type: 'node/move', key: 'k', target: null })).toBe(true);
  });

  it('moves a group between windows', () => {
    const from = grouped();
    const { win: rest, node } = takeNode(from, 'g');
    expect(leaves(rest.layout)).toEqual(['c']);
    const to = putNode(withTiles('x'), node!, { key: tileKey('x'), side: 'bottom' });
    expect(groupOf(to.layout, tileKey('a'))?.id).toBe('g');
    expect(putNode(emptyWindow('w2'), node!, null).layout).toEqual(node);
  });
});

describe('view mode', () => {
  it('defaults to tiled for windows saved without one', () => {
    const old = windowStateSchema.parse(JSON.parse(JSON.stringify(emptyWindow('w1'))));
    expect(old.viewMode).toBeUndefined();
    expect(viewModeOf(old)).toBe('tiled');
  });

  it('is switched by window/viewMode, and only to a known mode', () => {
    const win = applyLayoutAction(emptyWindow('w1'), { type: 'window/viewMode', mode: 'single' });
    expect(viewModeOf(win)).toBe('single');
    expect(isLayoutAction({ type: 'window/viewMode', mode: 'single' })).toBe(true);
    expect(isLayoutAction({ type: 'window/viewMode', mode: 'zoomed' })).toBe(false);
    expect(isLayoutAction({ type: 'layout/tile' })).toBe(false);
  });
});

describe('settleGrid', () => {
  const keyOf = (w: ReturnType<typeof withTiles>) => settleGrid(w, undefined).key;

  it('leaves a window seen for the first time as it is', () => {
    const win = withTiles('a', 'b', 'c');
    expect(settleGrid(win, undefined).win).toBe(win);
  });

  it('regrids a tiled window when a tile is added, closed, docked or expanded', () => {
    const win = withTiles('a', 'b', 'c');
    const added = addTile(win, 'd');
    const settled = settleGrid(added, keyOf(win)).win;
    // 2×2: two rows of two.
    expect(settled.layout).toMatchObject({ kind: 'split', children: [{ kind: 'split' }, { kind: 'split' }] });
    expect(leaves(settled.layout).sort()).toEqual(['a', 'b', 'c', 'd']);

    const closed = dropSession(settled, 'd');
    expect(settleGrid(closed, keyOf(settled)).win).not.toBe(closed);
    const docked = applyLayoutAction(settled, { type: 'tile/collapse', sessionId: 'a' });
    expect(settleGrid(docked, keyOf(settled)).win).not.toBe(docked);
    const expanded = applyLayoutAction(docked, { type: 'tile/expand', sessionId: 'a' });
    expect(settleGrid(expanded, keyOf(docked)).win).not.toBe(expanded);
  });

  it('regrids when the project or the mode changes', () => {
    const win = withTiles('a', 'b');
    expect(settleGrid({ ...win, activeProject: 'p' }, keyOf(win)).win).not.toBe(win);
    const single = { ...win, viewMode: 'single' as const };
    expect(settleGrid(win, keyOf(single)).win).not.toBe(win);
  });

  it('does not regrid on a resize or a reorder, nor in single window mode', () => {
    const win = withTiles('a', 'b', 'c');
    const resized = { ...win, layout: { ...win.layout!, sizes: [0.5, 0.3, 0.2] } } as typeof win;
    expect(settleGrid(resized, keyOf(win)).win).toBe(resized);
    const single = { ...win, viewMode: 'single' as const };
    const added = addTile(single, 'd');
    expect(settleGrid(added, keyOf(single)).win).toBe(added);
  });
});
