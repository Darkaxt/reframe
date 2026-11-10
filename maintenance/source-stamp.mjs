// Content fingerprint for the integrated source readiness/verification record. Never includes histories or credentials.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
const root = path.resolve(import.meta.dirname, '..');
const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0');
const extra = execFileSync('git', ['ls-files', '--others', '--exclude-standard', '-z'], {
  cwd: root,
  encoding: 'utf8',
}).split('\0');
const hash = createHash('sha256');
for (const name of [...new Set([...tracked, ...extra])]
  .filter(Boolean)
  .filter((n) => !n.startsWith('docs/') && !n.startsWith('.github/'))
  .sort()) {
  const file = path.join(root, name);
  if (!fs.existsSync(file)) continue;
  hash.update(name + '\0');
  hash.update(fs.readFileSync(file));
  hash.update('\0');
}
process.stdout.write(
  JSON.stringify({
    schema: 'reframe.source-stamp.v1',
    sha256: hash.digest('hex'),
    head: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim(),
  }) + '\n',
);
