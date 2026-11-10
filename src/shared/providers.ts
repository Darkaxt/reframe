/** User-selected routing. Errors never choose or change a provider. */
export const PROVIDERS = ['chatgpt', 'anthropic'] as const;
export type Provider = (typeof PROVIDERS)[number];
export const providerName = (provider?: Provider) =>
  provider === 'chatgpt' ? 'ChatGPT' : provider === 'anthropic' ? 'Anthropic' : 'Provider not selected';
export const isProvider = (value: unknown): value is Provider => PROVIDERS.includes(value as Provider);

/** Reselecting a route is a no-op for its explicit model; a different route starts with its own default. */
export function modelForProviderChange(
  current: Provider | undefined,
  next: Provider,
  model: string | undefined,
): string | undefined {
  return (current ?? 'chatgpt') === next ? model : undefined;
}

// Remove gateway/auth/model overrides only for the explicitly chosen subscription route.
// Host permissions, managed settings, project instructions, MCP, OAuth storage and effort stay intact.
export const ROUTING_ENV = [
  'ANTHROPIC_BASE_URL',
  'ANTHROPIC_AUTH_TOKEN',
  'ANTHROPIC_API_KEY',
  'ANTHROPIC_MODEL',
  'ANTHROPIC_DEFAULT_OPUS_MODEL',
  'ANTHROPIC_DEFAULT_SONNET_MODEL',
  'ANTHROPIC_DEFAULT_HAIKU_MODEL',
  'ANTHROPIC_SMALL_FAST_MODEL',
  'CLAUDE_CODE_SUBAGENT_MODEL',
  'ANTHROPIC_CUSTOM_HEADERS',
  'ANTHROPIC_CUSTOM_MODEL_OPTION',
  'ANTHROPIC_CUSTOM_MODEL_OPTION_NAME',
  'CLAUDE_CODE_USE_BEDROCK',
  'CLAUDE_CODE_USE_VERTEX',
  'CLAUDE_CODE_USE_FOUNDRY',
] as const;

export function providerSettings(provider?: Provider): Record<string, unknown> {
  if (provider !== 'anthropic') return {};
  return {
    env: Object.fromEntries(
      ROUTING_ENV.map((key) => [key, key === 'ANTHROPIC_BASE_URL' ? 'https://api.anthropic.com' : '']),
    ),
    apiKeyHelper: '',
    model: 'sonnet',
  };
}

export function providerEnv(env: Record<string, string>, provider?: Provider): Record<string, string> {
  if (provider !== 'anthropic') return env;
  const next = { ...env };
  for (const key of Object.keys(next))
    if (ROUTING_ENV.some((k) => k.toLowerCase() === key.toLowerCase())) delete next[key];
  next.ANTHROPIC_BASE_URL = 'https://api.anthropic.com';
  return next;
}
