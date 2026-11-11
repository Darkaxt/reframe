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
const runtimeAssets = ['resources/icon.png', 'resources/tray.png', 'resources/tray@2x.png'];
const allowed = new Set(['out', 'node_modules', 'package.json', 'docs', 'resources', 'LICENSE', 'README.md']);
const unexpected = names.filter(
  (n) =>
    !allowed.has(n.split('/')[0]) ||
    (n.startsWith('docs/') && n !== 'docs/provider-icons.md') ||
    (n.startsWith('resources/') && !runtimeAssets.includes(n)),
);
if (unexpected.length) throw Error('Unexpected packaged entries: ' + unexpected.slice(0, 20).join(', '));
const info = JSON.parse(asar.extractFile(archive, 'package.json').toString());
const expected = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
if (info.version !== expected.version || info.repository.url !== 'https://github.com/Darkaxt/reframe.git')
  throw Error('Wrong package version/fork provenance');
for (const asset of runtimeAssets) {
  const nativePath = path.join(...asset.split('/'));
  if (!asar.extractFile(archive, nativePath).equals(fs.readFileSync(new URL('../' + asset, import.meta.url))))
    throw Error('Packaged runtime image differs from source: ' + asset);
}
const backend = fs.readFileSync(path.join(directory, 'resources', 'history_backend.py'));
for (const name of ['stop-managed.ps1', 'release-files.ps1'])
  if (
    !fs
      .readFileSync(path.join(directory, 'resources', 'maintenance', 'windows', name))
      .equals(fs.readFileSync(new URL('./windows/' + name, import.meta.url)))
  )
    throw Error('Packaged quit helper differs from reviewed source: ' + name);
// The Rust PTY host and the ConPTY it runs with, as built by CI and shipped by node-pty.
const hostDir = path.join(directory, 'resources', 'reframe-host');
for (const [name, source] of [
  ['reframe-host.exe', '../rust/dist/reframe-host.exe'],
  ['conpty.dll', '../node_modules/node-pty/prebuilds/win32-x64/conpty/conpty.dll'],
  ['OpenConsole.exe', '../node_modules/node-pty/prebuilds/win32-x64/conpty/OpenConsole.exe'],
])
  if (
    !fs.existsSync(path.join(hostDir, name)) ||
    !fs.readFileSync(path.join(hostDir, name)).equals(fs.readFileSync(new URL(source, import.meta.url)))
  )
    throw Error('Packaged Rust PTY host file missing or different: ' + name);
if (!backend.equals(fs.readFileSync(new URL('./history_backend.py', import.meta.url))))
  throw Error('Packaged history backend differs from qualified source');
// The main process, the PTY host and the chunks they share (built together, so a feature may sit in a chunk).
const mainFiles = names.filter((name) => /^out\/main\/.+\.js$/.test(name));
if (!mainFiles.includes('out/main/index.js')) throw Error('Packaged main is missing');
for (const name of mainFiles)
  if (
    !asar
      .extractFile(archive, path.join(...name.split('/')))
      .equals(fs.readFileSync(new URL('../' + name, import.meta.url)))
  )
    throw Error('Packaged main differs from the verified build: ' + name);
const main = mainFiles.map((name) => asar.extractFile(archive, path.join(...name.split('/'))).toString()).join('\n');
if (
  !asar
    .extractFile(archive, path.join('out', 'preload', 'index.js'))
    .equals(fs.readFileSync(new URL('../out/preload/index.js', import.meta.url)))
)
  throw Error('Packaged preload differs from the verified build');
for (const name of names.filter((entry) => /^out\/renderer\/.+\.(js|css|html)$/.test(entry)))
  if (
    !asar
      .extractFile(archive, path.join(...name.split('/')))
      .equals(fs.readFileSync(new URL('../' + name, import.meta.url)))
  )
    throw Error('Packaged renderer differs from the verified build: ' + name);
for (const marker of [
  'https://api.anthropic.com',
  'reframe:restore-archive',
  'Model provider',
  'Archive chat',
  'reframe:set-project-expanded',
  'Pin chat',
  'fs.writeSync(process.stderr.fd, line)',
  'REFRAME_ANTHROPIC_CLAUDE_BIN',
  '--quit',
  'Archive selected',
  'Delete selected',
  'reframe:change-chat-models',
])
  if (!main.includes(marker)) throw Error('Missing packaged feature: ' + marker);
const renderer = names
  .filter((name) => /^out\/renderer\/assets\/.*\.js$/.test(name))
  .map((name) => asar.extractFile(archive, path.join(...name.split('/'))).toString())
  .join('\n');
for (const marker of [
  'sidebar-pinned',
  'sidebar-project-chats',
  'sidebar-recents',
  'historical provider not recorded',
  'Selected chat actions',
  'Apply to selected chats',
  'Selected chat results',
])
  if (!renderer.includes(marker)) throw Error('Missing packaged sidebar feature: ' + marker);
const hash = (data) => createHash('sha256').update(data).digest('hex');
process.stdout.write(
  JSON.stringify({
    version: info.version,
    entries: names.length,
    asarBytes: fs.statSync(archive).size,
    asarSha256: hash(fs.readFileSync(archive)),
    backendSha256: hash(backend),
    runtimeAssets,
    unexpected: [],
  }) + '\n',
);
