import fs from 'node:fs';
import { once } from 'node:events';
import { expect, test } from '@playwright/test';
import { cleanup, launch, setup } from './helpers';

test('native app stays responsive when the launcher stderr pipe closes', async () => {
  test.setTimeout(0);
  const { dir, env } = setup();
  try {
    const { app, page } = await launch(env);
    const stderr = app.process().stderr!;
    const closed = once(stderr, 'close');
    stderr.destroy();
    await closed;
    const file = await app.evaluate(() => {
      const { log } = (
        globalThis as unknown as {
          reframe: { log: ((level: 'info' | 'warn', message: string) => void) & { file: string } };
        }
      ).reframe;
      log('warn', 'qualification: launcher stderr closed');
      log('info', 'qualification: application remains responsive');
      return log.file;
    });
    await expect(page.getByRole('button', { name: 'New session', exact: true }).first()).toBeVisible();
    const log = fs.readFileSync(file, 'utf8');
    expect(log).toContain('qualification: launcher stderr closed');
    expect(log).toContain('qualification: application remains responsive');
    expect(log).not.toContain('uncaught exception');
    expect(log).not.toContain('EPIPE');
    // Playwright normally sees Node's debugger-disconnect notice on stderr. This
    // test intentionally closed that pipe; its supported close also disconnects
    // the inspector explicitly after requesting the normal Electron quit path.
    const exited = once(app.process(), 'exit');
    await app.close();
    await exited;
  } finally {
    await cleanup(dir);
  }
});
