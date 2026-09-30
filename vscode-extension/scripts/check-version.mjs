import { readFileSync } from 'node:fs';
const csproj = readFileSync(new URL('../../MssqlMcp/MssqlMcp.csproj', import.meta.url), 'utf8');
const server = /<Version>([^<]+)<\/Version>/.exec(csproj)?.[1];
const ext = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version;
if (!server || server !== ext) {
  console.error(`Version mismatch: server csproj <Version>=${server ?? '(missing)'} vs extension package.json=${ext}`);
  process.exit(1);
}
console.log(`Versions in sync: ${ext}`);
