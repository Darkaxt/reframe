import type { AppListItem, FoundListItem, ListItemRef, RunningListItem, SessionList } from './ipc';
import type { Provider } from './providers';

export type SidebarItem = AppListItem | RunningListItem | FoundListItem;
export const chatKey = (id: string, distro?: string): string => JSON.stringify([distro?.toLowerCase() || '', id]);
export function parseChatKey(key: string): { id: string; distro?: string } | null {
  try {
    const parts: unknown = JSON.parse(key);
    if (
      !Array.isArray(parts) ||
      parts.length !== 2 ||
      typeof parts[0] !== 'string' ||
      typeof parts[1] !== 'string' ||
      !/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(parts[1])
    )
      return null;
    return { id: parts[1], ...(parts[0] ? { distro: parts[0] } : {}) };
  } catch {
    return null;
  }
}

export interface SidebarChat {
  key: string;
  ref: ListItemRef;
  label: string;
  cwd: string;
  provider?: Provider;
  defaulted: boolean;
  lastActiveAt: number;
  status: AppListItem['status'];
  item: SidebarItem;
}

/** Source ownership stays an implementation detail; identity and recency span every source. */
export function sidebarChats(list: SessionList): SidebarChat[] {
  const result = new Map<string, SidebarChat>();
  for (const item of [...list.app, ...list.running, ...list.found, ...(list.pinnedItems || [])]) {
    const key = chatKey(item.claudeSessionId, item.distro);
    if (result.has(key)) continue;
    const folder = item.cwd.split(/[\\/]/).filter(Boolean).pop() || item.cwd;
    result.set(key, {
      key,
      item,
      cwd: item.cwd,
      ref:
        item.kind === 'app'
          ? { kind: 'app', sessionId: item.id }
          : item.kind === 'running'
            ? {
                kind: 'running',
                pid: item.pid,
                claudeSessionId: item.claudeSessionId,
                ...(item.distro ? { distro: item.distro } : {}),
              }
            : { kind: 'found', claudeSessionId: item.claudeSessionId, ...(item.distro ? { distro: item.distro } : {}) },
      label: item.kind === 'app' ? item.label : item.kind === 'running' ? item.name || folder : item.title || folder,
      provider: item.kind === 'app' ? item.provider : item.kind === 'found' ? 'chatgpt' : undefined,
      defaulted: item.kind === 'found',
      lastActiveAt: item.kind === 'found' ? item.modifiedAt : item.lastActiveAt || 0,
      status: item.kind === 'app' ? item.status : item.kind === 'running' ? 'idle' : 'ended',
    });
  }
  return [...result.values()].sort((a, b) => b.lastActiveAt - a.lastActiveAt || a.key.localeCompare(b.key));
}
