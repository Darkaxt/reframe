// A started-directly reframe.exe (no managed launcher) reads reframe-config.json next to itself: the fixed profile,
// runtime folder and the exact binary of each provider's CLI, each with its sha256. It sets what the managed launcher
// would have set; a variable the launcher did set wins, so a managed start is unchanged. Neither CLI is put on the
// PATH, and a provider whose binary is missing or changed is refused instead of falling back to PATH or the other one.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import type { Provider } from '@shared/providers';
import { adoptedClient } from './client-adoption';

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
  /** The client adoption record (adopt-client.ps1); when it is valid its clients replace the two bindings above. */
  clientAdoption?: string;
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

/** What a launch config sets: its profile and runtime folders, the variables it gives and what it refuses. */
export interface LaunchPlan {
  file: string;
  userData: string;
  runtime: string;
  claudeHome: string;
  /** REFRAME_CLIENT_ADOPTION, the two CLI bindings and the history helper's variables, as far as they are usable. */
  env: Record<string, string>;
  refused: Partial<Record<Provider, string>>;
  problems: string[];
}

const isAbsolute = (p: unknown): p is string => typeof p === 'string' && path.isAbsolute(p);

/**
 * Reads the launch config `file` and verifies its bindings by hash, without changing anything; null when it cannot be
 * read or lacks an absolute profile, runtime or Claude folder. (The PTY host's `launch.evaluate` answers the same.)
 */
export function evaluateLaunchConfig(file: string): LaunchPlan | null {
  let config: LaunchConfig;
  try {
    config = JSON.parse(fs.readFileSync(file, 'utf8')) as LaunchConfig;
  } catch {
    return null;
  }
  if (!config || typeof config !== 'object') return null;
  if (![config.userData, config.runtime, config.claudeHome].every(isAbsolute)) return null;
  const plan: LaunchPlan = {
    file,
    userData: config.userData,
    runtime: config.runtime,
    claudeHome: config.claudeHome,
    env: {},
    refused: {},
    problems: [],
  };
  const { env } = plan;

  // Adopted clients: the record is re-read for each new session (app.findClaude); a provider it cannot supply is
  // refused there. Without a usable record the fixed bindings below apply.
  const adoption = isAbsolute(config.clientAdoption) ? config.clientAdoption : null;
  const chatgptAdopted = adoption ? adoptedClient(adoption, 'chatgpt') : null;
  const anthropicAdopted = adoption ? adoptedClient(adoption, 'anthropic') : null;
  if (chatgptAdopted?.ok && anthropicAdopted?.ok) {
    env.REFRAME_CLIENT_ADOPTION = adoption!;
    env.REFRAME_CLAUDE_BIN = chatgptAdopted.path;
    env.REFRAME_ANTHROPIC_CLAUDE_BIN = anthropicAdopted.path;
  } else {
    for (const c of [chatgptAdopted, anthropicAdopted]) if (c && !c.ok) plan.problems.push(c.reason);
    const chatgpt = check(config.claudeBin, 'ChatGPT CLI');
    if (chatgpt) plan.refused.chatgpt = chatgpt;
    else env.REFRAME_CLAUDE_BIN = config.claudeBin.path;
    const anthropic = check(config.anthropicClaudeBin, 'Anthropic CLI');
    if (anthropic) plan.refused.anthropic = anthropic;
    else env.REFRAME_ANTHROPIC_CLAUDE_BIN = config.anthropicClaudeBin!.path;
  }

  const history = config.history;
  const helper = history && check({ path: history.helper, sha256: history.helperSha256 }, 'history helper');
  if (history && !helper && isAbsolute(history.python)) {
    env.REFRAME_HISTORY_PYTHON = history.python;
    env.REFRAME_HISTORY_HELPER = history.helper;
    env.REFRAME_HISTORY_HELPER_SHA256 = history.helperSha256;
  } else plan.problems.push(helper || 'history helper: not configured');
  return plan;
}

/** The launch config next to a direct start (REFRAME_USER_DATA unset, not a test) of a packaged Windows build. */
export function launchConfigFile(
  execPath: string,
  env: NodeJS.ProcessEnv,
  { packaged, platform = process.platform }: { packaged: boolean; platform?: NodeJS.Platform },
): string | null {
  if (!packaged || platform !== 'win32' || env.REFRAME_USER_DATA || env.REFRAME_E2E) return null;
  return path.join(path.dirname(execPath), LAUNCH_CONFIG);
}

/** Sets what `plan` gives in `env` (the variables of the managed launcher's it replaces are cleared first). */
export function applyLaunchPlan(plan: LaunchPlan, env: NodeJS.ProcessEnv): AppliedLaunchConfig {
  fs.mkdirSync(plan.runtime, { recursive: true });
  env.REFRAME_USER_DATA = plan.userData;
  env.REFRAME_RUNTIME = plan.runtime;
  env.TEMP = env.TMP = plan.runtime;
  env.REFRAME_CLAUDE_HOME = plan.claudeHome;
  delete env.CLAUDE_CONFIG_DIR;
  delete env.REFRAME_CLAUDE_BIN;
  delete env.REFRAME_ANTHROPIC_CLAUDE_BIN;
  delete env.REFRAME_CLIENT_ADOPTION;
  Object.assign(env, plan.env);
  return { file: plan.file, refused: { ...plan.refused }, problems: [...plan.problems] };
}

/**
 * Applies reframe-config.json to `env` when this is a direct start (REFRAME_USER_DATA unset, not a test) of a
 * packaged Windows build with such a file next to it. Returns null when nothing was applied. It runs before any PTY
 * host is reached (the profile and runtime it sets decide which host that is), so it reads here.
 */
export function applyLaunchConfig(
  execPath: string,
  env: NodeJS.ProcessEnv,
  options: { packaged: boolean; platform?: NodeJS.Platform },
): AppliedLaunchConfig | null {
  const file = launchConfigFile(execPath, env, options);
  const plan = file && evaluateLaunchConfig(file);
  return plan ? applyLaunchPlan(plan, env) : null;
}
