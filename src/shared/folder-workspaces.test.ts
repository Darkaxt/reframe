import { describe, it, expect } from 'vitest';
import { expandedWorkspaceKey, folderKey, folderWorkspaces, rebaseFolder, workspaceKey } from './folder-workspaces';
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

describe('rebaseFolder', () => {
  const from = 'C:\\Users\\me\\Documents\\ChatGPT\\Windows';
  const to = 'C:\\Users\\me\\Documents\\Anthropic\\Windows';
  it('moves a folder inside the root, keeping its relative part as written', () => {
    expect(rebaseFolder(`${from}\\Foo/Bar`, from, to)).toBe(`${to}\\Foo/Bar`);
    expect(rebaseFolder('c:/users/ME/documents/chatgpt/windows/Foo', from, to)).toBe(`${to}\\Foo`);
    expect(rebaseFolder(`${from}\\foo`, `${from}\\`, `${to}\\`)).toBe(`${to}\\foo`);
  });
  it('gives the new root for the root itself and for an empty folder', () => {
    expect(rebaseFolder(from, from, to)).toBe(to);
    expect(rebaseFolder(`${from}\\`, from, to)).toBe(to);
    expect(rebaseFolder('', from, to)).toBe(to);
  });
  it('leaves folders outside the root, siblings included', () => {
    expect(rebaseFolder('D:\\Projects\\x', from, to)).toBe('D:\\Projects\\x');
    const sibling = 'C:\\Users\\me\\Documents\\ChatGPT2\\Windows\\x';
    expect(rebaseFolder(sibling, 'C:\\Users\\me\\Documents\\ChatGPT', to)).toBe(sibling);
    expect(rebaseFolder(`${from}2\\x`, from, to)).toBe(`${from}2\\x`);
  });
});
