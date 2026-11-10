import { describe, it, expect } from 'vitest';
import { folderKey, folderWorkspaces } from './folder-workspaces';
describe('folder workspaces', () => {
  it('groups Windows case/separators/dot aliases without merging same basenames', () => {
    const groups = folderWorkspaces([
      { id: 'a', cwd: 'D:\\work\\game' },
      { id: 'b', cwd: 'd:/WORK/game/' },
      { id: 'c', cwd: 'D:/other/game' },
    ]);
    expect(groups).toHaveLength(2);
    expect(groups.find((g) => g.id === 'd:/work/game')?.sessionIds).toEqual(['a', 'b']);
    expect(folderKey('D:/work/./game/../game')).toBe('d:/work/game');
  });
  it('keeps Linux paths case-sensitive and removes duplicate session identities', () => {
    expect(folderKey('/A')).not.toBe(folderKey('/a'));
    expect(
      folderWorkspaces([
        { id: 'one', cwd: '/a' },
        { id: 'one', cwd: '/a' },
      ])[0]?.sessionIds,
    ).toEqual(['one']);
  });
});
