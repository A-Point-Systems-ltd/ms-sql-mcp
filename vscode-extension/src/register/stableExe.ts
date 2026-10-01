import * as fs from 'fs';
import * as path from 'path';

/**
 * Copy `sourceExe` to `<storageDir>/bin/<version>/<exeName>` and return that path. Older version folders are left in place
 * (a client may still be running them). An existing copy with the same size and modification time is reused, because a running exe cannot be overwritten on Windows.
 */
export function copyStableExe(sourceExe: string, storageDir: string, version: string, exeName: string): string {
  if (!/^[0-9A-Za-z.+-]+$/.test(version)) throw new Error(`Unexpected extension version '${version}'.`);
  const dir = path.join(storageDir, 'bin', version);
  const target = path.join(dir, exeName);
  fs.mkdirSync(dir, { recursive: true });
  const src = fs.statSync(sourceExe);
  if (fs.existsSync(target)) {
    const dst = fs.statSync(target);
    if (dst.size === src.size && Math.abs(dst.mtimeMs - src.mtimeMs) < 2) return target;
  }
  const tmp = `${target}.${process.pid}.tmp`;
  try {
    fs.copyFileSync(sourceExe, tmp);
    fs.utimesSync(tmp, src.atime, src.mtime);
    fs.renameSync(tmp, target);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* best effort */ }
    throw err;
  }
  return target;
}
