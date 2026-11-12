import { describe, expect, it } from 'vitest';
import {
  coordinatorQuotaIsNewer,
  parseClaudeRateLimits,
  parseCoordinatorProviders,
  parseCoordinatorUsage,
  quotaLevel,
  untilLabel,
  windowLabel,
} from './quota';

describe('parseClaudeRateLimits', () => {
  it('reads every reported window, shortest first, resets in ms', () => {
    expect(
      parseClaudeRateLimits({
        seven_day: { used_percentage: 41.2, resets_at: 1_800_000_000 },
        five_hour: { used_percentage: 120, resets_at: 1_799_990_000 },
      }),
    ).toEqual([
      { id: 'five_hour', minutes: 300, usedPercent: 100, resetsAt: 1_799_990_000_000 },
      { id: 'seven_day', minutes: 10080, usedPercent: 41.2, resetsAt: 1_800_000_000_000 },
    ]);
  });
  it('drops what it cannot read, so a window the plan lacks disappears', () => {
    expect(parseClaudeRateLimits({ seven_day: { used_percentage: 3 }, five_hour: null, x: 'y' })).toEqual([
      { id: 'seven_day', minutes: 10080, usedPercent: 3, resetsAt: null },
    ]);
    expect(parseClaudeRateLimits(undefined)).toEqual([]);
  });
});

describe('parseCoordinatorUsage', () => {
  it('takes the latest observed bucket per window', () => {
    const response = {
      ok: true,
      usage: {
        buckets: [
          {
            id: 'codex',
            windowMinutes: 10080,
            resetAt: 2e12,
            points: [
              { t: 10, remaining: 32 },
              { t: 30, remaining: 4 },
            ],
          },
          { id: 'codex', windowMinutes: 10080, resetAt: 1.9e12, points: [{ t: 20, remaining: 50 }] },
          { id: 'codex', windowMinutes: 300, resetAt: 2e12, points: [] },
        ],
      },
    };
    expect(parseCoordinatorUsage(response)).toEqual([
      { id: 'codex:10080', minutes: 10080, usedPercent: 96, resetsAt: 2e12 },
    ]);
    expect(parseCoordinatorUsage({ ok: false, error: 'x' })).toEqual([]);
  });
});

describe('parseCoordinatorProviders', () => {
  // Trimmed from a live Coordinator's usage.snapshot (forecasts and most points left out).
  const snapshot = {
    ok: true,
    usage: {
      buckets: [{ id: 'codex', windowMinutes: 10080, resetAt: 1794758099000, points: [{ t: 1, remaining: 50 }] }],
      providers: [
        {
          id: 'codex',
          label: 'Codex',
          state: 'available',
          observedAt: 1794479110531.8745,
          windows: [
            {
              id: 'weekly',
              windowMinutes: 10080,
              resetAt: 1794758099000,
              remaining: 1,
              points: [
                { remaining: 3, t: 1794470000000 },
                { remaining: 1, t: 1794479110531.8745 },
              ],
            },
          ],
        },
        {
          id: 'claude',
          label: 'Claude',
          state: 'available',
          observedAt: 1794479106306.2878,
          windows: [
            {
              id: 'weekly',
              windowMinutes: 10080,
              resetAt: 1794729600000,
              remaining: 83,
              points: [{ remaining: 83, t: 1794479106306.2878 }],
            },
            {
              id: 'five-hour',
              windowMinutes: 300,
              resetAt: 1794484800000,
              remaining: 98,
              points: [{ remaining: 98, t: 1794479095544.2231 }],
            },
          ],
        },
        { id: 'other', windows: [{ windowMinutes: 60, remaining: 10 }] },
      ],
    },
  };

  it("reads each provider's windows from their last point, shortest first", () => {
    const quotas = parseCoordinatorProviders(snapshot);
    expect(Object.keys(quotas).sort()).toEqual(['claude', 'codex']);
    expect(quotas.claude).toEqual({
      at: 1794479106306.2878,
      windows: [
        { id: 'claude:300', minutes: 300, usedPercent: 2, resetsAt: 1794484800000 },
        { id: 'claude:10080', minutes: 10080, usedPercent: 17, resetsAt: 1794729600000 },
      ],
    });
    expect(quotas.codex!.windows).toEqual([
      { id: 'codex:10080', minutes: 10080, usedPercent: 99, resetsAt: 1794758099000 },
    ]);
  });

  it('gives Codex from the providers, and from the buckets of an older Coordinator', () => {
    expect(parseCoordinatorUsage(snapshot)).toEqual(parseCoordinatorProviders(snapshot).codex!.windows);
    const older = { ok: true, usage: { buckets: snapshot.usage.buckets } };
    expect(parseCoordinatorProviders(older)).toEqual({});
    expect(parseCoordinatorUsage(older)).toEqual([
      { id: 'codex:10080', minutes: 10080, usedPercent: 50, resetsAt: 1794758099000 },
    ]);
  });

  it("prefers reframe's own status line unless the Coordinator observed later", () => {
    const observed = parseCoordinatorProviders(snapshot).claude!;
    expect(coordinatorQuotaIsNewer(undefined, observed)).toBe(true);
    const captured = { source: 'claude' as const, windows: [], at: observed.at + 1 };
    expect(coordinatorQuotaIsNewer(captured, observed)).toBe(false);
    expect(coordinatorQuotaIsNewer({ ...captured, at: observed.at - 60_000 }, observed)).toBe(true);
    // The same observation again does not replace itself.
    expect(coordinatorQuotaIsNewer({ ...captured, at: observed.at }, observed)).toBe(false);
  });
});

describe('labels', () => {
  it('names windows, levels and time left', () => {
    expect([windowLabel(300), windowLabel(10080), windowLabel(90), windowLabel(null)]).toEqual(['5h', '7d', '90m', '']);
    expect([quotaLevel(10), quotaLevel(75), quotaLevel(95)]).toEqual(['ok', 'warn', 'danger']);
    expect(untilLabel(30_000, 0)).toBe('<1m');
    expect(untilLabel(12 * 60_000, 0)).toBe('12m');
    expect(untilLabel(130 * 60_000, 0)).toBe('2h 10m');
    expect(untilLabel((3 * 1440 + 240) * 60_000, 0)).toBe('3d 4h');
  });
});
