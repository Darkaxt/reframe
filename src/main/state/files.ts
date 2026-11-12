// The `state` service's files (docs/ptyhost-protocol.md, "Services"), as the Node PTY host serves them; the Rust
// host's is rust/reframe-services/src/state.rs, held to the same by the conformance tests. The content is opaque here:
// reframe checks it against its schema (store.ts). Each file read or written gets a revision; a write names the
// revision it replaces and is refused (`conflict`) when that is not the file's current one, or the file changed on
// disk since. It is written as StateStore writes it: `<file>.<pid>.tmp`, flushed, renamed over the file; with
// `backup`, the file there first is copied to `<file>.bak`.
import fs from 'node:fs';
import path from 'node:path';

export class StateConflict extends Error {}

type Seen = { size: number; mtimeMs: number } | null;

const same = (a: Seen, b: Seen) => (a && b ? a.size === b.size && a.mtimeMs === b.mtimeMs : a === b);

function seen(file: string): Seen {
  try {
    const { size, mtimeMs } = fs.statSync(file);
    return { size, mtimeMs };
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}

export class StateFiles {
  private last = 0;
  private records = new Map<string, { revision: number; seen: Seen }>();

  private next(file: string, now: Seen): number {
    const revision = ++this.last;
    this.records.set(file, { revision, seen: now });
    return revision;
  }

  /** The file's current revision and content (null: not there). */
  read(file: string): { revision: number; content: string | null } {
    let fd: number;
    try {
      fd = fs.openSync(file, 'r');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      const record = this.records.get(file);
      return { revision: record && record.seen === null ? record.revision : this.next(file, null), content: null };
    }
    let content: string;
    let now: Seen;
    try {
      content = fs.readFileSync(fd, 'utf8');
      // Size and time of what was read (a rename meanwhile replaces the file, not this one).
      const { size, mtimeMs } = fs.fstatSync(fd);
      now = { size, mtimeMs };
    } finally {
      fs.closeSync(fd);
    }
    const record = this.records.get(file);
    return { revision: record && same(record.seen, now) ? record.revision : this.next(file, now), content };
  }

  /** Writes `content` over the file if `revision` is its current one; the new revision. */
  write(file: string, content: string, revision: number, backup: boolean): number {
    const now = seen(file);
    const record = this.records.get(file);
    if (!record) throw new StateConflict(`${file} has not been read here`);
    if (record.revision !== revision)
      throw new StateConflict(`${file} is at revision ${record.revision}, not ${revision}`);
    if (!same(record.seen, now)) throw new StateConflict(`${file} changed since revision ${revision}`);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const temp = `${file}.${process.pid}.tmp`;
    try {
      const fd = fs.openSync(temp, 'w');
      try {
        fs.writeSync(fd, content);
        fs.fsyncSync(fd);
      } finally {
        fs.closeSync(fd);
      }
      if (backup && now) fs.copyFileSync(file, `${file}.bak`);
      renameWithRetry(temp, file);
    } catch (error) {
      fs.rmSync(temp, { force: true });
      // What is on disk now is not known to be what was read: the next write must read it again.
      this.records.delete(file);
      throw error;
    }
    return this.next(file, seen(file));
  }
}

// On Windows a scanner or indexer can hold the target open for a moment.
export function renameWithRetry(from: string, to: string): void {
  for (let attempt = 0; ; attempt++) {
    try {
      fs.renameSync(from, to);
      return;
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (attempt >= 4 || (code !== 'EPERM' && code !== 'EBUSY' && code !== 'EACCES')) throw error;
      const until = Date.now() + 20 * (attempt + 1);
      while (Date.now() < until) {
        // short synchronous back-off; saves must not interleave
      }
    }
  }
}
