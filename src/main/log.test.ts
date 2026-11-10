import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { pathToFileURL } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';
import { createLogger } from './log';

const temps: string[] = [];
afterEach(() => {
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe('createLogger', () => {
  it('keeps logging and responding after its stderr reader disconnects', async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-log-'));
    temps.push(dir);
    const loggerUrl = pathToFileURL(path.join(import.meta.dirname, 'log.ts')).href;
    const child = spawn(
      process.execPath,
      [
        '--input-type=module',
        '--eval',
        `
      import { createLogger } from ${JSON.stringify(loggerUrl)};
      const log = createLogger(process.argv[1]);
      let uncaught = 0;
      process.on('uncaughtException', (error) => {
        // Bound a regression's recursive error path without hiding a failed result.
        if (++uncaught > 1) process.exit(42);
        log('error', 'uncaught exception', { code: error.code });
      });
      process.on('message', () => {
        log('warn', 'stderr reader disconnected');
        setImmediate(() => {
          log('info', 'still responsive');
          process.send({ uncaught });
          process.disconnect();
        });
      });
      process.send('ready');
    `,
        dir,
      ],
      { stdio: ['ignore', 'ignore', 'pipe', 'ipc'] },
    );
    const exited = once(child, 'exit');
    await once(child, 'message');
    const closed = once(child.stderr!, 'close');
    child.stderr!.destroy();
    await closed;
    const response = once(child, 'message');
    child.send('log');
    const [code] = await exited;
    expect(code).toBe(0);
    expect((await response)[0]).toEqual({ uncaught: 0 });
    const lines = fs.readFileSync(path.join(dir, 'main.log'), 'utf8');
    expect(lines).toContain('stderr reader disconnected');
    expect(lines).toContain('still responsive');
    expect(lines).not.toContain('uncaught exception');
  });

  it('appends one line per event with level and data', () => {
    const dir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-log-')), 'logs');
    temps.push(path.dirname(dir));
    const log = createLogger(dir);
    log('info', 'session started', { id: 'a' });
    log('info', 'second');
    const lines = fs.readFileSync(log.file, 'utf8').trim().split('\n');
    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatch(/^\d{4}-\d\d-\d\dT.* INFO {2}session started {"id":"a"}$/);
  });

  it('rotates a large file', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-log-'));
    temps.push(dir);
    fs.writeFileSync(path.join(dir, 'main.log'), 'x'.repeat(2 * 1024 * 1024 + 1));
    createLogger(dir)('info', 'fresh');
    expect(fs.readFileSync(path.join(dir, 'main.log'), 'utf8')).toContain('fresh');
    expect(fs.statSync(path.join(dir, 'main.old.log')).size).toBeGreaterThan(2 * 1024 * 1024);
  });
});
