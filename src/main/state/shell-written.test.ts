// What the WPF shell writes to state.json (test/state-fixtures/shell-written, made by
// wpf/Reframe.Shell.Tests/StateWriteTests.cs from its actions: rename, pin, hide, restore, end, remove): reframe's own
// parser must accept every file, and make of it what the shell's model says (parsed/, the state without the keys the
// schema does not know).
import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { parse } from './store';

const root = path.resolve(__dirname, '../../../test/state-fixtures/shell-written');
const names = fs.readdirSync(path.join(root, 'written')).filter((name) => name.endsWith('.json'));

describe('state files written by the WPF shell', () => {
  it('has them', () => {
    expect(names.length).toBeGreaterThanOrEqual(10);
    expect(fs.readdirSync(path.join(root, 'parsed')).sort()).toEqual([...names].sort());
  });

  it.each(names)('%s is accepted as the shell means it', (name) => {
    const result = parse(fs.readFileSync(path.join(root, 'written', name), 'utf8'));
    expect(result.reason).toBeUndefined();
    expect(result.state).toBeDefined();
    expect(result.state).toEqual(JSON.parse(fs.readFileSync(path.join(root, 'parsed', name), 'utf8')));
  });
});
