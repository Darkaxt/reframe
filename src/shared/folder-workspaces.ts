import type { Provider } from './providers';

/** Lexical canonicalization is independent of renderer OS APIs; actual folders stay untouched. */
export function folderKey(folder: string): string {
  const windows = /^[a-z]:[\\/]|^\\\\/i.test(folder);
  const normalized = folder.replace(/\\/g, '/').replace(/\/+$/, '');
  const parts: string[] = [];
  for (const part of normalized.split('/')) {
    if (part === '.') continue;
    if (part === '..' && parts.length > 1) parts.pop();
    else parts.push(part);
  }
  const key = parts.join('/') || '/';
  return windows ? key.toLowerCase() : key;
}

export interface FolderWorkspace {
  id: string;
  cwd: string;
  name: string;
  sessionIds: string[];
  locations: { cwd: string; provider: Provider; sessionIds: string[] }[];
}
/** Project grouping is visual; it never changes the actual working-directory identity. */
export const workspaceKey = (folder: string): string =>
  `project:${folderKey(folder).split('/').filter(Boolean).pop()?.toLowerCase() || folderKey(folder)}`;

export const expandedWorkspaceKey = (key: string): string =>
  key.startsWith('project:') ? key : workspaceKey(key.replace(/^(chatgpt|anthropic):/, ''));

export function folderWorkspaces(chats: { cwd: string; id: string; provider?: Provider }[]): FolderWorkspace[] {
  const result = new Map<string, FolderWorkspace>();
  for (const chat of chats) {
    const provider = chat.provider ?? 'chatgpt';
    const id = workspaceKey(chat.cwd);
    let group = result.get(id);
    if (!group) {
      group = {
        id,
        cwd: chat.cwd,
        name: chat.cwd.split(/[\\/]/).filter(Boolean).pop() || chat.cwd,
        sessionIds: [],
        locations: [],
      };
      result.set(id, group);
    }
    if (!group.sessionIds.includes(chat.id)) group.sessionIds.push(chat.id);
    let location = group.locations.find(
      (entry) => folderKey(entry.cwd) === folderKey(chat.cwd) && entry.provider === provider,
    );
    if (!location) {
      location = { cwd: chat.cwd, provider, sessionIds: [] };
      group.locations.push(location);
    }
    if (!location.sessionIds.includes(chat.id)) location.sessionIds.push(chat.id);
  }
  return [...result.values()].sort((a, b) => a.name.localeCompare(b.name));
}
