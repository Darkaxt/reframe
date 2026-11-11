// Per-session settings file passed via `claude --settings`. Claude Code merges its hooks with the
// user's and the project's own hooks. HTTP hooks report to reframe's local hook server.
import fs from 'node:fs';
import path from 'node:path';
import { providerSettings, type Provider } from '@shared/providers';

export const HOOK_EVENTS = ['UserPromptSubmit', 'Stop', 'Notification'] as const;
/** Claude Code's status line input, posted to the hook server: its `rate_limits` are the subscription's quota. */
export const STATUS_LINE_EVENT = 'StatusLine';
export type HookEvent = (typeof HOOK_EVENTS)[number] | typeof STATUS_LINE_EVENT;
export const HOOK_TOKEN_HEADER = 'X-Reframe-Token';

export function hookSettings(port: number, token: string, sessionId: string): object {
  const hooks: Record<string, unknown> = {};
  for (const event of HOOK_EVENTS) {
    hooks[event] = [
      {
        hooks: [
          {
            type: 'http',
            url: `http://127.0.0.1:${port}/hook/${encodeURIComponent(sessionId)}/${event}`,
            headers: { [HOOK_TOKEN_HEADER]: token },
          },
        ],
      },
    ];
  }
  return { hooks };
}

/**
 * A status line command that hands Claude Code's status line input to reframe and shows nothing. Claude Code runs it
 * through a shell (Git Bash or cmd); the Windows curl.exe works from both.
 */
export function statusLineSettings(port: number, token: string, sessionId: string): object {
  const url = `http://127.0.0.1:${port}/hook/${encodeURIComponent(sessionId)}/${STATUS_LINE_EVENT}`;
  return {
    statusLine: {
      type: 'command',
      command: `curl.exe -s -m 2 -o NUL -X POST -H "${HOOK_TOKEN_HEADER}: ${token}" -H "content-type: application/json" --data-binary @- "${url}"`,
    },
  };
}

export function writeHookSettings(
  dir: string,
  port: number,
  token: string,
  sessionId: string,
  provider?: Provider,
  statusLine = false,
): string {
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${sessionId}.json`);
  fs.writeFileSync(
    file,
    JSON.stringify(
      {
        ...hookSettings(port, token, sessionId),
        ...(statusLine ? statusLineSettings(port, token, sessionId) : {}),
        ...providerSettings(provider),
      },
      null,
      2,
    ),
  );
  return file;
}
