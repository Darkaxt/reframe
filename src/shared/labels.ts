/**
 * What a session is called: the name the user gave it in reframe, else Claude Code's own title for it (its latest
 * custom or AI title), else its folder. The folder is only that last fallback, never stored or passed as --name.
 */
export function sessionLabel(name: string | undefined, title: string | null | undefined, cwd: string): string {
  return name?.trim() || title?.trim() || cwd.split(/[\\/]/).filter(Boolean).pop() || cwd;
}
