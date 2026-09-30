import * as fs from 'fs';
import * as path from 'path';

/**
 * Copy `sourceExe` to `<storageDir>/bin/<version>/<exeName>` and return that path. Older version folders are left in place
 * (a client may still be running them). A same-size existing copy is reused, because a running exe cannot be overwritten on Windows.
 */
export function copyStableExe(sourceExe: string, storageDir: string, version: string, exeName: string): string {
  if (!/^[0-9A-Za-z.+-]+$/.test(version)) throw new Error(`Unexpected extension version '${version}'.`);
  const dir = path.join(storageDir, 'bin', version);
  const target = path.join(dir, exeName);
  fs.mkdirSync(dir, { recursive: true });
  if (fs.existsSync(target) && fs.statSync(target).size === fs.statSync(sourceExe).size) return target;
  const tmp = `${target}.${process.pid}.tmp`;
  try {
    fs.copyFileSync(sourceExe, tmp);
    fs.renameSync(tmp, target);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* best effort */ }
    throw err;
  }
  return target;
}
