import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { callMain, cleanup, launch, newSession, quit, setup } from './helpers';
import { workspaceKey } from '../src/shared/folder-workspaces';

test('one catalogue has persistent pins, independent project branches and unified recents', async () => {
  test.setTimeout(0);
  const { dir, project, env, launches, state } = setup();
  const other = path.join(dir, 'other-project');
  const sameProject = path.join(dir, 'other-parent', path.basename(project));
  fs.mkdirSync(other, { recursive: true });
  const transcripts = path.join(env.REFRAME_CLAUDE_HOME!, 'projects', 'fixture');
  fs.mkdirSync(transcripts, { recursive: true });
  const past = (title: string, cwd: string, age: number) => {
    const id = crypto.randomUUID();
    const file = path.join(transcripts, `${id}.jsonl`);
    fs.writeFileSync(
      file,
      [
        { type: 'user', cwd, message: { content: 'Fictional tree fixture' } },
        { type: 'ai-title', aiTitle: title },
      ]
        .map((entry) => JSON.stringify(entry))
        .join('\n') + '\n',
    );
    const time = new Date(Date.now() - age * 60000);
    fs.utimesSync(file, time, time);
    return id;
  };
  for (let index = 0; index < 25; index++) past(`Project A history ${index}`, project, 100 + index);
  past('Same project from another path', sameProject, 105);
  past('Project B recent', other, 1);
  try {
    let { app, page } = await launch(env);
    await newSession(page, project, 'Managed project A');
    if (!(await page.locator('.sidebar').isVisible()))
      await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    await expect(page.locator('.sidebar-heading')).toHaveText(['Pinned', 'Projects', 'Recents']);
    const branchA = page.locator(`.sidebar-project[data-project=${JSON.stringify(workspaceKey(project))}]`);
    const branchB = page.locator(`.sidebar-project[data-project=${JSON.stringify(workspaceKey(other))}]`);
    await expect(branchA).toHaveCount(1);
    await expect(branchB).toHaveCount(1);
    await branchA.locator('.folder-workspace').focus();
    await page.keyboard.press('Enter');
    await expect(branchA.locator('.folder-workspace')).toHaveAttribute('aria-expanded', 'true');
    await expect(page.locator('.sidebar-recents')).toContainText('Project B recent');
    await branchA.getByRole('button', { name: 'Show more', exact: true }).click();
    const old = branchA.locator('.sidebar-row[draggable="true"]', { hasText: 'Project A history 24' });
    await expect(old).toBeVisible();
    await expect(branchA.locator('.sidebar-project-chats')).toContainText('Same project from another path');
    await expect(page.locator('.sidebar-project')).toHaveCount(2);
    await old.click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Pin chat')).toBe(true);
    await expect(page.locator('.sidebar-pinned .sidebar-row[draggable="true"]')).toHaveCount(1);
    await expect(page.locator('.sidebar-pinned')).toContainText('Project A history 24');
    await branchB.locator('.folder-workspace').click();
    await expect(branchB.locator('.sidebar-project-chats')).toContainText('Project B recent');
    await branchA.locator('.folder-workspace').click();
    expect(launches().filter((entry) => entry.event === 'start')).toHaveLength(1);
    for (const [width, height] of [
      [900, 700],
      [1400, 900],
      [1920, 1080],
    ]) {
      await app.evaluate(
        ({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0]!.setSize(size.width, size.height),
        { width: width!, height: height! },
      );
      const sidebar = page.locator('.sidebar');
      expect(await sidebar.evaluate((element) => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
      await page.screenshot({ path: path.join(process.env.REFRAME_SCREENSHOTS || dir, `project-tree-${width}.png`) });
    }
    await quit(app);
    ({ app, page } = await launch(env));
    await expect(page.locator('.sidebar-pinned')).toContainText('Project A history 24');
    await expect(
      page.locator(`.sidebar-project[data-project=${JSON.stringify(workspaceKey(other))}] .folder-workspace`),
    ).toHaveAttribute('aria-expanded', 'true');
    const pinned = page.locator('.sidebar-pinned .sidebar-row[draggable="true"]');
    await pinned.click();
    await expect(pinned).toHaveAttribute('data-session', /.+/);
    await expect(page.locator('.sidebar-pinned .sidebar-row[draggable="true"]')).toHaveCount(1);
    await pinned.click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Unpin chat')).toBe(true);
    await expect.poll(async () => ((await state()) as unknown as { pinnedChats: string[] }).pinnedChats).toEqual([]);
    await quit(app);
  } finally {
    await cleanup(dir);
  }
});
