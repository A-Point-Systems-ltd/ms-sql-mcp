// Bundles test/*.test.ts with esbuild (they import extension sources without extensions) and runs node --test.
import { build } from 'esbuild';
import { spawnSync } from 'node:child_process';
import { readdir, rm } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(root, 'dist-test');
await rm(out, { recursive: true, force: true });

const tests = (await readdir(join(root, 'test'))).filter(f => f.endsWith('.test.ts'));
await build({
  entryPoints: tests.map(f => join(root, 'test', f)),
  outdir: out,
  bundle: true,
  platform: 'node',
  format: 'esm',
  outExtension: { '.js': '.mjs' },
  packages: 'external',
});

const files = tests.map(f => join(out, f.replace(/\.ts$/, '.mjs')));
const r = spawnSync(process.execPath, ['--test', ...files], { stdio: 'inherit' });
process.exit(r.status ?? 1);
