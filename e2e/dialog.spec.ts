// End to end: the "New session" dialog leaves the name empty unless the user types one (the folder is no name: Claude
// Code names the session), and without Claude Code it says how to get it.
import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { captureWindows, cleanup, launch, quit, setup } from './helpers';

const open = async (page: Page) => {
  await page.getByRole('button', { name: 'New session' }).first().click();
  const dialog = page.getByRole('dialog');
  return {
    dialog,
    folder: dialog.getByPlaceholder('Path to a project folder'),
    name: dialog.getByPlaceholder('Optional: otherwise Claude Code names it'),
  };
};

test('the session name stays empty unless typed; the folder is not used as one', async () => {
  const { dir, project, env } = setup();
  const other = path.join(dir, 'other-project');
  fs.mkdirSync(other);
  try {
    const { app, page } = await launch(env);
    const d = await open(page);
    await expect(d.dialog.getByRole('checkbox', { name: 'Use the folder name' })).toHaveCount(0);
    await d.folder.fill(project);
    await expect(d.name).toHaveValue('');
    await d.name.fill('mine');
    await d.folder.fill(other);
    await expect(d.name).toHaveValue('mine');
    await d.dialog.getByRole('button', { name: 'Cancel' }).click();
    await quit(app);
  } catch (error) {
    await captureWindows('dialog');
    throw error;
  } finally {
    await cleanup(dir);
  }
});

test('without Claude Code, the dialog says how to get it', async () => {
  const { dir, project, env } = setup();
  // No claude anywhere: no stand-in, and a PATH (whatever its spelling) without one.
  env.REFRAME_CLAUDE_BIN = '';
  for (const key of Object.keys(env).filter((k) => k.toUpperCase() === 'PATH')) env[key] = dir;
  try {
    const { app, page } = await launch(env);
    const d = await open(page);
    await d.folder.fill(project);
    await d.dialog.getByRole('button', { name: 'Start' }).click();
    const error = d.dialog.locator('.form-error');
    await expect(error).toContainText('Install it on this computer: claude.com/claude-code');
    if (process.platform === 'win32')
      await expect(error).toContainText('Installed it only in WSL? Choose a folder in WSL');
    else await expect(error).not.toContainText('WSL');
    await quit(app);
  } catch (error) {
    await captureWindows('dialog-no-claude');
    throw error;
  } finally {
    await cleanup(dir);
  }
});
