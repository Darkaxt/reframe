import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HostReads, type ReadHost } from './host-reads';

const temps: string[] = [];
afterEach(() => {
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

function claudeHome(settings?: unknown): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-reads-'));
  temps.push(dir);
  if (settings !== undefined) fs.writeFileSync(path.join(dir, 'settings.json'), JSON.stringify(settings));
  return dir;
}

function fakeHost(services: ReadHost['services'], answer: (service: string, method: string) => unknown) {
  const call = vi.fn(async (service: string, method: string) => {
    const result = answer(service, method);
    if (result instanceof Error) throw result;
    return result;
  });
  return { host: { services, call } as unknown as ReadHost, call };
}

describe('HostReads', () => {
  it('reads here, synchronously, when the host does not serve it (an older host)', async () => {
    const { host, call } = fakeHost({ transcripts: 1 }, () => null);
    const reads = new HostReads(() => host);
    expect(reads.userHasStatusLine(claudeHome({ statusLine: { type: 'command' } }))).toBe(true);
    expect(reads.userHasStatusLine(claudeHome())).toBe(false);
    expect(reads.adoptedClient(path.join(claudeHome(), 'none.json'), 'chatgpt')).toMatchObject({ ok: false });
    // No Coordinator under an empty LOCALAPPDATA: nothing to ask again.
    expect(await reads.codexQuota(claudeHome())).toEqual({ coordinator: false, windows: null });
    expect(call).not.toHaveBeenCalled();
  });

  it('asks the host when it serves it, and reads here when it cannot answer', async () => {
    let fail = false;
    const usage = { buckets: [{ id: 'codex', windowMinutes: 300, points: [{ t: 1000, remaining: 40 }] }] };
    const { host, call } = fakeHost({ quota: 1, clients: 1 }, (_service, method) => {
      if (fail) return new Error('gone');
      if (method === 'codex') return { coordinator: true, usage };
      if (method === 'statusLine') return true;
      return { ok: true, path: 'C:\\adopted\\claude.exe', version: '9' };
    });
    const reads = new HostReads(() => host);
    const home = claudeHome();
    expect(await reads.userHasStatusLine(home)).toBe(true);
    expect(call).toHaveBeenCalledWith('quota', 'statusLine', { claudeHome: home });
    expect(await reads.adoptedClient('C:\\record.json', 'anthropic')).toEqual({
      ok: true,
      path: 'C:\\adopted\\claude.exe',
      version: '9',
    });
    expect(call).toHaveBeenCalledWith('clients', 'adopted', { record: 'C:\\record.json', provider: 'anthropic' });
    const quota = await reads.codexQuota('C:\\local');
    expect(call).toHaveBeenCalledWith('quota', 'codex', { localAppData: 'C:\\local' });
    expect(quota).toMatchObject({ coordinator: true, windows: [{ id: 'codex:300', minutes: 300, usedPercent: 60 }] });

    fail = true;
    expect(await reads.userHasStatusLine(home)).toBe(false);
    expect(await reads.codexQuota(home)).toEqual({ coordinator: false, windows: null });
    expect(await reads.adoptedClient(path.join(home, 'none.json'), 'chatgpt')).toMatchObject({ ok: false });
  });
});
