// A started-directly reframe.exe (no managed launcher) reads reframe-config.json next to itself: the fixed profile,
// runtime folder and the exact binary of each provider's CLI, each with its sha256. It sets what the managed launcher
// would have set; a variable the launcher did set wins, so a managed start is unchanged. Neither CLI is put on the
// PATH, and a provider whose binary is missing or changed is refused instead of falling back to PATH or the other one.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import type { Provider } from '@shared/providers';

export const LAUNCH_CONFIG = 'reframe-config.json';

interface Binary {
  path: string;
  sha256: string;
}
export interface LaunchConfig {
  userData: string;
  runtime: string;
  claudeHome: string;
  claudeBin: Binary;
  anthropicClaudeBin?: Binary;
  history?: { python: string; helper: string; helperSha256: string };
}

export interface AppliedLaunchConfig {
  file: string;
  refused: Partial<Record<Provider, string>>;
  problems: string[];
}

/** Providers this direct start refuses (set once at startup), with the reason. */
export const refusedProviders: Partial<Record<Provider, string>> = {};

const sha256 = (file: string) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

function check(binary: Binary | undefined, what: string): string | null {
  if (!binary || typeof binary.path !== 'string' || !path.isAbsolute(binary.path))
    return `${what}: no binary configured`;
  try {
    const actual = sha256(binary.path);
    return actual === String(binary.sha256).toLowerCase() ? null : `${what}: ${binary.path} changed (sha256 ${actual})`;
  } catch {
    return `${what}: ${binary.path} cannot be read`;
  }
}

/**
 * Applies reframe-config.json to `env` when this is a direct start (REFRAME_USER_DATA unset, not a test) of a
 * packaged Windows build with such a file next to it. Returns null when nothing was applied.
 */
export function applyLaunchConfig(
  execPath: string,
  env: NodeJS.ProcessEnv,
  { packaged, platform = process.platform }: { packaged: boolean; platform?: NodeJS.Platform },
): AppliedLaunchConfig | null {
  if (!packaged || platform !== 'win32' || env.REFRAME_USER_DATA || env.REFRAME_E2E) return null;
  const file = path.join(path.dirname(execPath), LAUNCH_CONFIG);
  let config: LaunchConfig;
  try {
    config = JSON.parse(fs.readFileSync(file, 'utf8')) as LaunchConfig;
  } catch {
    return null;
  }
  if (![config.userData, config.runtime, config.claudeHome].every((p) => typeof p === 'string' && path.isAbsolute(p)))
    return null;
  const result: AppliedLaunchConfig = { file, refused: {}, problems: [] };
  fs.mkdirSync(config.runtime, { recursive: true });
  env.REFRAME_USER_DATA = config.userData;
  env.REFRAME_RUNTIME = config.runtime;
  env.TEMP = env.TMP = config.runtime;
  env.REFRAME_CLAUDE_HOME = config.claudeHome;
  delete env.CLAUDE_CONFIG_DIR;
  delete env.REFRAME_CLAUDE_BIN;
  delete env.REFRAME_ANTHROPIC_CLAUDE_BIN;

  const chatgpt = check(config.claudeBin, 'ChatGPT CLI');
  if (chatgpt) result.refused.chatgpt = chatgpt;
  else env.REFRAME_CLAUDE_BIN = config.claudeBin.path;
  const anthropic = check(config.anthropicClaudeBin, 'Anthropic CLI');
  if (anthropic) result.refused.anthropic = anthropic;
  else env.REFRAME_ANTHROPIC_CLAUDE_BIN = config.anthropicClaudeBin!.path;

  const history = config.history;
  const helper = history && check({ path: history.helper, sha256: history.helperSha256 }, 'history helper');
  if (history && !helper && path.isAbsolute(history.python)) {
    env.REFRAME_HISTORY_PYTHON = history.python;
    env.REFRAME_HISTORY_HELPER = history.helper;
    env.REFRAME_HISTORY_HELPER_SHA256 = history.helperSha256;
  } else result.problems.push(helper || 'history helper: not configured');
  return result;
}
