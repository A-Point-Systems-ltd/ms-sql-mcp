// Bundles each view entry into ONE self-contained HTML file (inline script, no external URLs) and copies it
// into MssqlMcp/Apps/, where the server embeds it as a ui:// resource.
import { build } from 'esbuild';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const serverApps = join(root, '..', '..', 'MssqlMcp', 'Apps');
const views = ['connections'];

await mkdir(join(root, 'dist'), { recursive: true });
await mkdir(serverApps, { recursive: true });

for (const view of views) {
  const result = await build({
    entryPoints: [join(root, 'src', `${view}.ts`)],
    bundle: true,
    format: 'iife',
    target: 'es2022',
    minify: true,
    write: false,
    legalComments: 'none',
  });
  const js = result.outputFiles[0].text.replace(/<\/script/gi, '<\\/script');
  const shell = await readFile(join(root, 'src', `${view}.html`), 'utf8');
  if (!shell.includes('<!--SCRIPT-->')) {
    throw new Error(`${view}.html must contain <!--SCRIPT-->`);
  }

  const html = shell.replace('<!--SCRIPT-->', () => `<script>${js}</script>`);
  if (/\b(src|href)\s*=\s*["']https?:/i.test(html)) {
    throw new Error(`${view}.html references an external URL; MCP Apps views must be self-contained.`);
  }

  await writeFile(join(root, 'dist', `${view}.html`), html);
  await writeFile(join(serverApps, `${view}.html`), html);
  console.log(`${view}.html: ${(html.length / 1024).toFixed(1)} KiB`);
}
