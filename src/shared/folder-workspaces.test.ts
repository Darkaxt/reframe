import { describe, it, expect } from 'vitest';
import { expandedWorkspaceKey, folderKey, folderWorkspaces, workspaceKey } from './folder-workspaces';
describe('folder workspaces', () => {
  it('merges final folder names across paths and providers, retaining actual locations', () => {
    const groups = folderWorkspaces([
      { id: 'a', cwd: 'D:\\work\\game' },
      { id: 'b', cwd: 'd:/WORK/game/' },
      { id: 'c', cwd: 'D:/other/game', provider: 'anthropic' },
      { id: 'd', cwd: 'D:/other/different' },
    ]);
    expect(groups).toHaveLength(2);
    const game = groups.find((g) => g.id === workspaceKey('d:/work/game'))!;
    expect(game.sessionIds).toEqual(['a', 'b', 'c']);
    expect(game.locations).toEqual([
      { cwd: 'D:\\work\\game', provider: 'chatgpt', sessionIds: ['a', 'b'] },
      { cwd: 'D:/other/game', provider: 'anthropic', sessionIds: ['c'] },
    ]);
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
  it('merges providers at the same folder without losing their actual bindings', () => {
    const groups = folderWorkspaces([
      { id: 'gpt', cwd: 'D:/work/game', provider: 'chatgpt' },
      { id: 'claude', cwd: 'd:\\WORK\\game\\', provider: 'anthropic' },
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0]?.sessionIds).toEqual(['gpt', 'claude']);
    expect(groups[0]?.locations.map((location) => location.provider)).toEqual(['chatgpt', 'anthropic']);
  });
  it('migrates legacy full-path and provider-scoped expansion into one project', () => {
    expect(expandedWorkspaceKey('chatgpt:c:/owner/ChatGPT/Android')).toBe('project:android');
    expect(expandedWorkspaceKey('anthropic:c:/owner/Anthropic/Android')).toBe('project:android');
    expect(expandedWorkspaceKey('d:/other/ANDROID')).toBe('project:android');
    expect(expandedWorkspaceKey('project:android')).toBe('project:android');
  });
});
