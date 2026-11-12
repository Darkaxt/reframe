import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type ElectronApplication, type Page } from '@playwright/test';
import { setup, launch, quit, cleanup, newSession, callMain, answerDialogs, dialogsAsked } from './helpers';

const ids = [
  '7fd82700-0c63-4034-a0af-000000000001',
  '7fd82700-0c63-4034-a0af-000000000002',
  '7fd82700-0c63-4034-a0af-000000000003',
];
function histories(home: string, project: string) {
  const folder = path.join(home, 'projects', 'bulk-fixture');
  fs.mkdirSync(folder, { recursive: true });
  const files = ids.map((id, index) => {
    const file = path.join(folder, `${id}.jsonl`);
    fs.writeFileSync(
      file,
      JSON.stringify({ type: 'user', sessionId: id, cwd: project, message: { content: `Bulk ${index + 1}` } }) + '\n',
    );
    return file;
  });
  const history = path.join(home, 'history.jsonl');
  fs.writeFileSync(history, ids.map((id) => JSON.stringify({ sessionId: id, display: 'fictional' })).join('\n') + '\n');
  const index = path.join(folder, 'sessions-index.json');
  fs.writeFileSync(index, JSON.stringify({ version: 1, entries: ids.map((sessionId) => ({ sessionId })) }));
  return { files, history, index };
}
async function sidebar(page: Page) {
  if (!(await page.locator('.sidebar').isVisible()))
    await page.getByRole('button', { name: 'Sessions', exact: true }).click();
}
async function select(page: Page, labels: string[]) {
  await sidebar(page);
  for (const label of labels)
    await page
      .locator('.sidebar-recents .sidebar-row')
      .filter({ hasText: label })
      .click({ modifiers: [process.platform === 'darwin' ? 'Meta' : 'Control'] });
  await page.getByRole('button', { name: 'Selected chat actions', exact: true }).click();
}
async function settled(app: ElectronApplication) {
  const results = await app.evaluate(async () =>
    Promise.allSettled([
      ...(globalThis as unknown as { reframe: { historyOperations: Set<Promise<unknown>> } }).reframe.historyOperations,
    ]),
  );
  expect(results.every((result) => result.status === 'fulfilled')).toBe(true);
}

test('selected native rows expose bulk model/provider controls and preserve active chats and restart state', async () => {
  test.setTimeout(0);
  const fixture = setup();
  histories(fixture.env.REFRAME_CLAUDE_HOME!, fixture.project);
  try {
    let { app, page } = await launch(fixture.env);
    await newSession(page, fixture.project, 'Bulk active');
    const preferences = (await fixture.state()).preferences;
    await select(page, ['Bulk 1', 'Bulk 2', 'Bulk active']);
    await expect(page.getByRole('group', { name: 'Selected chats' })).toContainText('3 selected');
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Change model…')).toBe(true);
    await page.locator('[role="dialog"]').waitFor();
    console.log(JSON.stringify({ modelEditor: await page.locator('[role="dialog"]').ariaSnapshot() }));
    const editor = page.getByRole('dialog', { name: 'Change selected chats’ model' });
    for (const [width, height] of [
      [900, 700],
      [1400, 900],
      [1920, 1080],
    ]) {
      await app.evaluate(
        ({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0]!.setSize(size.width, size.height),
        { width: width!, height: height! },
      );
      await expect(editor.getByRole('combobox', { name: 'Model provider', exact: true })).toBeVisible();
      const fits = await editor.evaluate((element) => {
        const bounds = element.getBoundingClientRect();
        return (
          bounds.left >= 0 &&
          bounds.right <= innerWidth &&
          bounds.top >= 0 &&
          bounds.bottom <= innerHeight &&
          element.scrollWidth <= element.clientWidth + 1
        );
      });
      expect(fits).toBe(true);
      if (process.env.REFRAME_VISUAL_EVIDENCE) {
        fs.mkdirSync(process.env.REFRAME_VISUAL_EVIDENCE, { recursive: true });
        await page.screenshot({
          path: path.join(process.env.REFRAME_VISUAL_EVIDENCE, `bulk-model-${width}.png`),
          scale: 'css',
        });
      }
    }
    await editor.getByRole('combobox', { name: 'Model provider', exact: true }).selectOption('anthropic');
    await editor.getByRole('textbox', { name: 'Model', exact: true }).fill('opus');
    await editor.getByRole('button', { name: 'Apply to selected chats' }).click();
    await expect(editor).toBeHidden();
    await expect(page.getByRole('region', { name: 'Selected chat results' })).toContainText(
      '2 completed · 1 skipped · 0 failed',
    );
    const records = Object.values((await fixture.state()).sessions).filter((record) =>
      ids.slice(0, 2).includes(record.claudeSessionId),
    );
    expect(records).toHaveLength(2);
    for (const record of records) expect(record.launch).toMatchObject({ provider: 'anthropic', model: 'opus' });
    expect((await fixture.byName('Bulk active')).launch).not.toHaveProperty('provider', 'anthropic');
    expect(fixture.launches().filter((entry) => entry.event === 'start')).toHaveLength(1);
    expect((await fixture.state()).preferences).toEqual(preferences);
    await quit(app);
    ({ app, page } = await launch(fixture.env));
    for (const id of ids.slice(0, 2))
      expect(
        Object.values((await fixture.state()).sessions).find((record) => record.claudeSessionId === id)?.launch,
      ).toMatchObject({ provider: 'anthropic', model: 'opus' });
    const resumed = Object.values((await fixture.state()).sessions).find(
      (record) => record.claudeSessionId === ids[1],
    )!;
    await sidebar(page);
    await page.locator(`.sidebar-recents .sidebar-row[data-session="${resumed.id}"]`).click();
    await expect
      .poll(
        async () => fixture.launches().find((entry) => entry.event === 'start' && entry.args.includes(ids[1]!))?.args,
      )
      .toEqual(expect.arrayContaining(['--model', 'opus']));
    await quit(app);
  } finally {
    await cleanup(fixture.dir);
  }
});

test('one bulk confirmation archives shared-index histories, restores bytes and deletes only selected chats', async () => {
  test.setTimeout(0);
  test.skip(
    process.platform !== 'win32' || !process.env.REFRAME_HISTORY_HELPER_SHA256,
    'Managed Windows exact-retirement integration',
  );
  const fixture = setup();
  const original = histories(fixture.env.REFRAME_CLAUDE_HOME!, fixture.project);
  const bytes = original.files.map((file) => fs.readFileSync(file));
  try {
    const { app, page } = await launch(fixture.env);
    await answerDialogs(app, 1);
    await select(page, ['Bulk 1', 'Bulk 2']);
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Archive selected…')).toBe(true);
    await settled(app);
    expect(await dialogsAsked(app)).toEqual(['Archive 2 selected chats?']);
    original.files.forEach((file, index) => expect(fs.readFileSync(file)).toEqual(bytes[index]));
    await answerDialogs(app, 0);
    await page.getByRole('button', { name: 'Selected chat actions', exact: true }).click();
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Archive selected…')).toBe(true);
    await settled(app);
    expect(await dialogsAsked(app)).toHaveLength(2);
    expect(fs.existsSync(original.files[0]!)).toBe(false);
    expect(fs.existsSync(original.files[1]!)).toBe(false);
    expect(fs.readFileSync(original.files[2]!)).toEqual(bytes[2]);
    expect(
      fs
        .readFileSync(original.history, 'utf8')
        .trim()
        .split('\n')
        .map((line) => JSON.parse(line).sessionId),
    ).toEqual([ids[2]]);
    expect(JSON.parse(fs.readFileSync(original.index, 'utf8')).entries).toEqual([{ sessionId: ids[2] }]);
    await expect(page.getByRole('region', { name: 'Selected chat results' })).toContainText(
      '2 completed · 0 skipped · 0 failed',
    );
    const archives = await app.evaluate(
      () => (globalThis as unknown as { reframe: { state: { archives: { id: string }[] } } }).reframe.state.archives,
    );
    for (const archive of archives)
      expect(await page.evaluate((id) => window.reframe.restoreArchive(id), archive.id)).toEqual({ ok: true });
    original.files.forEach((file, index) => expect(fs.readFileSync(file)).toEqual(bytes[index]));
    // A new selection after the archived rows disappeared.
    await page.locator('.sidebar-search').focus();
    await page.keyboard.press('Escape');
    await select(page, ['Bulk 1', 'Bulk 2']);
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Delete selected…')).toBe(true);
    await settled(app);
    expect(await dialogsAsked(app)).toHaveLength(3);
    expect(fs.existsSync(original.files[0]!)).toBe(false);
    expect(fs.existsSync(original.files[1]!)).toBe(false);
    expect(fs.readFileSync(original.files[2]!)).toEqual(bytes[2]);
    await quit(app);
  } finally {
    await cleanup(fixture.dir);
  }
});

test('a mutation failure is not retried and does not prevent a later independent archive', async () => {
  test.setTimeout(0);
  test.skip(
    process.platform !== 'win32' || !process.env.REFRAME_HISTORY_HELPER_SHA256,
    'Managed Windows exact-retirement integration',
  );
  const fixture = setup();
  const original = histories(fixture.env.REFRAME_CLAUDE_HOME!, fixture.project);
  try {
    const { app, page } = await launch(fixture.env);
    await app.evaluate((_electron, blocked) => {
      const main = (
        globalThis as unknown as {
          reframe: {
            history: {
              retire: (home: string, jobs: { prepared: { id: string } }[]) => Promise<{ id: string; ok: boolean }[]>;
            };
          };
        }
      ).reframe;
      const retire = main.history.retire.bind(main.history);
      // One retire process for the selection: the blocked chat fails inside it, the other one goes on.
      main.history.retire = async (home, jobs) => [
        { id: blocked, ok: false, error: 'Fictional packaging failure before retirement' },
        ...(await retire(
          home,
          jobs.filter((job) => job.prepared.id !== blocked),
        )),
      ];
    }, ids[0]!);
    await answerDialogs(app, 0);
    await select(page, ['Bulk 1', 'Bulk 2']);
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Archive selected…')).toBe(true);
    await settled(app);
    expect(await dialogsAsked(app)).toEqual(['Archive 2 selected chats?']);
    await expect(page.getByRole('region', { name: 'Selected chat results' })).toContainText(
      '1 completed · 0 skipped · 1 failed',
    );
    expect(fs.existsSync(original.files[0]!)).toBe(true);
    expect(fs.existsSync(original.files[1]!)).toBe(false);
    expect(fs.existsSync(original.files[2]!)).toBe(true);
    await quit(app);
  } finally {
    await cleanup(fixture.dir);
  }
});

test('a chat that never wrote Claude history is deleted as a Reframe entry only and is not archived', async () => {
  test.setTimeout(0);
  const fixture = setup();
  const original = histories(fixture.env.REFRAME_CLAUDE_HOME!, fixture.project);
  const empty = ['7fd82700-0c63-4034-a0af-0000000000e1', '7fd82700-0c63-4034-a0af-0000000000e2'];
  try {
    let { app, page } = await launch(fixture.env);
    await quit(app);
    const file = path.join(fixture.env.REFRAME_USER_DATA!, 'state.json');
    const state = JSON.parse(fs.readFileSync(file, 'utf8'));
    empty.forEach((id, index) => {
      state.sessions[id] = {
        id,
        claudeSessionId: id,
        cwd: fixture.project,
        name: `Never started ${index + 1}`,
        launch: { provider: 'chatgpt' },
        lifecycle: 'ended',
        createdAt: Date.now(),
        lastActiveAt: Date.now(),
      };
    });
    fs.writeFileSync(file, JSON.stringify(state));
    ({ app, page } = await launch(fixture.env));
    await answerDialogs(app, 0);
    await select(page, ['Never started 1', 'Never started 2']);
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Archive selected…')).toBe(true);
    await settled(app);
    expect(await dialogsAsked(app)).toEqual([]);
    await expect(page.getByRole('region', { name: 'Selected chat results' })).toContainText(
      '0 completed · 0 skipped · 2 failed',
    );
    await page.getByRole('button', { name: 'Selected chat actions', exact: true }).click();
    expect(await callMain<boolean>(app, 'clickMenuItem', 'Delete selected…')).toBe(true);
    await settled(app);
    expect(await dialogsAsked(app)).toEqual(['Permanently delete 2 selected chats?']);
    await expect(page.getByRole('region', { name: 'Selected chat results' })).toContainText(
      '2 completed · 0 skipped · 0 failed',
    );
    const after = await fixture.state();
    empty.forEach((id) => expect(after.sessions[id]).toBeUndefined());
    original.files.forEach((history) => expect(fs.existsSync(history)).toBe(true));
    await quit(app);
  } finally {
    await cleanup(fixture.dir);
  }
});
