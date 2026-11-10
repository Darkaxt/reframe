import { windowSessions } from '@shared/actions';
import type { SessionRecord, WindowState } from '@shared/state';

/**
 * Tiles of a window that can no longer resume: the session was used (so it needs its transcript), it is not running,
 * and Claude Code no longer has its transcript. `hasTranscript` answers true when it cannot tell (e.g. WSL folders).
 */
export function staleTiles(
  win: WindowState,
  sessions: Record<string, SessionRecord>,
  hasTranscript: (record: SessionRecord) => boolean,
  isRunning: (sessionId: string) => boolean,
): string[] {
  return windowSessions(win).filter((id) => {
    const record = sessions[id];
    return !!record?.used && !isRunning(id) && !hasTranscript(record);
  });
}
