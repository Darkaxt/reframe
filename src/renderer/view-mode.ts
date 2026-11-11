// Single window mode: which thread fills the window as its tiles come and go.

/**
 * The thread to show after the window's threads went from `before` to `after`: one that just came (a new session,
 * one opened from the list) is shown; otherwise the selected one while it is there; when it closed, the one that
 * took its place (else the last); else the first.
 */
export function nextShown(before: string[], after: string[], selected: string | null): string | null {
  const added = after.filter((id) => !before.includes(id));
  if (added.length === 1 && before.length > 0) return added[0]!;
  if (selected && after.includes(selected)) return selected;
  const index = selected ? before.indexOf(selected) : -1;
  if (index >= 0) {
    const rest = before.filter((id) => after.includes(id));
    const following = before.slice(index + 1).find((id) => after.includes(id));
    return following ?? rest[rest.length - 1] ?? after[0] ?? null;
  }
  return after[0] ?? null;
}

/** The thread `step` places from `current` among `ids`, going round. */
export function cycle(ids: string[], current: string | null, step: number): string | null {
  if (ids.length === 0) return null;
  const index = current ? ids.indexOf(current) : -1;
  if (index < 0) return ids[0]!;
  return ids[(index + step + ids.length) % ids.length]!;
}
