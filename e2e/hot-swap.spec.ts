// End to end: reframe quits keeping its sessions (the default), and the next reframe takes them over from the PTY
// host, which outlived it: the same claude process, its screen as it was, input and hooks working. "End sessions and
// quit" then ends them and the host, as quitting did before.
import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { captureWindows, cleanup, hostPids, launch, newSession, quit, setup, tileByLabel } from './helpers';

const alive = (pid: number) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

/** The tile's find finds a word on its screen (so the screen there is the session's). */
async function expectOnScreen(page: Page, tile: Locator, word: string): Promise<void> {
  await tile.locator('.tile-body').click();
  await page.keyboard.press('Control+Shift+F');
  await expect(tile.locator('.tile-find-input')).toBeFocused();
  await page.keyboard.type(word);
  await expect(tile.locator('.tile-find-count')).toHaveText(/^\d+ of \d+$/);
  await page.keyboard.press('Escape');
  await expect(tile.locator('.tile-find')).toHaveCount(0);
}

test('a session survives quitting and is taken over by the next reframe', async () => {
  const { dir, project, env, launches, byName } = setup();
  delete env.REFRAME_QUIT_ENDS_SESSIONS;
  const userData = env.REFRAME_USER_DATA!;
  try {
    let { app, page } = await launch(env);
    await newSession(page, project, 'alpha');
    let tile = tileByLabel(page, 'alpha');
    await tile.locator('.tile-body').click();
    const used = byName('alpha').lastActiveAt;
    await page.keyboard.type('zebra before the swap');
    await page.keyboard.press('Enter');
    await expect.poll(() => byName('alpha').lastActiveAt).toBeGreaterThan(used);
    await expect(tile.locator('.status-idle')).toBeVisible();
    const pid = byName('alpha').pid!;
    const hosts = hostPids(userData);
    expect(hosts).toHaveLength(1);
    await quit(app);

    // Reframe is gone; the claude and its host are not.
    expect(alive(pid)).toBe(true);
    expect(alive(hosts[0]!)).toBe(true);
    expect(byName('alpha').lifecycle).toBe('open');

    ({ app, page } = await launch(env));
    tile = tileByLabel(page, 'alpha');
    await expect(tile.locator('.status-idle')).toBeVisible({ timeout: 15_000 });
    // The same process, not a resume, and the screen from before the swap.
    expect(launches().filter((l) => l.event === 'start')).toHaveLength(1);
    expect(byName('alpha').pid).toBe(pid);
    expect(hostPids(userData)).toEqual(hosts);
    await expectOnScreen(page, tile, 'zebra');
    // Input reaches it, and its hooks reach this reframe (busy on the prompt, idle on Stop).
    const before = byName('alpha').lastActiveAt;
    await tile.locator('.tile-body').click();
    await page.keyboard.type('giraffe after the swap');
    await page.keyboard.press('Enter');
    await expect.poll(() => byName('alpha').lastActiveAt).toBeGreaterThan(before);
    await expect(tile.locator('.status-idle')).toBeVisible();
    await expectOnScreen(page, tile, 'giraffe');
    const log = path.join(userData, 'logs', 'main.log');
    const afterSwap = () => fs.readFileSync(log, 'utf8').split('attached to the running pty host')[1] ?? '';
    expect(afterSwap()).toContain('session taken over from the pty host');
    // The hooks of the session (pointed at the host when the first reframe started it) reach this one.
    await expect.poll(() => afterSwap().includes('"event":"Stop"')).toBe(true);

    // "End sessions and quit": the session and its host end; it resumes on the next start.
    const closed = app.waitForEvent('close');
    await app.evaluate(() => {
      const reframe = (globalThis as unknown as { reframe: { endSessionsAndQuit(): void } }).reframe;
      setTimeout(() => reframe.endSessionsAndQuit(), 0);
    });
    await closed;
    await expect.poll(() => alive(pid), { timeout: 15_000 }).toBe(false);
    await expect.poll(() => alive(hosts[0]!), { timeout: 15_000 }).toBe(false);
    expect(byName('alpha').lifecycle).toBe('open');
  } catch (error) {
    await captureWindows('hot-swap');
    throw error;
  } finally {
    await cleanup(dir);
  }
});
