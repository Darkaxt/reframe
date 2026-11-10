import { describe, expect, it } from 'vitest';
import type { SessionRecord, WindowState } from '@shared/state';
import { staleTiles } from './stale-tiles';

const record = (id: string, used: boolean): SessionRecord => ({
  id,
  claudeSessionId: `c-${id}`,
  cwd: 'D:\\p',
  launch: {},
  lifecycle: 'ended',
  used,
  createdAt: 0,
  lastActiveAt: 0,
});

const win: WindowState = {
  id: 'w',
  bounds: null,
  maximized: false,
  hidden: false,
  layout: {
    kind: 'split',
    id: 's',
    dir: 'row',
    sizes: [50, 50],
    children: [
      { kind: 'tile', sessionId: 'kept' },
      { kind: 'tile', sessionId: 'lost' },
    ],
  },
  dock: [
    { kind: 'tile', sessionId: 'lostDocked', hint: null },
    { kind: 'tile', sessionId: 'unused', hint: null },
    { kind: 'tile', sessionId: 'live', hint: null },
  ],
};

describe('staleTiles', () => {
  it('lists used, stopped tiles (also collapsed ones) whose transcript is gone', () => {
    const sessions = Object.fromEntries(
      ['kept', 'lost', 'lostDocked', 'live']
        .map((id) => [id, record(id, true)])
        .concat([['unused', record('unused', false)]]),
    );
    const has = (r: SessionRecord) => r.id === 'kept';
    expect(staleTiles(win, sessions, has, (id) => id === 'live')).toEqual(['lost', 'lostDocked']);
  });

  it('ignores tiles without a record', () => {
    expect(
      staleTiles(
        win,
        {},
        () => false,
        () => false,
      ),
    ).toEqual([]);
  });
});
