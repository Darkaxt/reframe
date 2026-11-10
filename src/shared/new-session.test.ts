import { describe, expect, it } from 'vitest';
import { cleanAddDirs, cleanPrompt, defaultsFor, parseAddDirs, rememberedDefaults } from './new-session';

describe('new session defaults per provider', () => {
  it('falls back to Settings, the default model only for the ChatGPT route', () => {
    const preferences = { defaultModel: 'opus', defaultEffort: 'high' as const, defaultPermissionMode: 'plan' as const };
    expect(defaultsFor(preferences, 'chatgpt')).toEqual({
      model: 'opus',
      effort: 'high',
      permissionMode: 'plan',
      addDirs: [],
    });
    expect(defaultsFor(preferences, 'anthropic').model).toBeUndefined();
  });

  it('prefers what was used last for that provider, including a deliberate "Default" effort', () => {
    const preferences = {
      defaultEffort: 'high' as const,
      providerDefaults: { anthropic: { model: 'sonnet', addDirs: ['D:\\lib'] } },
    };
    expect(defaultsFor(preferences, 'anthropic')).toEqual({ model: 'sonnet', addDirs: ['D:\\lib'] });
    expect(defaultsFor(preferences, 'chatgpt').effort).toBe('high');
  });

  it('remembers only set choices', () => {
    expect(rememberedDefaults({ model: 'opus', addDirs: [] })).toEqual({ model: 'opus' });
  });
});

describe('new session fields', () => {
  it('reads one folder per line', () => {
    expect(parseAddDirs(' D:\\a \r\n\nD:\\a\nE:\\b ')).toEqual(['D:\\a', 'E:\\b']);
  });

  it('keeps absolute folders only and bounds them', () => {
    expect(cleanAddDirs(['D:\\a', 'relative', '\\\\srv\\share', '/home/me', 'C:/x\u0000', 42])).toEqual([
      'D:\\a',
      '\\\\srv\\share',
      '/home/me',
    ]);
    expect(cleanAddDirs('D:\\a')).toBeUndefined();
    expect(cleanAddDirs(Array.from({ length: 30 }, (_, i) => `D:\\d${i}`))).toHaveLength(20);
  });

  it('accepts multi-line prompts but not control characters or empty ones', () => {
    expect(cleanPrompt('  fix\nthe tests\t ')).toBe('fix\nthe tests');
    expect(cleanPrompt('bad\u0007')).toBeUndefined();
    expect(cleanPrompt('   ')).toBeUndefined();
    expect(cleanPrompt('x'.repeat(20_001))).toBeUndefined();
  });
});
