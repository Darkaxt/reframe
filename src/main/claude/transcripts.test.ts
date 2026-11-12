import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Transcripts, type TranscriptHost } from './transcripts';

const temps: string[] = [];
afterEach(() => {
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

function home(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-transcripts-'));
  temps.push(dir);
  fs.mkdirSync(path.join(dir, 'projects', 'p'), { recursive: true });
  const lines = [
    JSON.stringify({ type: 'user', cwd: 'C:\\here', message: { content: 'read here' } }),
    JSON.stringify({ type: 'ai-title', aiTitle: 'local title' }),
  ];
  fs.writeFileSync(path.join(dir, 'projects', 'p', 's.jsonl'), lines.join('\n'));
  return dir;
}

function fakeHost(services: TranscriptHost['services'], answer: (method: string) => unknown) {
  const emitter = new EventEmitter();
  const call = vi.fn(async (_service: string, method: string) => {
    const result = answer(method);
    if (result instanceof Error) throw result;
    return result;
  });
  const host = { services, call, on: emitter.on.bind(emitter) } as unknown as TranscriptHost;
  return { host, call, emitter };
}

describe('Transcripts', () => {
  it('reads here when the host does not serve transcripts (an older host)', async () => {
    const dir = home();
    const { host, call } = fakeHost({}, () => null);
    const transcripts = new Transcripts(dir, () => host);
    const listed = await transcripts.list({ limit: 10, exclude: new Set() });
    expect(listed.items.map((i) => i.title)).toEqual(['local title']);
    expect(await transcripts.readTitle('s')).toBe('local title');
    expect(transcripts.cachedTitle('s')).toBe('local title');
    expect(call).not.toHaveBeenCalled();
  });

  it('asks the host when it serves them, and reads here when it cannot answer', async () => {
    const dir = home();
    let fail = false;
    const { host, call } = fakeHost({ transcripts: 1 }, (method) => {
      if (fail) return new Error('gone');
      if (method === 'title') return 'host title';
      if (method === 'times') return { s: 5 };
      return { items: [], hasMore: false, folders: [{ name: 'b' }, { name: 'a' }] };
    });
    const transcripts = new Transcripts(dir, () => host);
    expect(await transcripts.readTitle('s')).toBe('host title');
    expect(call).toHaveBeenCalledWith('transcripts', 'title', { home: dir, sessionId: 's' });
    expect((await transcripts.modifiedTimes()).get('s')).toBe(5);
    const listed = await transcripts.list({ limit: 3, exclude: new Set(['x']), query: 'q' });
    expect(listed.folders.map((f) => f.name)).toEqual(['a', 'b']);
    expect(call).toHaveBeenLastCalledWith('transcripts', 'list', {
      home: dir,
      limit: 3,
      exclude: ['x'],
      query: 'q',
      folder: undefined,
    });
    fail = true;
    expect(await transcripts.readTitle('s')).toBe('local title');
    expect((await transcripts.list({ limit: 10, exclude: new Set() })).items).toHaveLength(1);
  });

  it('lets the host watch, and tells its changes and every attach', async () => {
    const dir = home();
    const { host, call, emitter } = fakeHost({ transcripts: 1 }, () => null);
    const transcripts = new Transcripts(dir, () => host);
    const changed = vi.fn();
    transcripts.watch(changed);
    expect(call).toHaveBeenCalledWith('transcripts', 'watch', { home: dir });
    emitter.emit('svc-event', { service: 'transcripts', event: 'changed', data: { home: dir, projects: ['p'] } });
    emitter.emit('svc-event', { service: 'transcripts', event: 'changed', data: { home: 'other', projects: 'all' } });
    expect(changed.mock.calls).toEqual([[new Set(['p'])]]);
    emitter.emit('attached');
    expect(changed).toHaveBeenLastCalledWith('all');
    transcripts.stop();
  });
});
