import { describe, expect, it } from 'vitest';
import { parseClaudeRateLimits, parseCoordinatorUsage, quotaLevel, untilLabel, windowLabel } from './quota';

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
