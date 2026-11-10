import path from 'node:path';

/**
 * Whether a folder may hold chat archives: absolute and, on Windows, not on the system drive. Forward slashes and
 * device paths (\\?\C:\…) spell the same drive, so the path is resolved before its drive is compared.
 */
export function isArchiveRoot(root: string, platform: NodeJS.Platform = process.platform): boolean {
  if (platform !== 'win32') return path.posix.isAbsolute(root);
  if (!path.win32.isAbsolute(root)) return false;
  return !/^(?:\\\\[?.]\\)?c:/i.test(path.win32.resolve(root));
}
