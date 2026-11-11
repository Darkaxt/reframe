import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { applyLaunchConfig } from './launch-config';

const temps: string[] = [];
afterEach(() => {
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

function release(mutate?: (config: Record<string, unknown>) => void) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-launch-'));
  temps.push(dir);
  const bin = (name: string) => {
    const file = path.join(dir, name);
    fs.writeFileSync(file, name);
    return { path: file, sha256: crypto.createHash('sha256').update(name).digest('hex') };
  };
  const helper = bin('helper.py');
  const config: Record<string, unknown> = {
    userData: path.join(dir, 'profile'),
    runtime: path.join(dir, 'runtime'),
    claudeHome: path.join(dir, 'home'),
    claudeBin: bin('chatgpt.exe'),
    anthropicClaudeBin: bin('anthropic.exe'),
    history: { python: path.join(dir, 'python.exe'), helper: helper.path, helperSha256: helper.sha256 },
  };
  mutate?.(config);
  fs.writeFileSync(path.join(dir, 'reframe-config.json'), JSON.stringify(config));
  return { exe: path.join(dir, 'reframe.exe'), config };
}
const opts = { packaged: true, platform: 'win32' as const };

describe('applyLaunchConfig', () => {
  it('sets the profile and both exact CLI routes for a direct start', () => {
    const { exe, config } = release();
    const env: NodeJS.ProcessEnv = { CLAUDE_CONFIG_DIR: 'x', PATH: 'p' };
    const applied = applyLaunchConfig(exe, env, opts);
    expect(applied?.refused).toEqual({});
    expect(env.REFRAME_USER_DATA).toBe(config.userData);
    expect(env.TEMP).toBe(config.runtime);
    expect(env.TMP).toBe(config.runtime);
    expect(env.REFRAME_CLAUDE_BIN).toBe((config.claudeBin as { path: string }).path);
    expect(env.REFRAME_ANTHROPIC_CLAUDE_BIN).toBe((config.anthropicClaudeBin as { path: string }).path);
    expect(env.REFRAME_HISTORY_HELPER_SHA256).toBeTruthy();
    expect(env.CLAUDE_CONFIG_DIR).toBeUndefined();
    expect(env.PATH).toBe('p');
    expect(fs.statSync(config.runtime as string).isDirectory()).toBe(true);
  });

  it('refuses a provider whose binary changed, without falling back to the other', () => {
    const { exe } = release((c) => ((c.anthropicClaudeBin as { sha256: string }).sha256 = '0'.repeat(64)));
    const env: NodeJS.ProcessEnv = {};
    const applied = applyLaunchConfig(exe, env, opts);
    expect(applied?.refused.anthropic).toMatch(/changed/);
    expect(applied?.refused.chatgpt).toBeUndefined();
    expect(env.REFRAME_ANTHROPIC_CLAUDE_BIN).toBeUndefined();
    expect(env.REFRAME_CLAUDE_BIN).toBeTruthy();
  });

  it('leaves a managed launch, a test, a development build and a release without the file alone', () => {
    const { exe } = release();
    const managed: NodeJS.ProcessEnv = { REFRAME_USER_DATA: 'managed' };
    expect(applyLaunchConfig(exe, managed, opts)).toBeNull();
    expect(managed).toEqual({ REFRAME_USER_DATA: 'managed' });
    expect(applyLaunchConfig(exe, { REFRAME_E2E: '1' }, opts)).toBeNull();
    expect(applyLaunchConfig(exe, {}, { packaged: false, platform: 'win32' })).toBeNull();
    expect(applyLaunchConfig(exe, {}, { packaged: true, platform: 'linux' })).toBeNull();
    fs.rmSync(path.join(path.dirname(exe), 'reframe-config.json'));
    expect(applyLaunchConfig(exe, {}, opts)).toBeNull();
  });
  it('uses a valid client adoption record instead of the fixed bindings', () => {
    const { exe, config } = release((c) => {
      // The fixed ChatGPT binding changed: irrelevant once the adopted clients apply.
      (c.claudeBin as { sha256: string }).sha256 = '0'.repeat(64);
    });
    const dir = path.dirname(exe);
    const adopted = path.join(dir, 'adopted.exe');
    fs.writeFileSync(adopted, 'adopted');
    const sha256 = crypto.createHash('sha256').update('adopted').digest('hex');
    const record = path.join(dir, 'client-adoption.json');
    fs.writeFileSync(
      record,
      JSON.stringify({
        schema: 'reframe.client-adoption.v1',
        version: '2.1.322',
        clients: { chatgpt: { binary: adopted, sha256 }, anthropic: { binary: adopted, sha256 } },
      }),
    );
    fs.writeFileSync(path.join(dir, 'reframe-config.json'), JSON.stringify({ ...config, clientAdoption: record }));
    const env: NodeJS.ProcessEnv = {};
    const applied = applyLaunchConfig(exe, env, opts);
    expect(applied?.refused).toEqual({});
    expect(env.REFRAME_CLIENT_ADOPTION).toBe(record);
    expect(env.REFRAME_CLAUDE_BIN).toBe(adopted);
    expect(env.REFRAME_ANTHROPIC_CLAUDE_BIN).toBe(adopted);

    // A changed adopted client falls back to the fixed bindings (here: the changed ChatGPT one is refused).
    fs.writeFileSync(adopted, 'tampered');
    const fallback: NodeJS.ProcessEnv = {};
    const second = applyLaunchConfig(exe, fallback, opts);
    expect(fallback.REFRAME_CLIENT_ADOPTION).toBeUndefined();
    expect(second?.refused.chatgpt).toMatch(/changed/);
    expect(second?.problems.join(' ')).toMatch(/adopted .* changed/);
  });
});
