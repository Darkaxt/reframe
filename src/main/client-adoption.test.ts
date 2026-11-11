import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { adoptedClient } from './client-adoption';

const temps: string[] = [];
afterEach(() => {
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

function record(clients: Record<string, string>) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-adoption-'));
  temps.push(dir);
  const bindings: Record<string, { binary: string; sha256: string }> = {};
  for (const [provider, content] of Object.entries(clients)) {
    const binary = path.join(dir, `${provider}.exe`);
    fs.writeFileSync(binary, content);
    bindings[provider] = { binary, sha256: crypto.createHash('sha256').update(content).digest('hex') };
  }
  const file = path.join(dir, 'client-adoption.json');
  fs.writeFileSync(
    file,
    JSON.stringify({ schema: 'reframe.client-adoption.v1', version: '2.1.322', clients: bindings }),
  );
  return { file, bindings };
}

describe('adoptedClient', () => {
  it('returns the provider’s adopted client when its hash matches', () => {
    const { file, bindings } = record({ chatgpt: 'a', anthropic: 'b' });
    expect(adoptedClient(file, 'chatgpt')).toEqual({ ok: true, path: bindings.chatgpt!.binary, version: '2.1.322' });
    expect(adoptedClient(file, 'anthropic')).toEqual({
      ok: true,
      path: bindings.anthropic!.binary,
      version: '2.1.322',
    });
  });

  it('refuses a changed binary, a missing provider and a foreign or unreadable record', () => {
    const { file, bindings } = record({ chatgpt: 'a' });
    expect(adoptedClient(file, 'anthropic')).toMatchObject({ ok: false, reason: expect.stringMatching(/no adopted/) });
    fs.writeFileSync(bindings.chatgpt!.binary, 'changed, other size');
    expect(adoptedClient(file, 'chatgpt')).toMatchObject({ ok: false, reason: expect.stringMatching(/changed/) });
    fs.writeFileSync(file, JSON.stringify({ schema: 'other' }));
    expect(adoptedClient(file, 'chatgpt')).toMatchObject({ ok: false, reason: expect.stringMatching(/not a client/) });
    expect(adoptedClient(path.join(path.dirname(file), 'missing.json'), 'chatgpt').ok).toBe(false);
  });
});

// The PowerShell rules (candidate qualification, selection and garbage collection) on Windows, where they run.
const pwsh = process.platform === 'win32' && spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0;
describe.skipIf(!pwsh)('client adoption rules (PowerShell)', () => {
  it('passes test-client-adoption.ps1', () => {
    const script = path.resolve(__dirname, '../../maintenance/windows/test-client-adoption.ps1');
    const run = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', script], { encoding: 'utf8' });
    expect(run.stdout + run.stderr).toMatch(/client adoption rules: all passed/);
    expect(run.status).toBe(0);
  });
});
