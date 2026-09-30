// Manual end-to-end check (not shipped): lists tables/types and describes a table on DC\DEV master,
// running the real payloads through the explorer parsers.
// Usage: RUN_DCDEV_EXT_CHECK=1 node scripts/check-explorer.mjs [path-to-MssqlMcp.exe]
import { McpStdioClient } from '../out/client/mcpStdioClient.js';
import { pick } from '../out/client/parse.js';
import { buildServerConnections } from '../out/connections/serverEnv.js';
import { CATEGORIES, parseObjectList, parseTableChildren, parseViewIndexes } from '../out/explorer/catalog.js';

if (process.env.RUN_DCDEV_EXT_CHECK !== '1') { console.log('Skipped (set RUN_DCDEV_EXT_CHECK=1).'); process.exit(0); }
const exe = process.argv[2] ?? new URL('../../MssqlMcp/bin/Release/net10.0/win-x64/MssqlMcp.exe', import.meta.url).pathname.replace(/^\/(\w:)/, '$1');
const profile = { name: 'dcdev', server: 'DC\\DEV', database: 'master', auth: 'windows', readOnly: true, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true };
const log = { info() {}, debug() {}, trace() {}, warn: (_s, m) => console.error('warn:', m), error: (_s, m) => console.error('error:', m) };
const client = new McpStdioClient(exe, {
  MSSQL_CONNECTIONS: buildServerConnections([profile], new Map(), { forceReadOnly: true, insights: false }),
  USE_INSIGHTS_LAYER: 'false', MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
}, log);
const data = async (tool, args) => { const r = await client.callTool(tool, { ...args, connection: 'dcdev' }); return pick(r, 'data') ?? r; };
try {
  await client.initialize();
  for (const def of CATEGORIES.filter(c => c.listType)) {
    try {
      const parsed = parseObjectList(await data('list_objects', { objectType: def.listType }), def);
      console.log(`${def.label}: ${parsed.length}`, parsed.slice(0, 2));
    } catch (e) { console.log(`${def.label}: ERROR ${e.message}`); }
  }
  const tables = parseObjectList(await data('list_objects', { objectType: 'Table' }), CATEGORIES[0]);
  // Prefer a table that has indexes/constraints/FKs so the parsers are exercised.
  for (const t of tables.slice(0, 40)) {
    const d = await data('describe_table', { name: `${t.schema}.${t.name}` });
    const c = parseTableChildren(d, t.schema, t.name);
    console.log(`${t.schema}.${t.name}: indexes=${c.indexes.length} fks=${c.foreignKeys.length} triggers=${c.triggers.length}`, c.indexes.slice(0, 2));
    if (c.indexes.length) break;
  }
  const views = parseObjectList(await data('list_objects', { objectType: 'View' }), CATEGORIES[1]);
  if (views[0]) console.log('view indexes', views[0].name, parseViewIndexes(await data('describe_view', { name: `${views[0].schema}.${views[0].name}` }), views[0].schema, views[0].name));
} finally { client.dispose(); }
