// Threads (Claude Code sessions, by their Claude session id) the user hid from Reframe: never listed, offered, counted
// or notified, until restored in Settings. Hiding only changes what Reframe shows; the session's process, registry
// entry and transcript are left alone.
import type { HiddenThread } from './state';

/** The hidden threads' Claude session ids, for a Set lookup at discovery time. */
export function hiddenIds(hidden: readonly HiddenThread[] | undefined): Set<string> {
  return new Set((hidden ?? []).map((h) => h.claudeSessionId));
}

/** `hidden` with `thread` added (or its entry replaced: hidden again, it keeps the newest label and time). */
export function hideThread(hidden: readonly HiddenThread[] | undefined, thread: HiddenThread): HiddenThread[] {
  return [...(hidden ?? []).filter((h) => h.claudeSessionId !== thread.claudeSessionId), thread];
}

/** `hidden` without the given threads, or without any ('all'). */
export function restoreThreads(
  hidden: readonly HiddenThread[] | undefined,
  ids: readonly string[] | 'all',
): HiddenThread[] {
  if (ids === 'all') return [];
  const restored = new Set(ids);
  return (hidden ?? []).filter((h) => !restored.has(h.claudeSessionId));
}

/** What is not hidden, by each item's Claude session id. */
export function withoutHidden<T>(items: readonly T[], hidden: Set<string>, idOf: (item: T) => string): T[] {
  return hidden.size === 0 ? [...items] : items.filter((item) => !hidden.has(idOf(item)));
}
