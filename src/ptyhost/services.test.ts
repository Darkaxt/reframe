import { describe, expect, it } from 'vitest';
import { Limit, SvcError } from './services';

describe('Limit', () => {
  it('queues a burst in order behind those running, and refuses beyond the bound', async () => {
    const limit = new Limit(2, 3);
    const releases: (() => void)[] = [];
    const started: number[] = [];
    const runs = [0, 1, 2, 3, 4].map((n) =>
      limit.run('test', async () => {
        started.push(n);
        await new Promise<void>((resolve) => releases.push(resolve));
        return n;
      }),
    );
    await expect(limit.run('test', async () => 9)).rejects.toEqual(
      new SvcError('busy', 'too many test requests at once'),
    );
    await Promise.resolve();
    expect(started).toEqual([0, 1]);
    while (started.length < 5 || releases.length) {
      releases.shift()?.();
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
    expect(await Promise.all(runs)).toEqual([0, 1, 2, 3, 4]);
    expect(started).toEqual([0, 1, 2, 3, 4]);
    expect(await limit.run('test', async () => 'room again')).toBe('room again');
  });
});
