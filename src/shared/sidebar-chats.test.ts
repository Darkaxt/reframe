import { describe, expect, it } from 'vitest';
import { chatKey, parseChatKey, sidebarChats } from './sidebar-chats';
import type { AppListItem, SessionList } from './ipc';

const id = '6d4c6300-9f9c-4fe5-a76d-5091b6b1f11a';
const managed: AppListItem = {
  kind: 'app',
  id: 'managed',
  claudeSessionId: id,
  label: 'Managed',
  cwd: 'D:\\Project',
  provider: 'anthropic',
  status: 'ended',
  lifecycle: 'ended',
  windowId: null,
  windowNumber: null,
  lastActiveAt: 10,
  elsewhere: false,
};
const empty: SessionList = { app: [], running: [], found: [], hasMore: false, wslStopped: [] };
describe('unified sidebar conversations', () => {
  it('keeps a canonical identity through history adoption and retains the recorded provider', () => {
    const found = { kind: 'found' as const, claudeSessionId: id, cwd: 'D:\\Project', title: 'Old', modifiedAt: 5 };
    const before = sidebarChats({ ...empty, found: [found] })[0]!;
    const after = sidebarChats({ ...empty, app: [managed], pinnedItems: [found] });
    expect(after).toHaveLength(1);
    expect(after[0]!.key).toBe(before.key);
    expect(after[0]!.provider).toBe('anthropic');
    expect(after[0]!.defaulted).toBe(false);
    expect(after[0]!.ref).toEqual({ kind: 'app', sessionId: 'managed' });
  });
  it('orders sources together by activity and distinguishes WSL identities', () => {
    const chats = sidebarChats({
      ...empty,
      app: [managed],
      running: [
        {
          kind: 'running',
          pid: 1,
          claudeSessionId: id,
          cwd: '\\\\wsl$\\Ubuntu\\home\\project',
          distro: 'Ubuntu',
          lastActiveAt: 20,
        },
      ],
      found: [
        { kind: 'found', claudeSessionId: 'b34f6c30-9f9c-4fe5-a76d-5091b6b1f11a', cwd: 'D:\\Other', modifiedAt: 30 },
      ],
    });
    expect(chats.map((chat) => chat.lastActiveAt)).toEqual([30, 20, 10]);
    expect(chats[0]!.provider).toBe('chatgpt');
    expect(chats[0]!.defaulted).toBe(true);
    expect(chats[1]!.provider).toBeUndefined();
    expect(chatKey(id, 'Ubuntu')).toBe(chatKey(id, 'ubuntu'));
    expect(chatKey(id)).not.toBe(chatKey(id, 'Ubuntu'));
    expect(parseChatKey(chatKey(id, 'Ubuntu'))).toEqual({ id, distro: 'ubuntu' });
    expect(parseChatKey('["", "../outside"]')).toBeNull();
  });
});
