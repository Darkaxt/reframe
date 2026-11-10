import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { folderKey } from '../src/shared/folder-workspaces';
import { setup, launch, quit, cleanup, newSession, callMain, answerDialogs, dialogsAsked } from './helpers';

test('providers, automatic folders and native inactive-chat actions survive restart', async () => {
  const { dir, project, env, state, byName, launches } = setup();
  const anthropicBinary = path.join(dir, 'claude-anthropic.mjs');
  fs.copyFileSync(env.REFRAME_CLAUDE_BIN!, anthropicBinary);
  env.REFRAME_ANTHROPIC_CLAUDE_BIN = anthropicBinary;
  const anthropicProject = path.join(dir, 'anthropic', path.basename(project));
  fs.mkdirSync(anthropicProject, { recursive: true });
  try {
    let { app, page } = await launch(env);
    await page.evaluate(() => window.reframe.setPreferences({ defaultModel: 'gpt-fixture' }));
    await page.keyboard.press('Control+Comma');
    const settings = page.getByRole('dialog', { name: 'Settings', exact: true });
    await settings.getByRole('combobox', { name: 'Default effort', exact: true }).selectOption('high');
    await settings.getByRole('button', { name: 'Close', exact: true }).click();
    await expect.poll(() => state().preferences.defaultEffort).toBe('high');
    await newSession(page, project, 'ChatGPT fixture');
    await expect
      .poll(
        () =>
          launches().find(
            (entry) => entry.event === 'start' && entry.args.includes(byName('ChatGPT fixture').claudeSessionId),
          )?.args,
      )
      .toEqual(expect.arrayContaining(['--effort', 'high']));
    await expect(page.locator('.tile .provider-icon[aria-label="ChatGPT"]')).toBeVisible();
    await page.getByRole('button', { name: 'New session', exact: true }).first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('combobox', { name: 'Effort', exact: true })).toHaveValue('high');
    await dialog.getByRole('combobox', { name: 'Effort', exact: true }).selectOption('xhigh');
    await dialog.getByRole('combobox', { name: 'Model provider' }).selectOption('anthropic');
    await dialog.getByText('Advanced', { exact: true }).click();
    await expect(dialog.getByLabel('Model', { exact: true })).toHaveValue('');
    await dialog.getByPlaceholder('Path to a project folder').fill(anthropicProject);
    await dialog.getByPlaceholder('Shown on the tile and in notifications').fill('Anthropic fixture');
    await dialog.getByRole('button', { name: 'Start', exact: true }).click();
    await expect(page.locator('.tile .provider-icon[aria-label="Anthropic"]')).toBeVisible();
    await expect
      .poll(
        () =>
          launches().find((l) => l.event === 'start' && l.args.includes(byName('Anthropic fixture').claudeSessionId))
            ?.args,
      )
      .toEqual(expect.arrayContaining(['--model', 'sonnet', '--effort', 'xhigh']));
    expect(
      launches().find((l) => l.event === 'start' && l.args.includes(byName('Anthropic fixture').claudeSessionId))
        ?.script,
    ).toBe(anthropicBinary);
    expect(
      launches().find((l) => l.event === 'start' && l.args.includes(byName('ChatGPT fixture').claudeSessionId))?.script,
    ).toBe(env.REFRAME_CLAUDE_BIN);
    expect((state().sessions[byName('Anthropic fixture').id]!.launch as { provider?: string }).provider).toBe(
      'anthropic',
    );
    if (!(await page.locator('.sidebar').isVisible()))
      await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    const projects = page.locator('.sidebar-projects');
    await expect(projects.locator('.folder-workspace')).toHaveCount(1);
    await expect(projects.locator('.sidebar-provider-heading')).toHaveCount(0);
    await projects.locator('.folder-workspace').click();
    await expect(projects.locator('.sidebar-project-chats')).toContainText('ChatGPT fixture');
    await expect(projects.locator('.sidebar-project-chats')).toContainText('Anthropic fixture');
    await expect(projects.locator('.sidebar-project-chats .provider-icon[aria-label="ChatGPT"]')).toHaveCount(1);
    await expect(projects.locator('.sidebar-project-chats .provider-icon[aria-label="Anthropic"]')).toHaveCount(1);
    await projects.getByRole('button', { name: `New session in ${path.basename(project)}`, exact: true }).click();
    await expect(page.getByRole('dialog').getByRole('combobox', { name: 'Model provider', exact: true })).toHaveValue(
      'chatgpt',
    );
    await expect(page.getByRole('dialog').getByPlaceholder('Path to a project folder')).toHaveValue(project);
    await expect(page.getByRole('dialog').getByRole('combobox', { name: 'Effort', exact: true })).toHaveValue('high');
    await page
      .getByRole('dialog')
      .getByRole('combobox', { name: 'Model provider', exact: true })
      .selectOption('anthropic');
    await expect(page.getByRole('dialog').getByPlaceholder('Path to a project folder')).toHaveValue(anthropicProject);
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.locator('.sidebar-recents .sidebar-row[draggable="true"]')).toHaveCount(2);
    await projects.locator('.folder-workspace').click();
    await page.locator('.tile').first().locator('.tile-header').click({ button: 'right' });
    const archiveEnabled = await app.evaluate(() => {
      const main = (
        globalThis as unknown as { reframe: { lastMenu: { items: { label: string; enabled: boolean }[] } } }
      ).reframe;
      return main.lastMenu.items.find((i) => i.label === 'Archive chat (end chat first)')?.enabled;
    });
    expect(archiveEnabled).toBe(false);
    if (process.platform === 'win32' && process.env.REFRAME_HISTORY_HELPER_SHA256) {
      await page
        .locator('.tile', { has: page.locator('.tile-label', { hasText: 'ChatGPT fixture' }) })
        .getByRole('button', { name: 'End session', exact: true })
        .click();
      await expect.poll(() => state().sessions[byName('ChatGPT fixture').id]?.lifecycle).toBe('ended');
      const ended = page.getByRole('button', { name: /ChatGPT fixture.*ended/ });
      await expect(ended.locator('.status-ended')).toBeVisible();
      await ended.click({ button: 'right' });
      expect(await callMain<boolean>(app, 'clickMenuItem', 'ChatGPT')).toBe(true);
      await expect
        .poll(
          () =>
            app.evaluate(
              () =>
                (globalThis as unknown as { reframe: { historyOperations: Set<unknown> } }).reframe.historyOperations
                  .size,
            ),
          { timeout: 60000 },
        )
        .toBe(0);
      await expect
        .poll(() => (state().sessions[byName('ChatGPT fixture').id]!.launch as { model?: string }).model)
        .toBe('gpt-fixture');
      await ended.click();
      await expect(page.locator('.tile .provider-icon[aria-label="ChatGPT"]')).toBeVisible();
    }
    await quit(app);
    ({ app, page } = await launch(env));
    await expect(page.locator('.tile .provider-icon[aria-label="ChatGPT"]')).toHaveCount(1);
    await expect(page.locator('.tile .provider-icon[aria-label="Anthropic"]')).toHaveCount(1);
    await expect(page.locator('.sidebar-projects .folder-workspace')).toHaveCount(1);
    await expect(page.locator('.sidebar-projects .folder-workspace')).toHaveAttribute('aria-expanded', 'false');
    expect(state().preferences.defaultEffort).toBe('high');
    expect(byName('ChatGPT fixture').launch.effort).toBe('high');
    expect(byName('Anthropic fixture').launch.effort).toBe('xhigh');
    await page.getByRole('button', { name: 'New session', exact: true }).first().click();
    const defaults = await page.evaluate(() => window.reframe.getDialogDefaults());
    await expect(page.getByRole('dialog').getByPlaceholder('Path to a project folder')).toHaveValue(
      defaults.providerWorkspaceRoots.chatgpt,
    );
    await page
      .getByRole('dialog')
      .getByRole('combobox', { name: 'Model provider', exact: true })
      .selectOption('anthropic');
    await expect(page.getByRole('dialog').getByPlaceholder('Path to a project folder')).toHaveValue(
      defaults.providerWorkspaceRoots.anthropic,
    );
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await quit(app);
  } finally {
    await cleanup(dir);
  }
});

test('legacy expansion merges same folder names across paths/providers without moving histories', async () => {
  const fixture = setup();
  try {
    let { app, page } = await launch(fixture.env);
    await quit(app);
    const file = path.join(fixture.env.REFRAME_USER_DATA!, 'state.json');
    const state = JSON.parse(fs.readFileSync(file, 'utf8'));
    const ids = ['a9157600-1122-4333-8444-000000000001', 'a9157600-1122-4333-8444-000000000002'];
    state.sessions = Object.fromEntries(
      ids.map((id, index) => [
        id,
        {
          id,
          claudeSessionId: id,
          cwd: index ? path.join(fixture.dir, 'anthropic', path.basename(fixture.project)) : fixture.project,
          name: index ? 'Legacy Claude' : 'Legacy ChatGPT',
          launch: { provider: index ? 'anthropic' : 'chatgpt' },
          lifecycle: 'ended',
          createdAt: 0,
          lastActiveAt: 0,
        },
      ]),
    );
    const legacyKey = folderKey(fixture.project);
    state.expandedProjects = [legacyKey, `chatgpt:${legacyKey}`, `anthropic:${legacyKey}`];
    state.folderWorkspaces = [
      { id: legacyKey, cwd: fixture.project, name: path.basename(fixture.project), sessionIds: ids },
    ];
    delete state.preferences.defaultEffort;
    fs.writeFileSync(file, JSON.stringify(state));
    ({ app, page } = await launch(fixture.env));
    if (!(await page.locator('.sidebar').isVisible()))
      await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    const projects = page.locator('.sidebar-projects');
    await expect(projects.locator('.folder-workspace')).toHaveCount(1);
    await expect(projects.locator('.folder-workspace')).toHaveAttribute('aria-expanded', 'true');
    await expect(projects.locator('.sidebar-project-chats')).toContainText('Legacy ChatGPT');
    await expect(projects.locator('.sidebar-project-chats')).toContainText('Legacy Claude');
    expect(fixture.state().expandedProjects).toEqual([`project:${path.basename(fixture.project).toLowerCase()}`]);
    expect(fixture.launches()).toHaveLength(0);
    expect(Object.values(fixture.state().sessions).map((record) => record.cwd)).toEqual([
      fixture.project,
      path.join(fixture.dir, 'anthropic', path.basename(fixture.project)),
    ]);
    expect(fixture.state().preferences.defaultEffort).toBeUndefined();
    await quit(app);
  } finally {
    await cleanup(fixture.dir);
  }
});

test('archive and restore a fictional discovered chat through the context menu', async () => {
  // Native process/identity checks finish by event; elapsed time is not a failed retirement.
  test.setTimeout(0);
  test.skip(
    process.platform !== 'win32' || !process.env.REFRAME_HISTORY_HELPER_SHA256,
    'Requires the installed Windows exact-retirement helper; qualified on the managed host.',
  );
  const { dir, project, env } = setup();
  const tid = '6d4c6300-9f9c-4fe5-a76d-5091b6b1f11a';
  const history = path.join(env.REFRAME_CLAUDE_HOME!, 'projects', 'fixture', tid + '.jsonl');
  fs.mkdirSync(path.dirname(history), { recursive: true });
  const content =
    JSON.stringify({ type: 'user', cwd: project, sessionId: tid, message: { content: 'Archive fixture' } }) + '\n';
  fs.writeFileSync(history, content);
  try {
    const { app, page } = await launch(env);
    if (!(await page.locator('.sidebar').isVisible()))
      await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    const row = page.getByRole('button', { name: /Archive fixture/ });
    await expect(row).toBeVisible();
    await answerDialogs(app, 0);
    await row.click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Archive chat…')).toBe(true);
    const retirement = await app.evaluate(async () =>
      (
        await Promise.allSettled([
          ...(globalThis as unknown as { reframe: { historyOperations: Set<Promise<unknown>> } }).reframe
            .historyOperations,
        ])
      ).map((result) =>
        result.status === 'fulfilled'
          ? { status: result.status }
          : { status: result.status, error: String(result.reason) },
      ),
    );
    expect(retirement).toEqual([{ status: 'fulfilled' }]);
    expect(await dialogsAsked(app)).toEqual([expect.stringMatching(/^Archive /)]);
    await expect.poll(() => fs.existsSync(history), { timeout: 30000 }).toBe(false);
    await page.locator('.sidebar-archives > summary').click();
    await expect(page.getByRole('button', { name: /Archive fixture.*Restore chat/ })).toBeVisible();
    await page.getByRole('button', { name: /Archive fixture.*Restore chat/ }).click();
    await expect.poll(() => fs.existsSync(history), { timeout: 30000 }).toBe(true);
    expect(fs.readFileSync(history, 'utf8')).toBe(content);
    const restored = page.getByRole('button', { name: /Archive fixture.*ended/ });
    await restored.click({ button: 'right' });
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Delete chat…')).toBe(true);
    const deletion = await app.evaluate(async () =>
      (
        await Promise.allSettled([
          ...(globalThis as unknown as { reframe: { historyOperations: Set<Promise<unknown>> } }).reframe
            .historyOperations,
        ])
      ).map((result) =>
        result.status === 'fulfilled'
          ? { status: result.status }
          : { status: result.status, error: String(result.reason) },
      ),
    );
    expect(deletion).toEqual([{ status: 'fulfilled' }]);
    await expect.poll(() => fs.existsSync(history), { timeout: 30000 }).toBe(false);
    await expect(page.getByRole('button', { name: /Archive fixture/ })).toHaveCount(0);
    await quit(app);
  } finally {
    await cleanup(dir);
  }
});

test('desktop sizes retain provider controls, readable tiles and a scrollable folder list', async () => {
  const { dir, project, env } = setup();
  try {
    const { app, page } = await launch(env);
    await newSession(page, project, 'Layout fixture');
    if (!(await page.locator('.sidebar').isVisible()))
      await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    for (const [width, height] of [
      [900, 700],
      [1400, 900],
      [1920, 1080],
    ]) {
      await app.evaluate(
        ({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0]!.setSize(size.width, size.height),
        { width: width!, height: height! },
      );
      await expect(page.locator('.tile .provider-icon[aria-label="ChatGPT"]')).toBeVisible();
      await expect(page.getByRole('button', { name: 'New session', exact: true }).first()).toBeVisible();
      const metrics = await page
        .locator('.tile')
        .evaluate((e) => ({ width: e.getBoundingClientRect().width, height: e.getBoundingClientRect().height }));
      expect(metrics.width).toBeGreaterThan(400);
      expect(metrics.height).toBeGreaterThan(400);
      await page.getByRole('button', { name: 'New session', exact: true }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('combobox', { name: 'Effort', exact: true })).toBeVisible();
      const dialogFits = await dialog.evaluate((element) => {
        const box = element.getBoundingClientRect();
        return box.top >= 0 && box.bottom <= window.innerHeight + 1 && element.scrollWidth <= element.clientWidth + 1;
      });
      expect(dialogFits).toBe(true);
      const output = process.env.REFRAME_VISUAL_EVIDENCE;
      if (output) {
        fs.mkdirSync(output, { recursive: true });
        await page.screenshot({ path: path.join(output, `desktop-${width}x${height}.png`), scale: 'css' });
      }
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    }
    await quit(app);
  } finally {
    await cleanup(dir);
  }
});
