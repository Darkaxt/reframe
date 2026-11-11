import { describe, expect, it } from 'vitest';
import { gridLayout, gridShape, leaves, tile, type LayoutNode, type SplitNode } from './layout';
import { projectActivity, showProject } from './project-view';
import { workspaceKey } from './folder-workspaces';
import { emptyWindow } from './state';

let n = 0;
const id = () => `s${++n}`;

describe('gridShape', () => {
  it('is as square as possible, wider than tall', () => {
    expect([1, 2, 3, 4, 5, 6, 7, 9, 10].map((c) => gridShape(c))).toEqual([
      { rows: 1, cols: 1 },
      { rows: 1, cols: 2 },
      { rows: 2, cols: 2 },
      { rows: 2, cols: 2 },
      { rows: 2, cols: 3 },
      { rows: 2, cols: 3 },
      { rows: 3, cols: 3 },
      { rows: 3, cols: 3 },
      { rows: 3, cols: 4 },
    ]);
  });
});

describe('gridLayout', () => {
  it('keeps one tile as it is and puts two side by side', () => {
    expect(gridLayout([tile('a')], id)).toEqual(tile('a'));
    expect(gridLayout([tile('a'), tile('b')], id)).toMatchObject({ kind: 'split', dir: 'row', sizes: [50, 50] });
  });
  it('makes rows of columns, a shorter last row sharing its width', () => {
    const grid = gridLayout(['a', 'b', 'c', 'd', 'e'].map(tile), id) as SplitNode;
    expect(grid.dir).toBe('col');
    expect(grid.sizes).toEqual([50, 50]);
    const [top, bottom] = grid.children as SplitNode[];
    expect(leaves(top!)).toEqual(['a', 'b', 'c']);
    expect(leaves(bottom!)).toEqual(['d', 'e']);
    expect(bottom!.sizes).toEqual([50, 50]);
  });
  it('keeps a group whole', () => {
    const group: LayoutNode = { kind: 'group', id: 'g', name: 'G', color: '#fff', child: tile('x') };
    const grid = gridLayout([group, tile('a')], id) as SplitNode;
    expect(grid.children[0]).toEqual(group);
  });
});

describe('showProject', () => {
  const cwd: Record<string, string> = { a: 'C:/w/shop', b: 'C:/w/blog' };
  const projectOf = (s: string) => (cwd[s] ? workspaceKey(cwd[s]) : undefined);
  const docked = (dock: { kind: string; sessionId?: string }[]) => dock.map((d) => d.sessionId);
  it('collapses other projects and brings back its own', () => {
    let win = { ...emptyWindow('w'), layout: gridLayout([tile('a'), tile('b')], id) };
    win = showProject(win, workspaceKey('C:/w/blog'), projectOf);
    expect(leaves(win.layout)).toEqual(['b']);
    expect(docked(win.dock)).toEqual(['a']);
    expect(win.activeProject).toBe(workspaceKey('C:/w/blog'));
    win = showProject(win, workspaceKey('C:/w/shop'), projectOf);
    expect(leaves(win.layout)).toEqual(['a']);
    expect(docked(win.dock)).toEqual(['b']);
  });
});

describe('projectActivity', () => {
  it('counts running sessions per project with the state that most needs attention', () => {
    const activity = projectActivity([
      { cwd: 'C:/w/shop', status: 'idle' },
      { cwd: 'D:/other/shop', status: 'busy' },
      { cwd: 'C:/w/blog', status: 'starting' },
      { cwd: 'C:/w/blog', status: 'waiting' },
      { cwd: 'C:/w/docs', status: 'ended' },
      { cwd: 'C:/w/api', status: 'error' },
    ]);
    expect(activity.get(workspaceKey('C:/w/shop'))).toEqual({ count: 2, status: 'busy' });
    expect(activity.get(workspaceKey('C:/w/blog'))).toEqual({ count: 2, status: 'waiting' });
    expect(activity.get(workspaceKey('C:/w/api'))).toEqual({ count: 1, status: 'error' });
    expect(activity.has(workspaceKey('C:/w/docs'))).toBe(false);
  });
});
