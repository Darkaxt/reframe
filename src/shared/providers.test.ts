import { describe, it, expect } from 'vitest';
import { providerEnv, providerSettings, ROUTING_ENV, modelForProviderChange } from './providers';
import { appStateSchema, defaultState } from './state';
import { claudeArgs } from '../main/claude/args';

describe('per-chat provider routing', () => {
  it('preserves an explicit model when reselecting the same route', () => {
    expect(modelForProviderChange('chatgpt', 'chatgpt', 'gpt-fixture')).toBe('gpt-fixture');
    expect(modelForProviderChange(undefined, 'chatgpt', 'gpt-fixture')).toBe('gpt-fixture');
    expect(modelForProviderChange('chatgpt', 'anthropic', 'gpt-fixture')).toBeUndefined();
  });
  it('leaves ChatGPT environment/settings untouched', () => {
    const env = {
      ANTHROPIC_AUTH_TOKEN: 'fixture-proxy',
      ANTHROPIC_BASE_URL: 'http://127.0.0.1:8317',
      HOST_PERMISSION: 'keep',
    };
    expect(providerEnv(env, 'chatgpt')).toBe(env);
    expect(providerSettings('chatgpt')).toEqual({});
  });
  it('isolates subscription routing without touching native permissions or credential storage', () => {
    const env = Object.fromEntries(ROUTING_ENV.map((k) => [k, 'fixture-gateway']));
    const result = providerEnv({ ...env, HOST_PERMISSION: 'keep', CLAUDE_CONFIG_DIR: '/original' }, 'anthropic');
    expect(result).toEqual({
      HOST_PERMISSION: 'keep',
      CLAUDE_CONFIG_DIR: '/original',
      ANTHROPIC_BASE_URL: 'https://api.anthropic.com',
    });
    const settings = providerSettings('anthropic');
    expect(settings).not.toHaveProperty('permissions');
    expect(settings.env).toMatchObject({
      ANTHROPIC_AUTH_TOKEN: '',
      ANTHROPIC_API_KEY: '',
      ANTHROPIC_BASE_URL: 'https://api.anthropic.com',
    });
  });
  it('preserves old state and persists explicit providers without changing history identity', () => {
    const old = defaultState();
    expect(appStateSchema.parse(old)).toMatchObject({ sessions: {} });
    const session = {
      id: 'app',
      claudeSessionId: 'history',
      cwd: 'D:/work',
      launch: { provider: 'anthropic' as const },
      lifecycle: 'ended' as const,
      createdAt: 0,
      lastActiveAt: 0,
    };
    const parsed = appStateSchema.parse({ ...old, sessions: { app: session } });
    expect(parsed.sessions.app?.launch.provider).toBe('anthropic');
    expect(claudeArgs(session, 'resume')).toEqual(['--resume', 'history', '--model', 'sonnet']);
    expect(claudeArgs({ ...session, launch: { ...session.launch, model: 'opus' } }, 'resume')).toContain('opus');
  });
});
