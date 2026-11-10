// Plain-text log in the profile (logs/main.log), for finding out afterwards what happened.
// One line per event; the file is rotated to main.old.log once it passes 2 MB.
import fs from 'node:fs';
import path from 'node:path';

const MAX_BYTES = 2 * 1024 * 1024;

export type LogLevel = 'info' | 'warn' | 'error';

export type LogFn = (level: LogLevel, message: string, data?: Record<string, unknown>) => void;

export interface Logger extends LogFn {
  readonly file: string;
}

export function createLogger(dir: string): Logger {
  const file = path.join(dir, 'main.log');
  let checked = false;
  const log = (level: LogLevel, message: string, data?: Record<string, unknown>) => {
    const line = `${new Date().toISOString()} ${level.toUpperCase().padEnd(5)} ${message}${
      data ? ` ${JSON.stringify(data)}` : ''
    }\n`;
    try {
      if (!checked) {
        fs.mkdirSync(dir, { recursive: true });
        checked = true;
      }
      if (fs.existsSync(file) && fs.statSync(file).size > MAX_BYTES) {
        fs.renameSync(file, path.join(dir, 'main.old.log'));
      }
      fs.appendFileSync(file, line);
    } catch {
      // logging must never break the app
    }
    if (level !== 'info' || process.env.REFRAME_LOG_STDOUT) {
      try {
        // The launcher may disconnect its pipe while the tray app keeps running.
        // Stream.write emits EPIPE asynchronously, outside a surrounding try/catch;
        // logging that uncaught error would write to the same broken pipe forever.
        fs.writeSync(process.stderr.fd, line);
      } catch {
        // The profile log remains the durable output when stderr is unavailable.
      }
    }
  };
  return Object.assign(log, { file });
}
