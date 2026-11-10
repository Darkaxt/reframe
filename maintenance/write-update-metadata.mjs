// Portable release metadata for the existing electron-updater GitHub provider.
// This does not download or install an update; portable runtime checks are notification-only.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import process from 'node:process';
import { URL } from 'node:url';

const [archive, output] = process.argv.slice(2);
if (!archive || !output) throw Error('Supply the completed portable ZIP and metadata directory');
const { version } = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
if (!/^\d+\.\d+\.\d+-claudegpt\.\d+$/.test(version)) throw Error('Expected a managed ClaudeGPT release version');
const name = `reframe-${version}-win-portable.zip`;
if (path.basename(archive) !== name) throw Error('Portable archive/version mismatch');
const sha512 = createHash('sha512');
for await (const chunk of fs.createReadStream(archive)) sha512.update(chunk);
const info = {
  version,
  files: [{ url: name, sha512: sha512.digest('base64'), size: fs.statSync(archive).size }],
  releaseDate: new Date().toISOString(),
};
fs.mkdirSync(output, { recursive: true });
// JSON is valid YAML. Custom prerelease channels use claudegpt.yml; stable clients use latest.yml.
for (const file of ['claudegpt.yml', 'latest.yml'])
  fs.writeFileSync(path.join(output, file), JSON.stringify(info, null, 2) + '\n', { flag: 'wx' });
process.stdout.write(JSON.stringify({ tag: `v${version}`, ...info }) + '\n');
