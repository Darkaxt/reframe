// Shared spawn plan fixtures (test/spawn-plan-fixtures): what spawnPlan makes of each input with its made-up disk,
// committed as JSON so that the Rust host's port (rust/reframe-services/src/spawn_plan.rs) is checked against the
// same results in its own tests. Fails when a committed result is stale; REFRAME_UPDATE_SPAWN_FIXTURES=1 writes them
// again. The inputs hold placeholders only (<user>, <hook-token>, <api-key>): no real secret or user path.
import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { spawnPlan, type PlanFs, type SpawnPlanInput } from './spawn-plan';

const root = path.resolve(__dirname, '../../../test/spawn-plan-fixtures');
const inputs = path.join(root, 'inputs');
const expected = path.join(root, 'expected');
const update = process.env.REFRAME_UPDATE_SPAWN_FIXTURES === '1';

/** A fixture's disk: files (null: there but unreadable), folders and the transcripts there are ([home, id]). */
interface FixtureFs {
  files: Record<string, string | null>;
  dirs: string[];
  transcripts: [string, string][];
}

function fixtureFs(disk: FixtureFs): PlanFs {
  return {
    isFile: (p) => Object.hasOwn(disk.files, p),
    readFile: (p) => {
      const text = disk.files[p];
      if (typeof text !== 'string') throw new Error(`cannot read ${p}`);
      return text;
    },
    exists: (p) => Object.hasOwn(disk.files, p) || disk.dirs.includes(p),
    isDir: (p) => disk.dirs.includes(p),
    transcriptExists: (home, id) => disk.transcripts.some(([h, i]) => h === home && i === id),
  };
}

function planned(text: string): string {
  const { input, fs: disk } = JSON.parse(text) as { input: SpawnPlanInput; fs: FixtureFs };
  return JSON.stringify(spawnPlan(input, fixtureFs(disk)), null, 2) + '\n';
}

const names = fs.readdirSync(inputs).filter((name) => name.endsWith('.json'));
if (update) {
  fs.rmSync(expected, { recursive: true, force: true });
  fs.mkdirSync(expected, { recursive: true });
  for (const name of names)
    fs.writeFileSync(path.join(expected, name), planned(fs.readFileSync(path.join(inputs, name), 'utf8')));
}

describe('shared spawn plan fixtures', () => {
  it('has inputs, and a result for each and no other', () => {
    expect(names.length).toBeGreaterThan(30);
    expect(fs.readdirSync(expected).sort()).toEqual([...names].sort());
  });

  it('holds no secret of the machine it was made on', () => {
    for (const dir of [inputs, expected])
      for (const name of fs.readdirSync(dir)) {
        const text = fs.readFileSync(path.join(dir, name), 'utf8');
        expect(text).not.toMatch(/\\\\Users\\\\(?!<user>)|\/home\/(?!<user>)|sk-ant-|credentials\.json/);
      }
  });

  it.each(names)('%s matches the committed result', (name) => {
    const actual = planned(fs.readFileSync(path.join(inputs, name), 'utf8'));
    expect(
      actual,
      'stale: run REFRAME_UPDATE_SPAWN_FIXTURES=1 vitest run src/main/claude/spawn-plan.fixtures.test.ts',
    ).toBe(fs.readFileSync(path.join(expected, name), 'utf8'));
  });
});
