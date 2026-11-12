// Subscription quota windows as their sources report them: Claude Code's status line input (rate_limits) for
// Anthropic, LLM Coordinator's usage snapshot for Codex (the ChatGPT route) and, from Coordinators that observe it
// too (`providers`), for Claude when reframe's own status line is not there to tell it. Whatever windows a source reports are
// shown, so a window the plan drops simply disappears.

export interface QuotaWindow {
  /** Stable per source: "five_hour", "seven_day", "codex:10080". */
  id: string;
  /** Length of the window in minutes, when known (labels it: 5h, 7d). */
  minutes: number | null;
  /** 0..100. */
  usedPercent: number;
  /** When the window resets (ms since epoch), when known. */
  resetsAt: number | null;
}

export interface ProviderQuota {
  /** Which subscription: Claude (Anthropic) or Codex (ChatGPT route). */
  source: 'claude' | 'codex';
  windows: QuotaWindow[];
  /** When the source last reported it (ms). */
  at: number;
}

const finite = (value: unknown): number | null => (typeof value === 'number' && Number.isFinite(value) ? value : null);
const clampPercent = (value: number) => Math.min(100, Math.max(0, value));

/** Seconds or milliseconds since the epoch, or an ISO string, as ms. */
function time(value: unknown): number | null {
  const n = finite(value);
  if (n !== null) return n < 1e12 ? n * 1000 : n;
  if (typeof value === 'string') {
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

const CLAUDE_MINUTES: Record<string, number> = { five_hour: 300, seven_day: 10080 };

/** `rate_limits` of Claude Code's status line input: `{ five_hour: { used_percentage, resets_at }, … }`. */
export function parseClaudeRateLimits(raw: unknown): QuotaWindow[] {
  if (!raw || typeof raw !== 'object') return [];
  const windows: QuotaWindow[] = [];
  for (const [id, entry] of Object.entries(raw as Record<string, unknown>)) {
    if (!entry || typeof entry !== 'object') continue;
    const used = finite((entry as Record<string, unknown>).used_percentage);
    if (used === null) continue;
    windows.push({
      id,
      minutes: CLAUDE_MINUTES[id] ?? null,
      usedPercent: clampPercent(used),
      resetsAt: time((entry as Record<string, unknown>).resets_at),
    });
  }
  return windows.sort((a, b) => (a.minutes ?? Infinity) - (b.minutes ?? Infinity));
}

/** A source's quota windows from LLM Coordinator, and when it observed them last (ms). */
export interface ObservedQuota {
  windows: QuotaWindow[];
  at: number;
}

/**
 * The quota per provider (`codex`, `claude`) of LLM Coordinator's `usage.snapshot` response, from its `providers:
 * [{ id, observedAt, windows: [{ id, windowMinutes, resetAt, remaining, points: [{ t, remaining }] }] }]` (newer
 * Coordinators). A window's last point says how much is left, and when.
 */
export function parseCoordinatorProviders(raw: unknown): Partial<Record<ProviderQuota['source'], ObservedQuota>> {
  const usage = (raw as { usage?: unknown } | null)?.usage ?? raw;
  const providers = (usage as { providers?: unknown } | null)?.providers;
  const quotas: Partial<Record<ProviderQuota['source'], ObservedQuota>> = {};
  if (!Array.isArray(providers)) return quotas;
  for (const provider of providers as Record<string, unknown>[]) {
    const source = provider?.id;
    if ((source !== 'codex' && source !== 'claude') || !Array.isArray(provider.windows)) continue;
    let at = finite(provider.observedAt);
    const windows: QuotaWindow[] = [];
    for (const window of provider.windows as Record<string, unknown>[]) {
      if (!window || typeof window !== 'object') continue;
      const last = (Array.isArray(window.points) ? (window.points as Record<string, unknown>[]) : []).at(-1);
      const remaining = finite(last?.remaining) ?? finite(window.remaining);
      if (remaining === null) continue;
      const t = finite(last?.t);
      if (t !== null && (at === null || t > at)) at = t;
      const minutes = finite(window.windowMinutes);
      windows.push({
        id: `${source}:${minutes ?? (typeof window.id === 'string' ? window.id : '')}`,
        minutes,
        usedPercent: clampPercent(100 - remaining),
        resetsAt: time(window.resetAt),
      });
    }
    if (windows.length && at !== null) {
      quotas[source] = { windows: windows.sort((a, b) => (a.minutes ?? Infinity) - (b.minutes ?? Infinity)), at };
    }
  }
  return quotas;
}

/**
 * Whether the Claude quota LLM Coordinator observed replaces the one shown: reframe's own status line (each answer
 * of a session it started) is preferred, so the Coordinator's counts only when it is newer.
 */
export function coordinatorQuotaIsNewer(shown: ProviderQuota | undefined, observed: ObservedQuota): boolean {
  return !shown || observed.at > shown.at;
}

/**
 * The Codex windows of LLM Coordinator's `usage.snapshot` response: its `codex` provider (see
 * parseCoordinatorProviders), or from older Coordinators its `buckets: [{ id, windowMinutes, resetAt, points:
 * [{ t, remaining }] }]`. Of buckets for the same window, the one observed last counts.
 */
export function parseCoordinatorUsage(raw: unknown): QuotaWindow[] {
  const codex = parseCoordinatorProviders(raw).codex;
  if (codex) return codex.windows;
  const usage = (raw as { usage?: unknown } | null)?.usage ?? raw;
  const buckets = (usage as { buckets?: unknown } | null)?.buckets;
  if (!Array.isArray(buckets)) return [];
  const latest = new Map<string, { at: number; window: QuotaWindow }>();
  for (const bucket of buckets as Record<string, unknown>[]) {
    if (!bucket || typeof bucket !== 'object') continue;
    const points = Array.isArray(bucket.points) ? (bucket.points as Record<string, unknown>[]) : [];
    const last = points.at(-1);
    const remaining = finite(last?.remaining);
    const at = finite(last?.t);
    const minutes = finite(bucket.windowMinutes);
    if (remaining === null || at === null) continue;
    const id = `${typeof bucket.id === 'string' ? bucket.id : 'codex'}:${minutes ?? ''}`;
    const prior = latest.get(id);
    if (prior && prior.at >= at) continue;
    latest.set(id, {
      at,
      window: { id, minutes, usedPercent: clampPercent(100 - remaining), resetsAt: time(bucket.resetAt) },
    });
  }
  return [...latest.values()].map((e) => e.window).sort((a, b) => (a.minutes ?? Infinity) - (b.minutes ?? Infinity));
}

/** "5h", "7d", "90m" for a window length. */
export function windowLabel(minutes: number | null): string {
  if (!minutes) return '';
  if (minutes % 1440 === 0) return `${minutes / 1440}d`;
  if (minutes % 60 === 0) return `${minutes / 60}h`;
  return `${minutes}m`;
}

/** The same colours as session states: plenty left green, getting used orange, nearly gone red. */
export function quotaLevel(usedPercent: number): 'ok' | 'warn' | 'danger' {
  return usedPercent >= 90 ? 'danger' : usedPercent >= 70 ? 'warn' : 'ok';
}

/** How long until a reset, compactly: "3d 4h", "2h 10m", "12m" ("<1m" when due). */
export function untilLabel(resetsAt: number, now: number): string {
  const minutes = Math.max(0, Math.floor((resetsAt - now) / 60_000));
  if (minutes < 1) return '<1m';
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const mins = minutes % 60;
  if (days) return hours ? `${days}d ${hours}h` : `${days}d`;
  if (hours) return mins ? `${hours}h ${mins}m` : `${hours}h`;
  return `${mins}m`;
}
