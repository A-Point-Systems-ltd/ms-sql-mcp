import * as fs from 'fs';
import * as path from 'path';
import { McpEntry, mergeMcpServer } from './jsonMerge';
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

export interface ClientWriteResult { path: string; backup?: string }

/**
 * Merge `entry` into the client config at `configPath`. The merge runs first, so invalid existing JSON
 * throws before anything is written. An existing file is copied to `<path>.<yyyyMMddHHmmss>.bak` (kept) first.
 */
export function writeClientConfig(configPath: string, key: string, entry: McpEntry, now: Date = new Date()): ClientWriteResult {
  const existing = fs.existsSync(configPath) ? fs.readFileSync(configPath, 'utf8') : undefined;
  const merged = mergeMcpServer(existing, key, entry);
  let backup: string | undefined;
  if (existing !== undefined) {
    backup = backupPath(configPath, now);
    fs.copyFileSync(configPath, backup, fs.constants.COPYFILE_EXCL);
  }
  atomicWriteFile(configPath, merged);
  return { path: configPath, backup };
}
