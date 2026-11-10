// Verify the actual packaged payload before it can become a candidate. No source/history is allowed in app.asar.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { createHash } from 'node:crypto';
import { URL } from 'node:url';
const require = createRequire(import.meta.url);
const asar = require('@electron/asar');
const directory = path.resolve(process.argv[2]);
const archive = path.join(directory, 'resources', 'app.asar');
const names = asar.listPackage(archive).map((n) => n.replace(/\\/g, '/').replace(/^\//, ''));
const allowed = new Set(['out', 'node_modules', 'package.json', 'docs', 'LICENSE', 'README.md']);
const unexpected = names.filter(
  (n) => !allowed.has(n.split('/')[0]) || (n.startsWith('docs/') && n !== 'docs/provider-icons.md'),
);
if (unexpected.length) throw Error('Unexpected packaged entries: ' + unexpected.slice(0, 20).join(', '));
const info = JSON.parse(asar.extractFile(archive, 'package.json').toString());
const expected = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
if (info.version !== expected.version || info.repository.url !== 'https://github.com/Darkaxt/reframe.git')
  throw Error('Wrong package version/fork provenance');
const backend = fs.readFileSync(path.join(directory, 'resources', 'history_backend.py'));
if (!backend.equals(fs.readFileSync(new URL('./history_backend.py', import.meta.url))))
  throw Error('Packaged history backend differs from qualified source');
const main = asar.extractFile(archive, path.join('out', 'main', 'index.js')).toString();
for (const marker of ['https://api.anthropic.com', 'reframe:restore-archive', 'Model provider', 'Archive chat'])
  if (!main.includes(marker)) throw Error('Missing packaged feature: ' + marker);
const hash = (data) => createHash('sha256').update(data).digest('hex');
process.stdout.write(
  JSON.stringify({
    version: info.version,
    entries: names.length,
    asarBytes: fs.statSync(archive).size,
    asarSha256: hash(fs.readFileSync(archive)),
    backendSha256: hash(backend),
    unexpected: [],
  }) + '\n',
);
