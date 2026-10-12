import * as fs from 'fs';
import * as path from 'path';
import { McpEntry, hasServerKey, mergeMcpServer } from './jsonMerge';
import { backupPath } from './naming';

/** Write `text` through a sibling temp file and a rename, so readers never see a half-written file. */
export function atomicWriteFile(target: string, text: string): void {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const tmp = `${target}.${process.pid}.${Date.now()}.tmp`;
  try {
    fs.writeFileSync(tmp, text, 'utf8');
    fs.renameSync(tmp, target);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* best effort */ }
    throw err;
  }
}

export interface ClientWriteResult { path: string; backup?: string; removedLegacy?: boolean }

/**
 * Merge `entry` into the client config at `configPath`. The merge runs first, so invalid existing JSON
 * throws before anything is written. An existing file is copied to `<path>.<yyyyMMddHHmmss>.bak` (kept) first.
 * `legacyKey` (the pre-rename server key) is dropped in the same atomic write; `removedLegacy` reports that it was there.
 */
export function writeClientConfig(configPath: string, key: string, entry: McpEntry, now: Date = new Date(), legacyKey?: string): ClientWriteResult {
  const existing = fs.existsSync(configPath) ? fs.readFileSync(configPath, 'utf8') : undefined;
  const merged = mergeMcpServer(existing, key, entry, legacyKey);
  const removedLegacy = !!legacyKey && legacyKey !== key && hasServerKey(existing, legacyKey);
  const backup = existing !== undefined ? backupFile(configPath, now) : undefined;
  atomicWriteFile(configPath, merged);
  return { path: configPath, backup, removedLegacy };
}

/** Copies an existing file to `<path>.<yyyyMMddHHmmss>.bak` (a free name; kept) and returns the backup path. */
export function backupFile(configPath: string, now: Date = new Date()): string {
  for (let attempt = 0; ; attempt++) {
    const candidate = backupPath(configPath, now, attempt);
    try {
      fs.copyFileSync(configPath, candidate, fs.constants.COPYFILE_EXCL);
      return candidate;
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code !== 'EEXIST' || attempt >= 99) throw err;
    }
  }
}

/** Replaces a client config with `text`, after a timestamped backup of the existing file. */
export function replaceClientConfig(configPath: string, text: string, now: Date = new Date()): ClientWriteResult {
  const backup = fs.existsSync(configPath) ? backupFile(configPath, now) : undefined;
  atomicWriteFile(configPath, text);
  return { path: configPath, backup };
}
