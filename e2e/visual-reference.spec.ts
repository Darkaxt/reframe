// Visual parity, Electron side: the reference set the WPF shell is compared against (docs: tools/visual/report.mjs).
// Each state is a fixed fixture (demo sessions drawn by screenshots/demo-claude.mjs, whose scenes also set their
// status) rendered at 1280×800 CSS px at scale 1, saved as <state id>.png; manifest.json lists the states with the
// source commit, and the ones Electron cannot show here. Not part of `npx playwright test` (playwright.config.ts
// ignores it); CI runs it with playwright.visual.config.ts and keeps the folder as the artifact `visual-reference`.
import { execFileSync } from 'node:child_process';
import { spawn, type ChildProcess } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type ElectronApplication, type Locator, type Page } from '@playwright/test';
import type { ForeignDropRequest } from '../src/shared/ipc';
import type { GroupNode, LayoutNode } from '../src/shared/layout';
import type { ProviderQuota } from '../src/shared/quota';
import { chatKey } from '../src/shared/sidebar-chats';
import { DEFAULT_PREFERENCES, type AppState, type SessionRecord, type WindowState } from '../src/shared/state';
import { callMain, cleanup, launch, root, tileByLabel } from './helpers';

const out = process.env.REFRAME_VISUAL_OUT ?? path.join(root, 'test-results', 'visual-reference');
const SIZE = { width: 1280, height: 800 };
const HOUR = 3600_000;
const BLUE = '#5b9cf0';

/** States that need what this runner does not have, or that are not in the page (native). */
const SKIPPED: Record<string, string> = {
  'sidebar-row-background': 'idle with background work needs real tool shells under a hooked claude',
  'sidebar-row-wsl-running': 'needs a real WSL distribution (fake WSL lists none as running)',
  'sidebar-row-wsl-stopped': 'needs a real WSL distribution',
  'new-session-wsl-folder': 'needs a real WSL distribution',
  'history-results': 'bulk results run maintenance/history_backend.py (pinned Python helper)',
  'context-menus': 'native Electron menus (menus.ts): not in the page; to be specified as structured item lists',
  'tray-menu': 'native tray menu: not in the page',
  toasts: 'Windows toasts: not in the page',
  'dpi-150': 'only 100% here: the runner has one display scale',
};

const captured: { state: string; file: string; width: number; height: number; component?: boolean }[] = [];

/** A throwaway home with demo projects, Claude Code's files for them, and reframe's profile (as screenshots/). */
class Fixture {
  readonly dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-visual-'));
  readonly home = path.join(this.dir, 'home');
  private claudeHome = path.join(this.dir, 'claude-home');
  private userData = path.join(this.dir, 'user-data');
  private inApp: SessionRecord[] = [];
  private processes: ChildProcess[] = [];
  extra: Partial<AppState> = {};

  readonly env: Record<string, string> = {
    ...(process.env as Record<string, string>),
    REFRAME_E2E: '1',
    REFRAME_USER_DATA: this.userData,
    REFRAME_CLAUDE_HOME: this.claudeHome,
    REFRAME_CLAUDE_BIN: path.join(root, 'screenshots', 'demo-claude.mjs'),
    REFRAME_NODE: process.execPath,
    REFRAME_LOCALE: 'en',
    REFRAME_DISABLE_NOTIFICATIONS: '1',
    REFRAME_QUIT_ENDS_SESSIONS: '1',
    REFRAME_PTYHOST_ROOT: path.join(os.tmpdir(), 'reframe-e2e-ptyhost'),
    REFRAME_WSL_EXE: path.join(root, 'test', 'fake-wsl', 'fake-wsl.mjs'),
    REFRAME_WSL_SHARE: path.join(this.dir, 'wsl'),
    USERPROFILE: this.home,
    HOME: this.home,
  };

  constructor() {
    fs.mkdirSync(this.env.REFRAME_WSL_SHARE!, { recursive: true });
  }

  folder(project: string): string {
    const folder = path.join(this.home, 'projects', project);
    fs.mkdirSync(folder, { recursive: true });
    return folder;
  }

  private transcript(claudeSessionId: string, cwd: string, prompt: string, modifiedAt: number): void {
    const file = path.join(this.claudeHome, 'projects', path.basename(cwd), `${claudeSessionId}.jsonl`);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ type: 'user', cwd, message: { content: prompt } }) + '\n');
    fs.utimesSync(file, new Date(modifiedAt), new Date(modifiedAt));
  }

  /** A session run in reframe (demo-claude draws the scene of its name, with its status). */
  session(name: string, project: string, options: { ended?: boolean; missingFolder?: boolean } = {}): SessionRecord {
    const cwd = options.missingFolder ? path.join(this.home, 'projects', 'moved-away') : this.folder(project);
    const record: SessionRecord = {
      id: crypto.randomUUID(),
      claudeSessionId: crypto.randomUUID(),
      cwd,
      name,
      launch: {},
      lifecycle: options.ended ? 'ended' : 'open',
      createdAt: Date.now() - 5 * HOUR,
      lastActiveAt: Date.now() - (options.ended ? 2 * HOUR : 0),
      used: true,
    };
    if (!options.missingFolder) this.transcript(record.claudeSessionId, cwd, name, Date.now() - HOUR);
    this.inApp.push(record);
    return record;
  }

  /** A past chat (a transcript only), listed among the recent ones; returns its id. */
  past(title: string, project: string, hoursAgo: number): string {
    const id = crypto.randomUUID();
    this.transcript(id, this.folder(project), title, Date.now() - hoursAgo * HOUR);
    return id;
  }

  /** A claude running in another terminal ("found"), as Claude Code registers it. */
  external(name: string, project: string): void {
    const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1e6)'], { stdio: 'ignore' });
    this.processes.push(child);
    const entry = {
      pid: child.pid,
      sessionId: crypto.randomUUID(),
      cwd: this.folder(project),
      name,
      nameSource: 'user',
      status: 'busy',
    };
    fs.mkdirSync(path.join(this.claudeHome, 'sessions'), { recursive: true });
    fs.writeFileSync(path.join(this.claudeHome, 'sessions', `${child.pid}.json`), JSON.stringify(entry));
  }

  write(windows: WindowState[]): void {
    const state: AppState = {
      version: 1,
      sessions: Object.fromEntries(this.inApp.map((record) => [record.id, record])),
      windows,
      recentFolders: [],
      preferences: { ...DEFAULT_PREFERENCES, language: 'en' },
      hiddenThreads: [],
      ...this.extra,
    };
    fs.mkdirSync(this.userData, { recursive: true });
    fs.writeFileSync(path.join(this.userData, 'state.json'), JSON.stringify(state, null, 2));
  }

  async close(): Promise<void> {
    for (const child of this.processes) child.kill();
    await cleanup(this.dir);
  }
}

const tile = (record: SessionRecord): LayoutNode => ({ kind: 'tile', sessionId: record.id });
const split = (dir: 'row' | 'col', children: LayoutNode[]): LayoutNode => ({
  kind: 'split',
  id: crypto.randomUUID(),
  dir,
  children,
  sizes: children.map(() => 100 / children.length),
});
const group = (name: string, color: string, child: LayoutNode): GroupNode => ({
  kind: 'group',
  id: crypto.randomUUID(),
  name,
  color,
  child,
});

function windowState(
  layout: LayoutNode | null,
  options: { dock?: SessionRecord[]; sidebarOpen?: boolean; viewMode?: 'single' | 'tiled' } = {},
): WindowState {
  return {
    id: crypto.randomUUID(),
    bounds: { x: 40, y: 40, ...SIZE },
    maximized: false,
    hidden: false,
    layout,
    dock: (options.dock ?? []).map((record) => ({ kind: 'tile' as const, sessionId: record.id, hint: null })),
    sidebarOpen: options.sidebarOpen ?? true,
    viewMode: options.viewMode,
  };
}

/** reframe on the fixture at scale 1, its page rendered at SIZE whatever its window's size on the runner. */
async function start(fixture: Fixture): Promise<{ app: ElectronApplication; page: Page; shoot: Shoot }> {
  const { app, page } = await launch(fixture.env, ['--force-device-scale-factor=1']);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setDeviceMetricsOverride', { ...SIZE, deviceScaleFactor: 1, mobile: false });
  const shoot: Shoot = async (state, component) => {
    // No caret blink or transition half way.
    await page.waitForTimeout(400);
    const file = `${state}.png`;
    fs.mkdirSync(out, { recursive: true });
    if (component) {
      await component.screenshot({ path: path.join(out, file), animations: 'disabled', caret: 'hide' });
      const box = (await component.boundingBox())!;
      captured.push({ state, file, width: Math.round(box.width), height: Math.round(box.height), component: true });
    } else {
      const { data } = await cdp.send('Page.captureScreenshot', { format: 'png' });
      fs.writeFileSync(path.join(out, file), Buffer.from(data, 'base64'));
      captured.push({ state, file, ...SIZE });
    }
  };
  return { app, page, shoot };
}
type Shoot = (state: string, component?: Locator) => Promise<void>;

/** Waits for the tiles, and for the sessions to have drawn at their size. */
async function settle(page: Page, tiles: number): Promise<void> {
  await expect(page.locator('.tile')).toHaveCount(tiles, { timeout: 20_000 });
  await page.waitForTimeout(2500);
}

async function sidebarOpen(page: Page): Promise<void> {
  if (!(await page.locator('.sidebar').isVisible()))
    await page.getByRole('button', { name: 'Sessions', exact: true }).click();
  await expect(page.locator('.sidebar')).toBeVisible();
}

test.afterAll(() => {
  // CI sets the pull request's head (GITHUB_SHA is its merge commit there).
  let commit = process.env.REFRAME_SOURCE_COMMIT || process.env.GITHUB_SHA;
  if (!commit) {
    try {
      commit = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
    } catch {
      commit = 'unknown';
    }
  }
  fs.mkdirSync(out, { recursive: true });
  const manifest = {
    side: 'electron',
    commit,
    size: SIZE,
    scale: 1,
    app: process.env.REFRAME_E2E_APP ? 'packaged' : 'dev build',
    states: captured.sort((a, b) => a.state.localeCompare(b.state)),
    skipped: SKIPPED,
  };
  fs.writeFileSync(path.join(out, 'manifest.json'), JSON.stringify(manifest, null, 2));
});

test('main window empty, About, settings pages and quota', async () => {
  const fixture = new Fixture();
  try {
    fixture.write([windowState(null, { sidebarOpen: true })]);
    const { app, page, shoot } = await start(fixture);
    await sidebarOpen(page);
    await page.waitForTimeout(1500);
    await shoot('main-empty');

    // Quota as the Coordinator reports it (its watcher is off under REFRAME_E2E).
    const now = Date.now();
    const quota: ProviderQuota[] = [
      {
        source: 'claude',
        at: now,
        windows: [
          { id: 'five_hour', minutes: 300, usedPercent: 42, resetsAt: now + 2 * HOUR },
          { id: 'seven_day', minutes: 10080, usedPercent: 81, resetsAt: now + 50 * HOUR },
        ],
      },
      { source: 'codex', at: now, windows: [{ id: 'codex:300', minutes: 300, usedPercent: 96, resetsAt: now + HOUR }] },
    ];
    for (const q of quota) await callMain(app, 'setQuota', q);
    const panel = page.getByRole('region', { name: 'Subscription quota' });
    await expect(panel).toBeVisible();
    await shoot('quota');
    await shoot('quota-panel', panel);

    await page.getByRole('button', { name: 'About Reframe' }).click();
    const about = page.getByRole('dialog', { name: 'About Reframe' });
    await expect(about).toBeVisible();
    await shoot('about');
    await about.getByRole('button', { name: 'Close' }).click();

    await page.keyboard.press('Control+Comma');
    const settings = page.getByRole('dialog', { name: 'Settings' });
    await expect(settings).toBeVisible();
    // One page per screenful of the scrolling dialog.
    const pages = await settings.evaluate((el) => Math.max(1, Math.ceil(el.scrollHeight / el.clientHeight)));
    for (let i = 0; i < pages; i++) {
      await settings.evaluate((el, i) => (el.scrollTop = i * el.clientHeight), i);
      await shoot(`settings-${i + 1}`);
    }
  } finally {
    await fixture.close();
  }
});

test('main window with sessions: sidebar rows in each status, search, error tile', async () => {
  const fixture = new Fixture();
  try {
    // demo-claude's scenes set the statuses: checkout-fix idle, rate-limiter busy, db-migration waiting.
    const idle = fixture.session('checkout-fix', 'webshop');
    const busy = fixture.session('rate-limiter', 'api-gateway');
    const waiting = fixture.session('db-migration', 'webshop');
    const ended = fixture.session('release-notes', 'mobile-app', { ended: true });
    const missing = fixture.session('old-checkout', 'webshop', { missingFolder: true });
    const pinned = fixture.past('Pinned: plan the Q3 roadmap', 'docs-site', 4);
    fixture.past('Add dark mode to the account settings', 'webshop', 3);
    fixture.past('Bump dependencies and fix the deprecation warnings', 'api-gateway', 26);
    fixture.extra = { pinnedChats: [chatKey(pinned)] };
    const layout = split('row', [split('col', [tile(idle), tile(busy)]), split('col', [tile(waiting), tile(ended)])]);
    fixture.write([windowState(layout)]);
    const { page, shoot } = await start(fixture);
    await settle(page, 4);
    await sidebarOpen(page);
    await page.waitForTimeout(3500); // the list has loaded once
    fixture.external('ci-pipeline', 'webshop'); // found: a claude in another terminal
    await expect(page.locator('.sidebar-row', { hasText: 'ci-pipeline' }).first()).toBeVisible({ timeout: 15_000 });
    await expect(tileByLabel(page, 'rate-limiter').locator('.status-busy')).toBeVisible({ timeout: 15_000 });
    await expect(tileByLabel(page, 'db-migration').locator('.status-waiting')).toBeVisible({ timeout: 15_000 });
    await shoot('main-sessions');
    await shoot('grid-2x2');
    await shoot('sidebar', page.locator('.sidebar'));
    for (const [state, label] of [
      ['sidebar-row-idle', 'checkout-fix'],
      ['sidebar-row-busy', 'rate-limiter'],
      ['sidebar-row-waiting', 'db-migration'],
      ['sidebar-row-ended', 'release-notes'],
      ['sidebar-row-found', 'ci-pipeline'],
      ['sidebar-row-pinned', 'Pinned: plan the Q3 roadmap'],
    ] as const) {
      const row = page.locator('.sidebar-row', { hasText: label }).first();
      if (await row.isVisible()) await shoot(state, row);
    }

    await page.getByRole('searchbox', { name: 'Search sessions' }).fill('check');
    await shoot('sidebar-search');
    await page.getByRole('searchbox', { name: 'Search sessions' }).fill('');

    // The error tile: a session whose folder is gone, opened from the list.
    const row = page.locator('.sidebar-row', { hasText: 'old-checkout' }).first();
    if (await row.isVisible()) {
      await row.click();
      const error = page.locator('.tile', { hasText: 'old-checkout' });
      await expect(error).toBeVisible({ timeout: 15_000 });
      await page.waitForTimeout(1500);
      await shoot('error-tile');
      await shoot('error-tile-component', error);
    }
    void missing;
  } finally {
    await fixture.close();
  }
});

test('starting status, tabs, zoom, group and collapsed', async () => {
  const fixture = new Fixture();
  try {
    const a = fixture.session('checkout-fix', 'webshop');
    const b = fixture.session('payment-webhooks', 'api-gateway');
    const c = fixture.session('docs-refresh', 'docs-site');
    const d = fixture.session('ios-crash', 'mobile-app');
    const e = fixture.session('android-push', 'mobile-app');
    const layout = split('row', [
      group('Backend', BLUE, split('col', [tile(a), tile(b)])),
      split('col', [tile(c), tile(d)]),
    ]);
    fixture.write([windowState(layout, { dock: [e] })]);
    const { page, shoot } = await start(fixture);
    // The first frames: the sessions are still starting.
    await expect(page.locator('.tile').first()).toBeVisible({ timeout: 20_000 });
    const starting = page.locator('.sidebar-row', { has: page.locator('.status-starting') }).first();
    if (await starting.isVisible().catch(() => false)) await shoot('sidebar-row-starting', starting);
    if (await page.locator('.tile .status-starting').count()) await shoot('main-starting');
    await settle(page, 4);
    await shoot('group');
    await shoot('group-frame', page.locator('.group').first());
    await shoot('collapsed');
    await shoot('dock-strip', page.locator('.dock').first());

    await tileByLabel(page, 'docs-refresh').locator('.tile-body').click();
    await page.keyboard.press('Control+Shift+Z');
    await page.waitForTimeout(800);
    await shoot('zoomed');
    await page.keyboard.press('Control+Shift+Z');

    await page.keyboard.press('Control+Shift+G');
    await page.waitForTimeout(1200);
    await shoot('tabs');
    await page.keyboard.press('Control+Shift+G');
  } finally {
    await fixture.close();
  }
});

test('New Session dialog: Claude, ChatGPT and an error; takeover dialog; hidden threads', async () => {
  const fixture = new Fixture();
  try {
    const a = fixture.session('checkout-fix', 'webshop');
    const hidden = fixture.past('An experiment nobody needs to see', 'webshop', 30);
    fixture.extra = {
      hiddenThreads: [
        {
          claudeSessionId: hidden,
          label: 'An experiment nobody needs to see',
          cwd: fixture.folder('webshop'),
          hiddenAt: Date.now() - HOUR,
        },
      ],
    };
    fixture.write([windowState(tile(a))]);
    const { app, page, shoot } = await start(fixture);
    await settle(page, 1);

    await page.getByRole('button', { name: 'New session', exact: true }).first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    const provider = dialog.getByRole('combobox', { name: 'Model provider', exact: true });
    await provider.selectOption('anthropic');
    await shoot('new-session-claude');
    await provider.selectOption('chatgpt');
    await shoot('new-session-chatgpt');
    await dialog.getByPlaceholder('Path to a project folder').fill(path.join(fixture.home, 'no-such-folder'));
    await dialog.getByRole('button', { name: 'Start' }).click();
    await expect(dialog.locator('.form-error')).toBeVisible();
    await shoot('new-session-error');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // A terminal window dropped on reframe (what foreign.ts sends after a real drop).
    const request: ForeignDropRequest = {
      requestId: 'visual',
      x: 700,
      y: 400,
      candidates: [
        {
          claudeSessionId: crypto.randomUUID(),
          cwd: fixture.folder('api-gateway'),
          name: 'rate-limiter',
          preselected: true,
        },
        {
          claudeSessionId: crypto.randomUUID(),
          cwd: fixture.folder('docs-site'),
          name: 'docs-refresh',
          preselected: false,
        },
      ],
    };
    await app.evaluate(
      ({ BrowserWindow }, r) => BrowserWindow.getAllWindows()[0]!.webContents.send('reframe:foreign-drop', r),
      request,
    );
    await expect(page.getByRole('dialog')).toBeVisible();
    await shoot('takeover');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.keyboard.press('Control+Comma');
    const manager = page.getByRole('dialog', { name: 'Settings' }).getByRole('region', { name: 'Hidden threads' });
    await manager.scrollIntoViewIfNeeded();
    await shoot('hidden-threads');
    await shoot('hidden-threads-component', manager);
  } finally {
    await fixture.close();
  }
});

test('history: selected chats and the bulk model editor', async () => {
  const fixture = new Fixture();
  try {
    const a = fixture.session('checkout-fix', 'webshop');
    fixture.past('Bulk 1: tidy the release scripts', 'webshop', 5);
    fixture.past('Bulk 2: profile the search endpoint', 'webshop', 8);
    fixture.past('Bulk 3: rename the payment events', 'api-gateway', 12);
    fixture.write([windowState(tile(a))]);
    const { app, page, shoot } = await start(fixture);
    await settle(page, 1);
    await sidebarOpen(page);
    for (const label of ['Bulk 1', 'Bulk 2'])
      await page
        .locator('.sidebar-recents .sidebar-row')
        .filter({ hasText: label })
        .click({ modifiers: ['Control'] });
    await expect(page.getByRole('group', { name: 'Selected chats' })).toBeVisible();
    await shoot('history-selection');
    await page.getByRole('button', { name: 'Selected chat actions', exact: true }).click();
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Change model…')).toBe(true);
    await expect(page.getByRole('dialog')).toBeVisible();
    await shoot('history-model-editor');
  } finally {
    await fixture.close();
  }
});
