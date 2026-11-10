import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { cleanup, launch, newSession, quit, setup } from './helpers';

test('full chat titles wrap and discovered history identifies its default provider', async () => {
  const { dir, project, env } = setup();
  const foundTitle = 'ChatGPT/Codex thread failure investigation and recovery notes with all supporting evidence';
  const managedTitle =
    'ManagedConversationWithAnUnbrokenNameThatMustRemainReadableWithoutAnyEllipsisOrHorizontalOverflow';
  const transcripts = path.join(env.REFRAME_CLAUDE_HOME!, 'projects', 'fixture');
  fs.mkdirSync(transcripts, { recursive: true });
  fs.writeFileSync(
    path.join(transcripts, `${crypto.randomUUID()}.jsonl`),
    [
      { type: 'user', cwd: project, message: { content: 'Fictional sidebar fixture' } },
      { type: 'ai-title', aiTitle: foundTitle },
    ]
      .map((entry) => JSON.stringify(entry))
      .join('\n') + '\n',
  );
  try {
    const { app, page } = await launch(env);
    await newSession(page, project, managedTitle);
    if (!(await page.locator('.sidebar').isVisible()))
      await page.getByRole('button', { name: 'Sessions', exact: true }).click();
    const found = page.locator('.sidebar-row[draggable="true"]', { hasText: foundTitle });
    const managed = page.locator('.sidebar-row[draggable="true"]', { hasText: managedTitle });
    await expect(found).toBeVisible();
    await expect(found.locator('.provider-chatgpt')).toHaveAttribute(
      'aria-label',
      'ChatGPT (default; historical provider not recorded)',
    );
    await expect(managed.locator('.provider-chatgpt')).toHaveAttribute('aria-label', 'ChatGPT');
    for (const [width, height] of [
      [900, 700],
      [1400, 900],
      [1920, 1080],
    ]) {
      await app.evaluate(
        ({ BrowserWindow }, dimensions) => {
          BrowserWindow.getAllWindows()[0]!.setSize(dimensions.width, dimensions.height);
        },
        { width: width!, height: height! },
      );
      for (const [row, label] of [
        [found, foundTitle],
        [managed, managedTitle],
      ] as const) {
        const title = row.locator('.sidebar-chat-title');
        await expect(title).toHaveText(label);
        await expect(title).toHaveAttribute('title', label);
        expect(await row.getAttribute('title')).toContain(label);
        const measured = await title.evaluate((element) => {
          const range = document.createRange();
          range.selectNodeContents(element);
          const lineTops = new Set(Array.from(range.getClientRects()).map((rect) => Math.round(rect.top)));
          const sidebar = element.closest('.sidebar')!;
          const row = element.closest('.sidebar-row')!;
          const titleBox = element.getBoundingClientRect();
          const rowBox = row.getBoundingClientRect();
          return {
            lines: lineTops.size,
            noHorizontalOverflow: sidebar.scrollWidth <= sidebar.clientWidth + 1,
            noTitleClipping:
              element.scrollWidth <= element.clientWidth + 1 && element.scrollHeight <= element.clientHeight + 1,
            insideRow:
              titleBox.left >= rowBox.left &&
              titleBox.right <= rowBox.right + 1 &&
              titleBox.bottom <= rowBox.bottom + 1,
          };
        });
        expect(measured.lines).toBeGreaterThan(1);
        expect(measured.noHorizontalOverflow).toBe(true);
        expect(measured.noTitleClipping).toBe(true);
        expect(measured.insideRow).toBe(true);
      }
      await page.screenshot({ path: path.join(process.env.REFRAME_SCREENSHOTS ?? dir, `sidebar-titles-${width}.png`) });
    }
    await quit(app);
  } finally {
    await cleanup(dir);
  }
});
