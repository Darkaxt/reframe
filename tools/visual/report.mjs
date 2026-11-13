// Visual parity report: pairs the Electron reference (e2e/visual-reference.spec.ts) and the WPF captures
// (reframe-shell --diag-capture) by state id (file name) and writes index.html with Electron | WPF | diff per state,
// plus summary.json. Score: the share of pixels whose perceptual colour difference (YIQ, as pixelmatch measures it)
// stays under the threshold, over the area both captures cover; a size mismatch is reported too. Pixel equality is
// not the goal (font rasterisation differs); the score flags regressions for review.
//
//   node tools/visual/report.mjs --electron <dir> --wpf <dir> --out <dir> [--threshold 0.1]
import fs from 'node:fs';
import path from 'node:path';
import { PNG } from 'pngjs';

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : fallback;
};
const electronDir = option('electron');
const wpfDir = option('wpf');
const outDir = option('out');
const threshold = Number(option('threshold', '0.1'));
if (!electronDir || !wpfDir || !outDir) {
  console.error('usage: report.mjs --electron <dir> --wpf <dir> --out <dir> [--threshold 0.1]');
  process.exit(2);
}

const MAX_DELTA = 35215; // the largest YIQ difference (black against white)
const pngs = (dir) =>
  fs.existsSync(dir)
    ? Object.fromEntries(
        fs
          .readdirSync(dir)
          .filter((f) => f.endsWith('.png'))
          .map((f) => [f.slice(0, -4), path.join(dir, f)]),
      )
    : {};
const readJson = (file) => (fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : null);

function yiqDelta(r1, g1, b1, r2, g2, b2) {
  const y = (r1 - r2) * 0.29889531 + (g1 - g2) * 0.58662247 + (b1 - b2) * 0.11448223;
  const i = (r1 - r2) * 0.59597799 - (g1 - g2) * 0.2741761 - (b1 - b2) * 0.32180189;
  const q = (r1 - r2) * 0.21147017 - (g1 - g2) * 0.52261711 + (b1 - b2) * 0.31114694;
  return 0.5053 * y * y + 0.299 * i * i + 0.1957 * q * q;
}

/** Compares over the common area; the diff image is the Electron capture greyed, differing pixels in red. */
function compare(electronFile, wpfFile) {
  const a = PNG.sync.read(fs.readFileSync(electronFile));
  const b = PNG.sync.read(fs.readFileSync(wpfFile));
  const width = Math.min(a.width, b.width);
  const height = Math.min(a.height, b.height);
  const diff = new PNG({ width, height });
  const limit = MAX_DELTA * threshold * threshold;
  let differing = 0;
  let total = 0;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const ia = (y * a.width + x) * 4;
      const ib = (y * b.width + x) * 4;
      const delta = yiqDelta(a.data[ia], a.data[ia + 1], a.data[ia + 2], b.data[ib], b.data[ib + 1], b.data[ib + 2]);
      total += delta;
      const o = (y * width + x) * 4;
      if (delta > limit) {
        differing++;
        diff.data.set([230, 40, 40, 255], o);
      } else {
        const grey = Math.round(255 - 0.15 * (255 - (a.data[ia] + a.data[ia + 1] + a.data[ia + 2]) / 3));
        diff.data.set([grey, grey, grey, 255], o);
      }
    }
  }
  const pixels = width * height || 1;
  return {
    diff,
    score: Number((1 - differing / pixels).toFixed(4)),
    meanDelta: Number((total / pixels / MAX_DELTA).toFixed(4)),
    sizes: { electron: [a.width, a.height], wpf: [b.width, b.height] },
    sameSize: a.width === b.width && a.height === b.height,
  };
}

const electron = pngs(electronDir);
const wpf = pngs(wpfDir);
const states = [...new Set([...Object.keys(electron), ...Object.keys(wpf)])].sort();
fs.mkdirSync(path.join(outDir, 'img'), { recursive: true });
const copy = (file, name) => {
  fs.copyFileSync(file, path.join(outDir, 'img', name));
  return `img/${name}`;
};

const rows = states.map((state) => {
  const row = { state, electron: null, wpf: null, diff: null, status: '' };
  if (electron[state]) row.electron = copy(electron[state], `${state}.electron.png`);
  if (wpf[state]) row.wpf = copy(wpf[state], `${state}.wpf.png`);
  if (electron[state] && wpf[state]) {
    const result = compare(electron[state], wpf[state]);
    fs.writeFileSync(path.join(outDir, 'img', `${state}.diff.png`), PNG.sync.write(result.diff));
    Object.assign(row, {
      diff: `img/${state}.diff.png`,
      status: 'compared',
      score: result.score,
      meanDelta: result.meanDelta,
      sizes: result.sizes,
      sameSize: result.sameSize,
    });
  } else row.status = electron[state] ? 'missing in WPF' : 'WPF only';
  return row;
});

const electronManifest = readJson(path.join(electronDir, 'manifest.json'));
const wpfManifest = readJson(path.join(wpfDir, 'capture.json'));
const summary = {
  threshold,
  electronCommit: electronManifest?.commit ?? null,
  compared: rows.filter((r) => r.status === 'compared').length,
  missingInWpf: rows.filter((r) => r.status === 'missing in WPF').map((r) => r.state),
  wpfOnly: rows.filter((r) => r.status === 'WPF only').map((r) => r.state),
  skippedInElectron: electronManifest?.skipped ?? {},
  wpfRuns: wpfManifest?.runs ?? [],
  rows,
};
fs.writeFileSync(path.join(outDir, 'summary.json'), JSON.stringify(summary, null, 2));

const esc = (text) =>
  String(text).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
const cell = (src, alt) =>
  src ? `<a href="${src}"><img src="${src}" alt="${esc(alt)}"></a>` : '<div class="missing">missing</div>';
const html = `<!doctype html>
<meta charset="utf-8">
<title>Reframe visual parity: Electron | WPF</title>
<style>
  body { font: 13px system-ui, 'Segoe UI', sans-serif; background: #1b1c20; color: #d7dae0; margin: 16px; }
  table { border-collapse: collapse; width: 100%; }
  th, td { border: 1px solid #30333b; padding: 6px; vertical-align: top; text-align: left; }
  td img { width: 100%; max-width: 480px; display: block; }
  .missing { padding: 40px 0; text-align: center; color: #8a8f98; border: 1px dashed #454a55; }
  .meta { color: #8a8f98; }
</style>
<h1>Reframe visual parity</h1>
<p class="meta">Electron commit ${esc(summary.electronCommit ?? 'unknown')} · compared ${summary.compared} ·
missing in WPF ${summary.missingInWpf.length} · threshold ${threshold}</p>
<table>
<tr><th>State</th><th>Electron</th><th>WPF</th><th>Diff</th></tr>
${rows
  .map(
    (r) =>
      `<tr><td><b>${esc(r.state)}</b><br><span class="meta">${esc(r.status)}${
        r.status === 'compared'
          ? `<br>score ${r.score}<br>mean Δ ${r.meanDelta}<br>${r.sizes.electron.join('×')} | ${r.sizes.wpf.join('×')}`
          : ''
      }</span></td><td>${cell(r.electron, 'Electron')}</td><td>${cell(r.wpf, 'WPF')}</td><td>${
        r.diff ? cell(r.diff, 'diff') : ''
      }</td></tr>`,
  )
  .join('\n')}
</table>
<h2>Not captured in Electron</h2>
<ul>${Object.entries(summary.skippedInElectron)
  .map(([state, why]) => `<li><b>${esc(state)}</b>: ${esc(why)}</li>`)
  .join('')}</ul>
`;
fs.writeFileSync(path.join(outDir, 'index.html'), html);
console.log(
  `report: ${summary.compared} compared, ${summary.missingInWpf.length} missing in WPF, ${summary.wpfOnly.length} WPF only -> ${outDir}`,
);
for (const r of rows.filter((r) => r.status === 'compared')) console.log(`  ${r.state}: score ${r.score}`);
