// Enter in a terminal tile, as in the Windows console: with text selected it copies the selection (nothing reaches
// the session); Shift+Enter without a selection types a newline into the prompt. xterm sends "\r" for both Enter and
// Shift+Enter; ESC CR (Meta+Enter) is what Claude Code reads as a newline (its /terminal-setup binds the same).

export const NEWLINE = '\x1b\r';

type Key = Pick<KeyboardEvent, 'type' | 'key' | 'shiftKey' | 'ctrlKey' | 'altKey' | 'metaKey'>;

/** Per terminal: whether the keypress that follows a handled Enter keydown must be dropped too. */
export interface EnterState {
  swallow: boolean;
}

/**
 * What to do with a key event: 'copy' the selection, type a 'newline', 'swallow' it (the keypress after a handled
 * keydown, or xterm would still send "\r"), or null to leave it to xterm.
 */
export function enterKey(event: Key, hasSelection: boolean, state: EnterState): 'copy' | 'newline' | 'swallow' | null {
  if (event.key !== 'Enter' || event.ctrlKey || event.altKey || event.metaKey) return null;
  if (event.type === 'keydown') {
    const action = hasSelection ? 'copy' : event.shiftKey ? 'newline' : null;
    state.swallow = action !== null;
    return action;
  }
  if (event.type === 'keypress' && state.swallow) {
    state.swallow = false;
    return 'swallow';
  }
  if (event.type === 'keyup') state.swallow = false;
  return null;
}

/** The copied text as the copy notice shows it: on one line, its first `max` characters. */
export function copyPreview(text: string, max = 40): string {
  const line = text.replace(/\s+/g, ' ').trim();
  return line.length > max ? `${line.slice(0, max)}…` : line;
}

/** Whether a mouse drag in a terminal that reads the mouse would have selected text, if Shift had been held. */
export function dragWithoutShift(
  start: { x: number; y: number } | null,
  move: Pick<MouseEvent, 'clientX' | 'clientY' | 'buttons' | 'shiftKey'>,
  mouseTracking: boolean,
): boolean {
  if (!start || !mouseTracking || move.shiftKey || (move.buttons & 1) === 0) return false;
  return Math.hypot(move.clientX - start.x, move.clientY - start.y) > 4;
}
