// What "New session" remembers per provider, and the checks for its free-form fields (shared by main and renderer).
import { EFFORT_LEVELS, PERMISSION_MODES, type EffortLevel, type PermissionMode } from './launch';
import type { Provider } from './providers';

/** The last choices made for a provider in "New session"; they fill the dialog next time that provider is picked. */
export interface ProviderDefaults {
  model?: string;
  effort?: EffortLevel;
  permissionMode?: PermissionMode;
  addDirs?: string[];
}

export const MAX_ADD_DIRS = 20;
export const MAX_PROMPT = 20_000;
// Control characters break a command line (and cmd.exe's in particular); tabs and newlines are fine in a prompt.
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/;

/**
 * The dialog's starting values for a provider: what was used for it last, else Settings' defaults (the default
 * model is the ChatGPT route's; the Anthropic route starts with its own).
 */
export function defaultsFor(
  preferences: {
    defaultModel?: string;
    defaultEffort?: EffortLevel;
    defaultPermissionMode?: PermissionMode;
    providerDefaults?: Partial<Record<Provider, ProviderDefaults>>;
  },
  provider: Provider,
): Required<Pick<ProviderDefaults, 'addDirs'>> & Omit<ProviderDefaults, 'addDirs'> {
  const remembered = preferences.providerDefaults?.[provider];
  if (remembered) return { ...remembered, addDirs: remembered.addDirs ?? [] };
  return {
    model: provider === 'chatgpt' ? preferences.defaultModel : undefined,
    effort: preferences.defaultEffort,
    permissionMode: preferences.defaultPermissionMode,
    addDirs: [],
  };
}

/** One folder per line in the dialog: trimmed, empty lines and repeats dropped. */
export function parseAddDirs(text: string): string[] {
  return [...new Set(text.split(/\r?\n/).map((line) => line.trim()))].filter(Boolean);
}

/** Main's check of the request's additional folders: absolute paths only (Windows or WSL), bounded. */
export function cleanAddDirs(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const dirs = [
    ...new Set(
      value
        .filter((dir): dir is string => typeof dir === 'string')
        .map((dir) => dir.trim())
        .filter((dir) => dir.length <= 1024 && !CONTROL.test(dir) && !/[\r\n\t]/.test(dir))
        .filter((dir) => /^[a-z]:[\\/]/i.test(dir) || dir.startsWith('\\\\') || dir.startsWith('/')),
    ),
  ].slice(0, MAX_ADD_DIRS);
  return dirs.length ? dirs : undefined;
}

/** Main's check of the first prompt: bounded, no control characters except tab and line breaks. */
export function cleanPrompt(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  const prompt = value.trim();
  return prompt && prompt.length <= MAX_PROMPT && !CONTROL.test(prompt) ? prompt : undefined;
}

/** What to remember for the provider after a session was created with these choices. */
export function rememberedDefaults(launch: ProviderDefaults): ProviderDefaults {
  return {
    ...(launch.model ? { model: launch.model } : {}),
    ...(launch.effort && EFFORT_LEVELS.includes(launch.effort) ? { effort: launch.effort } : {}),
    ...(launch.permissionMode && PERMISSION_MODES.includes(launch.permissionMode)
      ? { permissionMode: launch.permissionMode }
      : {}),
    ...(launch.addDirs?.length ? { addDirs: launch.addDirs } : {}),
  };
}
