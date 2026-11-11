import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { savedGpuAcceleration } from './gpu';

describe('saved GPU acceleration', () => {
  const dirs: string[] = [];
  const file = (content?: string) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-gpu-'));
    dirs.push(dir);
    const name = path.join(dir, 'state.json');
    if (content !== undefined) fs.writeFileSync(name, content);
    return name;
  };
  afterEach(() => {
    for (const dir of dirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
  });

  it('is off without a state file, with an unreadable one, and for older files without the setting', () => {
    expect(savedGpuAcceleration(file())).toBe(false);
    expect(savedGpuAcceleration(file('{not json'))).toBe(false);
    expect(savedGpuAcceleration(file('null'))).toBe(false);
    expect(savedGpuAcceleration(file(JSON.stringify({ preferences: { snapToGrid: true } })))).toBe(false);
    expect(savedGpuAcceleration(file(JSON.stringify({ preferences: { gpuAcceleration: 'yes' } })))).toBe(false);
  });

  it('is on only when chosen', () => {
    expect(savedGpuAcceleration(file(JSON.stringify({ preferences: { gpuAcceleration: true } })))).toBe(true);
    expect(savedGpuAcceleration(file(JSON.stringify({ preferences: { gpuAcceleration: false } })))).toBe(false);
  });
});
