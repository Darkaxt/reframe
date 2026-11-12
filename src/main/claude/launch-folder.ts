import fs from 'node:fs';
import path from 'node:path';

/** How far into a transcript its launch folder is looked for: the first entries carry it. */
const HEAD_BYTES = 64 * 1024;
const HEAD_LINES = 20;

/** Claude Code's folder for a project's transcripts: the launch folder with every other character a dash. */
export function projectSlug(folder: string): string {
  return folder.replace(/[^a-zA-Z0-9]/g, '-');
}

function sameName(a: string, b: string): boolean {
  return process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
}

/** The `cwd` of the first entries of a transcript, reading only its head. Null when none is there. */
export function transcriptHeadCwd(file: string): string | null {
  let fd: number | undefined;
  try {
    fd = fs.openSync(file, 'r');
    const buffer = Buffer.alloc(HEAD_BYTES);
    const read = fs.readSync(fd, buffer, 0, HEAD_BYTES, 0);
    const lines = buffer.subarray(0, read).toString('utf8').split('\n').slice(0, HEAD_LINES);
    for (const line of lines) {
      try {
        const entry = JSON.parse(line) as { cwd?: unknown };
        if (typeof entry.cwd === 'string' && entry.cwd) return entry.cwd;
      } catch {
        // A line cut off by the head's end, or not JSON.
      }
    }
    return null;
  } catch {
    return null;
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
}

/**
 * The folder a Claude conversation was launched in, as its transcript tells: the cwd of its first entries, when the
 * project folder the transcript is in is that folder's. Null when it cannot be told.
 */
export function transcriptLaunchFolder(claudeHome: string, claudeSessionId: string): string | null {
  const projects = path.join(claudeHome, 'projects');
  let dirs: string[];
  try {
    dirs = fs.readdirSync(projects);
  } catch {
    return null;
  }
  for (const dir of dirs) {
    const file = path.join(projects, dir, `${claudeSessionId}.jsonl`);
    if (!fs.existsSync(file)) continue;
    const cwd = transcriptHeadCwd(file);
    if (cwd && sameName(projectSlug(cwd), dir)) return cwd;
  }
  return null;
}

/** Whether `inner` is a folder strictly inside `outer`. */
function isInside(inner: string, outer: string): boolean {
  const norm = (p: string) => {
    const resolved = path.resolve(p);
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
  };
  const relative = path.relative(norm(outer), norm(inner));
  return !!relative && !relative.startsWith('..') && !path.isAbsolute(relative);
}

/**
 * The launch folder a session's record should have instead of its `cwd`, or null to keep it. Up to
 * 0.10.1-claudegpt.25 a hook's cwd (which follows the model's `cd`) replaced the folder a session was launched in; the
 * sign of that is a record folder inside the transcript's launch folder. Anything else is left as it is.
 */
export function repairedLaunchFolder(
  claudeHome: string,
  record: { cwd: string; claudeSessionId?: string | null; launch?: { worktree?: unknown } },
): string | null {
  // A worktree session runs in a folder inside its repository on purpose.
  if (!record.claudeSessionId || !record.cwd || record.launch?.worktree) return null;
  // Its transcript is where its folder says: nothing moved.
  const own = path.join(claudeHome, 'projects', projectSlug(record.cwd), `${record.claudeSessionId}.jsonl`);
  if (fs.existsSync(own)) return null;
  const launch = transcriptLaunchFolder(claudeHome, record.claudeSessionId);
  return launch && isInside(record.cwd, launch) ? launch : null;
}

/**
 * Whether a cwd a hook or the registry tells may become a session's folder. Those follow the model's `cd`, while a
 * session's folder is the one it was launched in: only a record without one takes it, or a worktree session that has
 * yet to move into the worktree Claude Code makes for it.
 */
export function takesReportedCwd(record: {
  cwd?: string | null;
  launch: { worktree?: unknown; worktreeCreated?: unknown };
}): boolean {
  return !record.cwd || (!!record.launch.worktree && !record.launch.worktreeCreated);
}
