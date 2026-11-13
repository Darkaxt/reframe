// What reframe starts for a session: its program, arguments, folder, environment, hook settings and the meta its PTY
// host follows it by. SessionManager (../sessions.ts) builds its spawns with these functions; `spawnPlan` is the
// whole decision made purely (the PTY host's `launch.spawnPlan`, docs/ptyhost-protocol.md), so that a shell without
// Electron can start sessions as reframe does. The Rust host's port (rust/reframe-services/src/spawn_plan.rs) is held
// to it by the fixtures in test/spawn-plan-fixtures (spawn-plan.fixtures.test.ts).
import nodeFs from 'node:fs';
import path from 'node:path';
import { providerEnv, providerSettings, type Provider } from '@shared/providers';
import type { SessionRecord } from '@shared/state';
import { parseWslPath, wslWindowsPath } from '@shared/wsl';
import { claudeArgs, type LaunchMode } from './args';
import { claudeEnv } from './env';
import { transcriptExists } from './registry';
import { cmdCommandLine, resolveClaude, type ClaudeBinary } from './resolve';
import { hookSettingsJson } from './settings';

/**
 * How to start a session that should resume. Claude Code writes a transcript only from the first message on;
 * without one there is nothing to resume: an unused session starts fresh under the same id, a used one has lost
 * its history.
 */
export function launchMode(session: SessionRecord, requested: LaunchMode, transcript: boolean): LaunchMode | 'lost' {
  if (requested !== 'resume' || transcript) return requested;
  return session.used ? 'lost' : 'new';
}

/** The claude a session of `provider` runs when no client is adopted (app.ts findClaude): an explicit path, if any. */
export function claudeOverride(
  provider: Provider | undefined,
  env: Record<string, string | undefined>,
  claudeBin: string | undefined,
): string | undefined {
  return (
    (provider === 'anthropic' ? env.REFRAME_ANTHROPIC_CLAUDE_BIN : undefined) || claudeBin || env.REFRAME_CLAUDE_BIN
  );
}

export interface SpawnCommand {
  file: string;
  /** A string for cmd.exe (its whole command line). */
  args: string[] | string;
  cwd: string;
  env: Record<string, string>;
}

/** A Windows session's spawn, with `claude` found and its settings file at `settingsFile`. */
export function hereSpawn(
  session: SessionRecord,
  mode: LaunchMode,
  claude: ClaudeBinary,
  settingsFile: string,
  base: Record<string, string | undefined>,
): SpawnCommand {
  return {
    file: claude.file,
    args: claude.viaCmd
      ? cmdCommandLine(claude.viaCmd, claudeArgs(session, mode, settingsFile, { prompt: false }))
      : [...claude.prefixArgs, ...claudeArgs(session, mode, settingsFile)],
    cwd: session.cwd,
    env: providerEnv(claudeEnv(base, { ...claude.env, REFRAME_SESSION: session.id }), session.launch.provider),
  };
}

export interface WslCommandSpec {
  file: string;
  prefixArgs: string[];
  env: Record<string, string>;
}

/** A WSL session's spawn: wsl.exe running the distribution's claude in its folder, with its login shell's PATH. */
export function wslSpawn(
  session: SessionRecord,
  mode: LaunchMode,
  where: { distro: string; path: string },
  info: { claude: string; path: string },
  command: WslCommandSpec,
  homeDir: string,
  base: Record<string, string | undefined>,
): SpawnCommand {
  return {
    file: command.file,
    // Without a shell (so nothing the user's startup files print gets in), with the PATH their login shell has.
    args: [
      ...command.prefixArgs,
      ...['-d', where.distro, '--cd', where.path, '--exec', 'env', `PATH=${info.path}`, info.claude],
      ...claudeArgs(
        session,
        mode,
        session.launch.provider === 'anthropic' ? JSON.stringify(providerSettings('anthropic')) : undefined,
      ),
    ],
    cwd: homeDir,
    env: claudeEnv(base, command.env),
  };
}

/** What `spawnPlan` looks at on disk (the host's own; the fixtures give a made-up one). */
export interface PlanFs {
  isFile(file: string): boolean;
  readFile(file: string): string;
  /** Whether a Windows session's folder is there (anything by that name, as fs.existsSync). */
  exists(file: string): boolean;
  /** Whether a WSL session's folder is a folder. */
  isDir(dir: string): boolean;
  transcriptExists(home: string, claudeSessionId: string): boolean;
}

export type PlanSession = Pick<SessionRecord, 'id' | 'claudeSessionId' | 'cwd' | 'name' | 'claudeName' | 'used'> & {
  launch: SessionRecord['launch'];
};

export interface SpawnPlanInput {
  session: PlanSession;
  /** fresh: a new conversation (restart "start fresh"): `freshClaudeSessionId` replaces the session's, unused. */
  mode: 'new' | 'resume' | 'fresh';
  freshClaudeSessionId?: string;
  /** The environment claude inherits (reframe's own). */
  env: Record<string, string>;
  claudeHome: string;
  /** Where the per-session settings files go (`<settingsDir>/<session id>.json`). */
  settingsDir: string;
  /** The PTY host's hook server (its welcome's). */
  hooks: { port: number; token: string };
  /** Whether an Anthropic session's status line input is captured (the user has no status line of their own). */
  captureStatusLine?: boolean;
  /** The Settings choice of claude (preferences.claudeBin). */
  claudeBin?: string;
  /** Runs a .js claude (REFRAME_NODE's node when not given). */
  nodeRunner?: { file: string; env: Record<string, string> };
  /** The launch config refused this provider (why). */
  refused?: string;
  /** The adopted client for this provider (clients.adopted), which replaces every other choice. */
  adopted?: { ok: true; path: string } | { ok: false; reason: string };
  /** Windows sessions in WSL folders; without it, those folders are taken as any other. */
  wsl?: {
    share: string;
    command: WslCommandSpec;
    /** The distribution's answer (wsl info), null when it has no claude or did not answer. */
    info: { claude: string; path: string; claudeHome: string } | null;
    /** The Windows user's home folder, wsl.exe's folder. */
    homeDir: string;
  };
}

export type SpawnPlanError = 'folderMissing' | 'transcriptMissing' | 'claudeNotFound';

export type SpawnPlan =
  | {
      ok: true;
      /** What claude is started as: 'new' also for a resume of an unused session without a transcript. */
      mode: LaunchMode;
      file: string;
      args: string[] | string;
      cwd: string;
      env: Record<string, string>;
      /** The settings file to write before the spawn (Windows sessions). */
      settings?: { file: string; content: string };
      /** The spawn's meta (docs/ptyhost-protocol.md, `sessions`). */
      meta: Record<string, unknown>;
      /** What changes in the session's record (a fresh start). */
      patch?: { claudeSessionId: string; used: false };
      warnings: string[];
    }
  | { ok: false; error: SpawnPlanError; warnings: string[] };

/** Why `params` is no spawnPlan input (the Rust host's messages), or null. */
export function spawnPlanInputProblem(params: Record<string, unknown>): string | null {
  const isObject = (v: unknown): v is Record<string, unknown> =>
    typeof v === 'object' && v !== null && !Array.isArray(v);
  const { session, hooks } = params;
  if (!isObject(session)) return 'session must be an object';
  for (const key of ['id', 'claudeSessionId', 'cwd'])
    if (typeof session[key] !== 'string') return `session.${key} must be a string`;
  if (typeof params.mode !== 'string') return 'mode must be a string';
  if (!['new', 'resume', 'fresh'].includes(params.mode)) return `mode ${params.mode} is not new, resume or fresh`;
  if (params.mode === 'fresh' && !params.freshClaudeSessionId) return 'freshClaudeSessionId is required';
  if (!isObject(params.env)) return 'env must be an object';
  for (const key of ['claudeHome', 'settingsDir'])
    if (typeof params[key] !== 'string') return `${key} must be a string`;
  if (!isObject(hooks)) return 'hooks must be an object';
  if (typeof hooks.port !== 'number' || !Number.isInteger(hooks.port) || hooks.port < 0)
    return 'hooks.port must be a number';
  if (typeof hooks.token !== 'string') return 'hooks.token must be a string';
  return null;
}

/** The machine's own disk. */
export const realPlanFs: PlanFs = {
  isFile: (file) => {
    try {
      return nodeFs.statSync(file).isFile();
    } catch {
      return false;
    }
  },
  readFile: (file) => nodeFs.readFileSync(file, 'utf8'),
  exists: (file) => nodeFs.existsSync(file),
  isDir: (dir) => {
    try {
      return nodeFs.statSync(dir).isDirectory();
    } catch {
      return false;
    }
  },
  transcriptExists,
};

const FRESH_NOTE = 'no transcript yet, starting fresh under the same id';

/** The spawn SessionManager.start makes for `input` (without starting, writing or asking anything). */
export function spawnPlan(input: SpawnPlanInput, fs: PlanFs): SpawnPlan {
  const warnings: string[] = [];
  const fresh = input.mode === 'fresh';
  if (fresh && !input.freshClaudeSessionId) throw new Error('freshClaudeSessionId is required');
  const session = (
    fresh ? { ...input.session, claudeSessionId: input.freshClaudeSessionId, used: false } : input.session
  ) as SessionRecord;
  const requested: LaunchMode = fresh ? 'new' : input.mode === 'resume' ? 'resume' : 'new';
  const patch = fresh ? { patch: { claudeSessionId: session.claudeSessionId, used: false as const } } : {};
  const fail = (error: SpawnPlanError): SpawnPlan => ({ ok: false, error, warnings });
  const provider = session.launch.provider;

  const where = input.wsl && parseWslPath(session.cwd, input.wsl.share);
  if (input.wsl && where) {
    const { info } = input.wsl;
    if (!info?.claude) return fail('claudeNotFound');
    if (!fs.isDir(session.cwd)) return fail('folderMissing');
    const home = wslWindowsPath(where.distro, info.claudeHome, input.wsl.share);
    const mode = launchMode(
      session,
      requested,
      requested !== 'resume' || fs.transcriptExists(home, session.claudeSessionId),
    );
    if (mode === 'lost') return fail('transcriptMissing');
    if (mode !== requested) warnings.push(FRESH_NOTE);
    return {
      ok: true,
      mode,
      ...wslSpawn(session, mode, where, info, input.wsl.command, input.wsl.homeDir, input.env),
      meta: {
        wsl: { distro: where.distro, home, claudeSessionId: session.claudeSessionId, share: input.wsl.share },
      },
      ...patch,
      warnings,
    };
  }

  if (!fs.exists(session.cwd)) return fail('folderMissing');
  const mode = launchMode(
    session,
    requested,
    requested !== 'resume' || fs.transcriptExists(input.claudeHome, session.claudeSessionId),
  );
  if (mode === 'lost') return fail('transcriptMissing');

  const claude = findClaude(input, provider, fs, warnings);
  if (!claude) return fail('claudeNotFound');
  if (mode !== requested) warnings.push(FRESH_NOTE);
  if (claude.viaCmd)
    warnings.push(`claude is a launcher reframe does not know, it runs through cmd.exe: ${claude.source}`);

  const file = path.win32.join(input.settingsDir, `${session.id}.json`);
  const content = hookSettingsJson(
    input.hooks.port,
    input.hooks.token,
    session.id,
    provider,
    provider === 'anthropic' && !!input.captureStatusLine,
  );
  return {
    ok: true,
    mode,
    ...hereSpawn(session, mode, claude, file, input.env),
    settings: { file, content },
    meta: {
      claudeHome: input.claudeHome,
      ...(claude.viaCmd ? { launched: { claudeSessionId: session.claudeSessionId } } : {}),
    },
    ...patch,
    warnings,
  };
}

/** app.ts findClaude, for Windows: the adopted client, else the chosen one, else the one on the PATH. */
function findClaude(
  input: SpawnPlanInput,
  provider: Provider | undefined,
  fs: PlanFs,
  warnings: string[],
): ClaudeBinary | null {
  const which = provider ?? 'chatgpt';
  if (input.refused) {
    warnings.push(`provider refused by launch config: ${which}: ${input.refused}`);
    return null;
  }
  const node = input.nodeRunner ?? (input.env.REFRAME_NODE ? { file: input.env.REFRAME_NODE, env: {} } : undefined);
  let override: string | undefined;
  if (input.adopted) {
    if (!input.adopted.ok) {
      warnings.push(`adopted client refused: ${which}: ${input.adopted.reason}`);
      return null;
    }
    override = input.adopted.path;
  } else {
    override = claudeOverride(provider, input.env, input.claudeBin);
  }
  // Electron runs a .js claude itself (as node); a plan has no Electron to name.
  if (override && /\.(c|m)?js$/i.test(override) && !node) {
    warnings.push(`a .js claude needs a node to run it (nodeRunner or REFRAME_NODE): ${override}`);
    return null;
  }
  return resolveClaude({
    env: input.env,
    platform: 'win32',
    override,
    nodeRunner: node,
    isFile: (p) => fs.isFile(p),
    readFile: (p) => fs.readFile(p),
  });
}
