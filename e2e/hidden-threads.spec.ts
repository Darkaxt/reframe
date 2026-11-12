// End to end: hiding threads from Reframe. A past session (a transcript) and a claude running in a terminal of its own
// are hidden from the list's menu; they leave the list (and the running one is left running, its registry entry and
// transcript untouched), and Settings › Hidden threads restores them without a restart.
import fs from 'node:fs';
import path from 'node:path';
import * as pty from 'node-pty';
import { expect, test, type Page } from '@playwright/test';
import {
  answerDialogs,
  callMain,
  captureWindows,
  cleanup,
  dialogsAsked,
  launch,
  newSession,
  quit,
  setup,
  tileByLabel,
} from './helpers';

const PAST = '7fd82700-0c63-4034-a0af-0000000000a1';
const OUTSIDE = '7fd82700-0c63-4034-a0af-0000000000a2';

const row = (page: Page, text: string) => page.locator('.sidebar-row[draggable="true"]', { hasText: text });

async function sidebar(page: Page): Promise<void> {
  if (!(await page.locator('.sidebar').isVisible()))
    await page.getByRole('button', { name: 'Sessions', exact: true }).click();
}

test('a hidden thread leaves the list and comes back when restored in Settings', async () => {
  const { dir, project, env, state, running } = setup();
  const home = env.REFRAME_CLAUDE_HOME!;
  const transcript = path.join(home, 'projects', 'hidden-fixture', `${PAST}.jsonl`);
  fs.mkdirSync(path.dirname(transcript), { recursive: true });
  const content =
    JSON.stringify({ type: 'user', sessionId: PAST, cwd: project, message: { content: 'Past hidden fixture' } }) + '\n';
  fs.writeFileSync(transcript, content);

  // A claude running outside reframe, in a pseudo console of its own.
  const noHooks = path.join(dir, 'no-hooks.json');
  fs.writeFileSync(noHooks, '{}');
  const outside = pty.spawn(
    process.execPath,
    [env.REFRAME_CLAUDE_BIN!, '--session-id', OUTSIDE, '--name', 'Outside fixture', '--settings', noHooks],
    { cwd: project, env, cols: 100, rows: 30 },
  );
  let ended = false;
  outside.onExit(() => (ended = true));
  try {
    await expect.poll(running, { timeout: 10_000 }).toBe(1);
    const registryFile = path.join(home, 'sessions', `${outside.pid}.json`);
    const registry = fs.readFileSync(registryFile, 'utf8');

    const { app, page } = await launch(env);
    await sidebar(page);
    await expect(row(page, 'Past hidden fixture')).toBeVisible({ timeout: 10_000 });
    await expect(row(page, 'Outside fixture')).toBeVisible({ timeout: 10_000 });

    await answerDialogs(app, 0);
    await row(page, 'Past hidden fixture').click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Hide from Reframe…')).toBe(true);
    await expect(row(page, 'Past hidden fixture')).toHaveCount(0, { timeout: 10_000 });

    await row(page, 'Outside fixture').click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Hide from Reframe…')).toBe(true);
    await expect(row(page, 'Outside fixture')).toHaveCount(0, { timeout: 10_000 });
    expect(await dialogsAsked(app)).toEqual([
      'Hide “Past hidden fixture” from Reframe?',
      'Hide “Outside fixture” from Reframe?',
    ]);
    await expect
      .poll(async () => (await state()).hiddenThreads?.map((h) => h.claudeSessionId), { timeout: 10_000 })
      .toEqual([PAST, OUTSIDE]);

    // Hiding touched nothing of the sessions themselves.
    expect(ended).toBe(false);
    expect(fs.readFileSync(registryFile, 'utf8')).toBe(registry);
    expect(fs.readFileSync(transcript, 'utf8')).toBe(content);
    // Not even a search finds them.
    const searched = await page.evaluate(() => window.reframe.getSessionList('fixture', 20));
    expect([...searched.running, ...searched.found].map((item) => item.claudeSessionId)).toEqual([]);

    await page.keyboard.press('Control+Comma');
    const settings = page.getByRole('dialog', { name: 'Settings' });
    const manager = settings.getByRole('region', { name: 'Hidden threads' });
    await expect(manager).toContainText('Past hidden fixture');
    await expect(manager).toContainText('Outside fixture');
    await manager.getByRole('button', { name: 'Restore: Past hidden fixture' }).click();
    await expect(row(page, 'Past hidden fixture')).toBeVisible({ timeout: 10_000 });
    await expect(row(page, 'Outside fixture')).toHaveCount(0);
    await manager.getByRole('button', { name: 'Restore all' }).click();
    await expect(manager).toContainText('No hidden threads.');
    await settings.getByRole('button', { name: 'Close' }).click();
    await expect(row(page, 'Outside fixture')).toBeVisible({ timeout: 10_000 });
    await expect.poll(async () => (await state()).hiddenThreads).toEqual([]);
    expect(ended).toBe(false);
    await quit(app);
  } catch (error) {
    await captureWindows('hidden-threads');
    throw error;
  } finally {
    if (!ended) outside.kill();
    await cleanup(dir);
  }
});

test('a session reframe runs cannot be hidden until it is ended', async () => {
  const { dir, project, env, state, running } = setup();
  try {
    const { app, page } = await launch(env);
    await newSession(page, project, 'managed');
    await sidebar(page);
    // Whatever the disabled item would do is answered (an error box at most), and nothing is hidden.
    await answerDialogs(app, 0);
    await row(page, 'managed').click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Hide from Reframe…')).toBe(false);
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Hide from Reframe (end chat first)')).toBe(true);
    await expect(row(page, 'managed')).toBeVisible();

    await tileByLabel(page, 'managed').getByRole('button', { name: 'End session' }).click();
    // Ended once its claude has exited (until then the menu still says to end it first).
    await expect.poll(running, { timeout: 10_000 }).toBe(0);
    // The menu is made when it is opened: once reframe knows the session is no longer running, it offers hiding.
    await expect
      .poll(
        async () => {
          await row(page, 'managed').click({ button: 'right' });
          return callMain<boolean>(app, 'clickMenuItem', 'Hide from Reframe…');
        },
        { timeout: 10_000 },
      )
      .toBe(true);
    await expect(row(page, 'managed')).toHaveCount(0, { timeout: 10_000 });
    // Its record is kept (ended), so that restoring it brings it back as it was.
    const saved = await state();
    expect(Object.values(saved.sessions).some((s) => s.name === 'managed' && s.lifecycle === 'ended')).toBe(true);
    await quit(app);
  } catch (error) {
    await captureWindows('hidden-threads-managed');
    throw error;
  } finally {
    await cleanup(dir);
  }
});
