import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionStatusService, type SessionStatusData } from './sessions';

// The Node host's status rule for background work (tool shells left running when a turn ends), with a stand-in for
// the process listing. The registry of a folder that is not there is never found, so only hooks count here.
describe('SessionStatusService and background work', () => {
  let events: SessionStatusData[];
  let shells: boolean;
  let asked: number[];
  let service: SessionStatusService;
  const hook = (event: string, payload: Record<string, unknown> = {}) =>
    service.hook({ sessionId: 's', event, payload } as never);
  const last = () => events.at(-1)!;

  beforeEach(() => {
    vi.useFakeTimers();
    events = [];
    shells = false;
    asked = [];
    service = new SessionStatusService(
      (data) => events.push(data),
      () => true,
      (pid) => {
        asked.push(pid);
        return shells;
      },
    );
    service.spawned('s', 1, 4321, { claudeHome: 'Z:\\nowhere\\.claude' });
  });
  afterEach(() => vi.useRealTimers());

  it('announces done at a Stop without tool shells', () => {
    hook('UserPromptSubmit');
    hook('Stop');
    expect(last()).toMatchObject({ status: 'idle', notify: 'done' });
    expect(last().background).toBeUndefined();
    expect(asked).toEqual([4321]);
  });

  it('waits with background work until the last shell is gone', () => {
    hook('UserPromptSubmit');
    shells = true;
    hook('Stop');
    expect(last()).toMatchObject({ status: 'idle', background: true });
    expect(last().notify).toBeUndefined();
    expect(service.list().sessions[0]).toMatchObject({ background: true });
    const count = events.length;
    vi.advanceTimersByTime(25_000);
    expect(events.length).toBe(count);
    shells = false;
    vi.advanceTimersByTime(21_000);
    expect(last()).toMatchObject({ status: 'idle', notify: 'done' });
    expect(last().background).toBeUndefined();
    // Looked at only while waiting.
    const looks = asked.length;
    vi.advanceTimersByTime(60_000);
    expect(asked.length).toBe(looks);
  });

  it('a new turn or a question ends the wait', () => {
    shells = true;
    hook('Stop');
    hook('UserPromptSubmit');
    expect(last()).toMatchObject({ status: 'busy' });
    expect(last().background).toBeUndefined();
    hook('Stop');
    hook('Notification', { notification_type: 'permission_prompt' });
    expect(last()).toMatchObject({ status: 'waiting', notify: 'waiting' });
    shells = false;
    vi.advanceTimersByTime(30_000);
    expect(events.filter((e) => e.notify === 'done')).toEqual([]);
  });
});
