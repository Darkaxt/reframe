import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { projectSlug, repairedLaunchFolder, takesReportedCwd, transcriptLaunchFolder } from './launch-folder';

const temps: string[] = [];
afterEach(() => {
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

/** A Claude home with one transcript, launched in `launch`, kept in the project folder `slug`. */
function home(launch: string, slug = projectSlug(launch)): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-launch-folder-'));
  temps.push(dir);
  fs.mkdirSync(path.join(dir, 'projects', slug), { recursive: true });
  const lines = [
    JSON.stringify({ type: 'permission-mode', permissionMode: 'default' }),
    JSON.stringify({ type: 'user', cwd: launch, message: { content: 'hello' } }),
    JSON.stringify({ type: 'assistant', cwd: path.join(launch, 'sub', 'src'), message: { content: 'cd sub/src' } }),
  ];
  fs.writeFileSync(path.join(dir, 'projects', slug, 'conv.jsonl'), lines.join('\n') + '\n');
  return dir;
}

const launch = path.resolve(os.tmpdir(), 'Downloads', 'Public');
const moved = path.join(launch, 'llm-coordinator', 'native-rust', 'src');
const record = (cwd: string, extra: object = {}) => ({ cwd, claudeSessionId: 'conv', launch: {}, ...extra });

describe('transcriptLaunchFolder', () => {
  it('reads the cwd of the first entries', () => {
    expect(transcriptLaunchFolder(home(launch), 'conv')).toBe(launch);
  });

  it('is null when the transcript sits in another folder than its cwd', () => {
    expect(transcriptLaunchFolder(home(launch, 'elsewhere'), 'conv')).toBeNull();
  });

  it('is null without a transcript', () => {
    expect(transcriptLaunchFolder(home(launch), 'other')).toBeNull();
  });
});

describe('repairedLaunchFolder', () => {
  it('sets a folder that cd moved into back to the launch folder', () => {
    expect(repairedLaunchFolder(home(launch), record(moved))).toBe(launch);
  });

  it('keeps a folder that is the launch folder', () => {
    expect(repairedLaunchFolder(home(launch), record(launch))).toBeNull();
  });

  it('keeps a folder outside the launch folder', () => {
    expect(repairedLaunchFolder(home(launch), record(path.resolve(os.tmpdir(), 'Other')))).toBeNull();
  });

  it('keeps worktree sessions and sessions without a transcript', () => {
    expect(repairedLaunchFolder(home(launch), record(moved, { launch: { worktree: true } }))).toBeNull();
    expect(repairedLaunchFolder(home(launch), record(moved, { claudeSessionId: 'other' }))).toBeNull();
  });
});

describe('takesReportedCwd', () => {
  it('does not let a hook cwd move a session that has a folder', () => {
    expect(takesReportedCwd({ cwd: launch, launch: {} })).toBe(false);
  });

  it('takes it for a record without a folder, or a worktree session not yet in its worktree', () => {
    expect(takesReportedCwd({ cwd: '', launch: {} })).toBe(true);
    expect(takesReportedCwd({ cwd: launch, launch: { worktree: true } })).toBe(true);
    expect(takesReportedCwd({ cwd: launch, launch: { worktree: true, worktreeCreated: true } })).toBe(false);
  });
});
