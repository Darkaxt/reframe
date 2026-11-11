// The adopted Claude clients (maintenance/windows/adopt-client.ps1 writes REFRAME_CLIENT_ADOPTION, a
// reframe.client-adoption.v1 record). Each new session re-reads it, so a client adopted while reframe runs is used by
// the next session; running sessions keep the executable they started from. A binary is used only when its sha256
// matches the record; the hash is cached per path, size and modification time.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import type { Provider } from '@shared/providers';

export const CLIENT_ADOPTION_SCHEMA = 'reframe.client-adoption.v1';

export type AdoptedClient = { ok: true; path: string; version: string } | { ok: false; reason: string };

interface Verified {
  size: number;
  mtimeMs: number;
  sha256: string;
}
const verified = new Map<string, Verified>();

function sha256Of(file: string): string {
  const stat = fs.statSync(file);
  const known = verified.get(file);
  if (known && known.size === stat.size && known.mtimeMs === stat.mtimeMs) return known.sha256;
  const sha256 = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  verified.set(file, { size: stat.size, mtimeMs: stat.mtimeMs, sha256 });
  return sha256;
}

/** The provider's adopted client from the record at `recordFile`, verified by hash. */
export function adoptedClient(recordFile: string, provider: Provider): AdoptedClient {
  let record: unknown;
  try {
    record = JSON.parse(fs.readFileSync(recordFile, 'utf8').replace(/^\uFEFF/, ''));
  } catch {
    return { ok: false, reason: `client adoption record ${recordFile} cannot be read` };
  }
  const r = record as {
    schema?: unknown;
    version?: unknown;
    clients?: Record<string, { binary?: unknown; sha256?: unknown }>;
  };
  if (r?.schema !== CLIENT_ADOPTION_SCHEMA)
    return { ok: false, reason: `${recordFile} is not a client adoption record` };
  const binding = r.clients?.[provider];
  const file = binding?.binary;
  const expected = binding?.sha256;
  if (typeof file !== 'string' || !path.isAbsolute(file) || typeof expected !== 'string')
    return { ok: false, reason: `no adopted ${provider} client in ${recordFile}` };
  let actual: string;
  try {
    actual = sha256Of(file);
  } catch {
    return { ok: false, reason: `adopted ${provider} client ${file} cannot be read` };
  }
  if (actual !== expected.toLowerCase())
    return { ok: false, reason: `adopted ${provider} client ${file} changed (sha256 ${actual})` };
  return { ok: true, path: file, version: typeof r.version === 'string' ? r.version : '' };
}
