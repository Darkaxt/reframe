// Command line for starting or resuming a session.
import type { SessionRecord } from '@shared/state';

export type LaunchMode = 'new' | 'resume';

/** `settingsFile` brings reframe's hooks; without one (WSL, out of the hooks' reach) the status comes from the registry. */
export function claudeArgs(
  session: SessionRecord,
  mode: LaunchMode,
  settingsFile?: string,
  options: { prompt?: boolean } = {},
): string[] {
  const args = mode === 'new' ? ['--session-id', session.claudeSessionId] : ['--resume', session.claudeSessionId];
  const name = session.claudeName === undefined ? session.name : session.claudeName;
  if (name) args.push('--name', name);
  if (session.launch.model) args.push('--model', session.launch.model);
  else if (session.launch.provider === 'anthropic') args.push('--model', 'sonnet');
  if (session.launch.permissionMode) args.push('--permission-mode', session.launch.permissionMode);
  if (session.launch.effort) args.push('--effort', session.launch.effort);
  // A worktree session that was started once runs in its worktree folder; -w again would make another one in it.
  if (mode === 'new' && session.launch.worktree && !session.launch.worktreeCreated) {
    args.push('--worktree');
    if (session.launch.worktreeName) args.push(session.launch.worktreeName);
  }
  for (const dir of session.launch.addDirs ?? []) args.push('--add-dir', dir);
  if (settingsFile) args.push('--settings', settingsFile);
  // The first message, only on the very first start: once used, the transcript has it. A leading "-" would read as
  // an option. Not through cmd.exe (options.prompt false): free text there would meet %-expansion and quoting.
  const prompt = session.launch.initialPrompt;
  if (prompt && mode === 'new' && !session.used && options.prompt !== false)
    args.push(prompt.startsWith('-') ? ` ${prompt}` : prompt);
  return args;
}
