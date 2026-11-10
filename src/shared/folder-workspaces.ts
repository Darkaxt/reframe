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
  provider: Provider;
}
export const workspaceKey = (folder: string, provider: Provider = 'chatgpt'): string =>
  `${provider}:${folderKey(folder)}`;

export function folderWorkspaces(chats: { cwd: string; id: string; provider?: Provider }[]): FolderWorkspace[] {
  const result = new Map<string, FolderWorkspace>();
  for (const chat of chats) {
    const provider = chat.provider ?? 'chatgpt';
    const id = workspaceKey(chat.cwd, provider);
    let group = result.get(id);
    if (!group) {
      group = {
        id,
        cwd: chat.cwd,
        name: chat.cwd.split(/[\\/]/).filter(Boolean).pop() || chat.cwd,
        sessionIds: [],
        provider,
      };
      result.set(id, group);
    }
    if (!group.sessionIds.includes(chat.id)) group.sessionIds.push(chat.id);
  }
  return [...result.values()].sort((a, b) => a.cwd.localeCompare(b.cwd));
}
