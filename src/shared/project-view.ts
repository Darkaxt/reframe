// A window shows one project (sidebar "workspace") at a time, can tile its tiles into a grid, and the session list
// shows each project's activity. Pure functions, used by main and the renderer.
import { applyLayoutAction, dockSessions } from './actions';
import { layoutUnits, leaves } from './layout';
import type { SessionStatus } from './ipc';
import type { WindowState } from './state';
import { workspaceKey } from './folder-workspaces';

/**
 * Shows one project in the window: tiles (and groups) of other projects are collapsed, the project's collapsed ones
 * come back. `projectOf` gives a session's project key, or undefined for one that is gone.
 */
export function showProject(
  win: WindowState,
  key: string,
  projectOf: (sessionId: string) => string | undefined,
): WindowState {
  const belongs = (ids: string[]) => ids.some((id) => projectOf(id) === key);
  let next: WindowState = { ...win, activeProject: key };
  for (const unit of layoutUnits(win.layout)) {
    if (belongs(leaves(unit))) continue;
    next =
      unit.kind === 'group'
        ? applyLayoutAction(next, { type: 'group/collapse', groupId: unit.id })
        : applyLayoutAction(next, { type: 'tile/collapse', sessionId: leaves(unit)[0]! });
  }
  for (const entry of win.dock) {
    if (!belongs(dockSessions(entry))) continue;
    next =
      entry.kind === 'group'
        ? applyLayoutAction(next, { type: 'group/expand', groupId: entry.node.id })
        : applyLayoutAction(next, { type: 'tile/expand', sessionId: entry.sessionId });
  }
  return next;
}

/** A project's activity: how many of its sessions run, and the state that most needs attention among them. */
export interface ProjectActivity {
  count: number;
  status: 'waiting' | 'error' | 'busy' | 'background' | 'idle';
}

const ATTENTION: ProjectActivity['status'][] = ['waiting', 'error', 'busy', 'background', 'idle'];
const ACTIVE: ReadonlySet<SessionStatus> = new Set(['starting', 'idle', 'background', 'busy', 'waiting', 'error']);

export function projectActivity(sessions: { cwd: string; status: SessionStatus }[]): Map<string, ProjectActivity> {
  const statuses = new Map<string, SessionStatus[]>();
  for (const session of sessions) {
    if (!ACTIVE.has(session.status)) continue;
    const key = workspaceKey(session.cwd);
    statuses.set(key, [...(statuses.get(key) ?? []), session.status]);
  }
  const result = new Map<string, ProjectActivity>();
  for (const [key, list] of statuses) {
    // Starting counts as busy: it is working, nobody needs to act yet.
    const shown = list.map((s) => (s === 'starting' ? 'busy' : s));
    result.set(key, { count: list.length, status: ATTENTION.find((s) => shown.includes(s)) ?? 'idle' });
  }
  return result;
}
