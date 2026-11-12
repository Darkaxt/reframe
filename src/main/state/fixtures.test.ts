// Shared state fixtures (test/state-fixtures): what this parser makes of each input file, committed as JSON so the
// WPF shell's port of the schema (wpf/Reframe.Shell.Core/AppState.cs) is checked against the same results in
// its own tests. Fails when a committed result is stale; REFRAME_UPDATE_STATE_FIXTURES=1 writes them again.
import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { parse } from './store';

const root = path.resolve(__dirname, '../../../test/state-fixtures');
const inputs = path.join(root, 'inputs');
const expected = path.join(root, 'expected');
const update = process.env.REFRAME_UPDATE_STATE_FIXTURES === '1';

/** The parse result in the shared form: the normalised state, or "newer", or "rejected" (reasons are not compared). */
function normalised(text: string): string {
  const result = parse(text);
  const shared = result.state
    ? { result: 'state', state: result.state }
    : result.newer
      ? { result: 'newer' }
      : { result: 'rejected' };
  return JSON.stringify(shared, null, 2) + '\n';
}

const names = fs.readdirSync(inputs).filter((name) => name.endsWith('.json'));
if (update) {
  fs.rmSync(expected, { recursive: true, force: true });
  fs.mkdirSync(expected, { recursive: true });
  for (const name of names)
    fs.writeFileSync(path.join(expected, name), normalised(fs.readFileSync(path.join(inputs, name), 'utf8')));
}

describe('shared state fixtures', () => {
  it('has inputs, and a result for each and no other', () => {
    expect(names.length).toBeGreaterThan(10);
    expect(fs.readdirSync(expected).sort()).toEqual([...names].sort());
  });

  it.each(names)('%s matches the committed result', (name) => {
    const actual = normalised(fs.readFileSync(path.join(inputs, name), 'utf8'));
    const file = path.join(expected, name);
    expect(actual, `stale: run REFRAME_UPDATE_STATE_FIXTURES=1 vitest run src/main/state/fixtures.test.ts`).toBe(
      fs.readFileSync(file, 'utf8'),
    );
  });
});
