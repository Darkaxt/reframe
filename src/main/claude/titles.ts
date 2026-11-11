// Claude Code's own name for a session: the latest custom-title (from --name or /rename) or ai-title line of its
// transcript. Claude Code appends these again and again, so the end of the file has the current one; only the end is
// read, and only again when the file grew. Transcripts are never written here.
import fs from 'node:fs';
import path from 'node:path';

/** How much of a transcript's end is read for its title. */
const TAIL_BYTES = 512 * 1024;

/** The latest title in transcript lines (JSONL), or null. A custom title and an AI title count alike: the later wins. */
export function latestTitle(text: string): string | null {
  const lines = text.split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i]!;
    if (!line.includes('"custom-title"') && !line.includes('"ai-title"')) continue;
    try {
      const entry = JSON.parse(line) as { type?: unknown; customTitle?: unknown; aiTitle?: unknown };
      const title =
        entry.type === 'custom-title' ? entry.customTitle : entry.type === 'ai-title' ? entry.aiTitle : undefined;
      if (typeof title === 'string' && title.trim()) return title.trim();
    } catch {
      // a line cut by the tail, or one being written
    }
  }
  return null;
}

async function findTranscript(home: string, sessionId: string): Promise<string | null> {
  const projects = path.join(home, 'projects');
  let dirs: string[];
  try {
    dirs = await fs.promises.readdir(projects);
  } catch {
    return null;
  }
  for (const dir of dirs) {
    const file = path.join(projects, dir, `${sessionId}.jsonl`);
    if (
      await fs.promises.stat(file).then(
        () => true,
        () => false,
      )
    )
      return file;
  }
  return null;
}

async function readRange(file: string, start: number, end: number): Promise<string> {
  const handle = await fs.promises.open(file, 'r');
  try {
    const buffer = Buffer.alloc(end - start);
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, start);
    return buffer.subarray(0, bytesRead).toString('utf8');
  } finally {
    await handle.close();
  }
}

/** Titles per Claude session id, read one transcript at a time. */
export class TranscriptTitles {
  private known = new Map<string, { file: string; size: number; title: string | null }>();
  private queue: Promise<unknown> = Promise.resolve();

  /** What was read last for the session, without reading. */
  cached(sessionId: string): string | null | undefined {
    return this.known.get(sessionId)?.title;
  }

  read(home: string, sessionId: string): Promise<string | null> {
    const next = this.queue.then(() => this.readNow(home, sessionId));
    this.queue = next.catch(() => null);
    return next.catch(() => this.known.get(sessionId)?.title ?? null);
  }

  private async readNow(home: string, sessionId: string): Promise<string | null> {
    const prior = this.known.get(sessionId);
    const file = prior?.file ?? (await findTranscript(home, sessionId));
    if (!file) return null;
    const size = await fs.promises.stat(file).then(
      (s) => s.size,
      () => null,
    );
    if (size === null) {
      this.known.delete(sessionId);
      return null;
    }
    if (prior && prior.size === size) return prior.title;
    // Grown: only what was added (a title there is newer); otherwise the end of the file.
    const grown = prior && size > prior.size;
    const start = grown ? Math.max(prior.size, size - TAIL_BYTES) : Math.max(0, size - TAIL_BYTES);
    let text = await readRange(file, start, size);
    if (start > 0 && !grown) text = text.slice(text.indexOf('\n') + 1);
    const title = latestTitle(text) ?? (grown ? prior.title : null);
    this.known.set(sessionId, { file, size, title });
    return title;
  }
}
