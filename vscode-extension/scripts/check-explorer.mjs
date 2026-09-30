// Manual end-to-end check (not shipped): lists every category, describes a table/view, scripts DDL and runs a
// Data View preview on DC\DEV master, running the real payloads through the explorer parsers / tree model.
// Prints only counts and DDL first lines - never row data (privacy).
// Usage: RUN_DCDEV_EXT_CHECK=1 node scripts/check-explorer.mjs [path-to-MssqlMcp.exe]
import { McpStdioClient } from '../out/client/mcpStdioClient.js';
import { pick } from '../out/client/parse.js';
import { buildServerConnections } from '../out/connections/serverEnv.js';
import { CATEGORIES, parseObjectList, parseTableChildren, parseViewIndexes } from '../out/explorer/catalog.js';
import { childNodes, dataViewRequest, ddlText, parseChildren, scriptArgs } from '../out/explorer/treeModel.js';
import { parseReadData, rowsToTable } from '../out/dataTable.js';

if (process.env.RUN_DCDEV_EXT_CHECK !== '1') { console.log('Skipped (set RUN_DCDEV_EXT_CHECK=1).'); process.exit(0); }
const exe = process.argv[2] ?? new URL('../../MssqlMcp/bin/Release/net10.0/win-x64/MssqlMcp.exe', import.meta.url).pathname.replace(/^\/(\w:)/, '$1');
const profile = { name: 'dcdev', server: 'DC\\DEV', database: 'master', auth: 'windows', readOnly: true, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true };
const log = { info() {}, debug() {}, trace() {}, warn: (_s, m) => console.error('warn:', m), error: (_s, m) => console.error('error:', m) };
const client = new McpStdioClient(exe, {
  MSSQL_CONNECTIONS: buildServerConnections([profile], new Map(), { forceReadOnly: true, insights: false }),
  USE_INSIGHTS_LAYER: 'false', MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
}, log);
const full = (tool, args) => client.callTool(tool, { ...args, connection: 'dcdev' });
const data = async (tool, args) => { const r = await full(tool, args); return pick(r, 'data') ?? r; };
// First CREATE/ALTER line (skips SET options and comments), so the CREATE OR ALTER vs ALTER form is visible.
const firstLine = text => { const lines = text.split(/\r?\n/); return lines.find(l => /^\s*(CREATE|ALTER)\b/i.test(l)) ?? lines.find(l => l.trim() && !l.startsWith('--')) ?? ''; };
const ddl = async (label, ref) => {
  try {
    const text = ddlText(await data('script_object', scriptArgs(ref)), 'dcdev');
    console.log(`DDL ${label}: ${firstLine(text)}  [${text.split('\n').length} lines]`);
    return text;
  } catch (e) { console.log(`DDL ${label}: ERROR ${e.message}`); return ''; }
};
try {
  await client.initialize();
  for (const def of CATEGORIES.filter(c => c.listType)) {
    try {
      const parsed = parseObjectList(await data('list_objects', { objectType: def.listType }), def);
      console.log(`${def.label}: ${parsed.length}`, parsed.slice(0, 2));
    } catch (e) { console.log(`${def.label}: ERROR ${e.message}`); }
  }
  const cat = id => CATEGORIES.find(c => c.id === id);
  const tables = parseObjectList(await data('list_objects', { objectType: 'Table' }), cat('tables'));
  // Prefer a table that has indexes/constraints/FKs so the parsers are exercised.
  let table;
  for (const t of tables.slice(0, 40)) {
    const d = await data('describe_table', { name: `${t.schema}.${t.name}` });
    const c = parseTableChildren(d, t.schema, t.name);
    console.log(`${t.schema}.${t.name}: indexes=${c.indexes.length} fks=${c.foreignKeys.length} triggers=${c.triggers.length}`, c.indexes.slice(0, 2));
    table ??= t;
    if (c.indexes.length) { table = t; break; }
  }
  const views = parseObjectList(await data('list_objects', { objectType: 'View' }), cat('views'));
  if (views[0]) console.log('view indexes', views[0].name, parseViewIndexes(await data('describe_view', { name: `${views[0].schema}.${views[0].name}` }), views[0].schema, views[0].name));

  // DDL through the same argument builder / text formatter the DDL documents use.
  if (table) {
    await ddl(`table ${table.schema}.${table.name}`, { connection: 'dcdev', scriptType: 'Table', schema: table.schema, name: table.name });
    const kids = parseChildren(cat('tables'), await data('describe_table', { name: `${table.schema}.${table.name}` }), table.schema, table.name);
    for (const folder of ['indexes', 'foreignKeys', 'triggers']) {
      const [k] = childNodes('dcdev', folder, kids);
      if (k) await ddl(`${k.ref.scriptType} ${k.ref.name}`, k.ref);
    }
  }
  // Data View: dataViewRequest (TOP n+1, maxRows n) + read_data on every table/view until one returns rows; counts only.
  const preview = async (kind, o) => {
    try {
      const r = parseReadData(await full('read_data', dataViewRequest(o, 500)));
      const grid = rowsToTable(r.rows);
      console.log(`read_data ${kind} ${o.schema}.${o.name}: rows=${grid.data.length} columns=${grid.columns.length} truncated=${r.truncated}`);
      if (grid.data.length > 1) {
        // Same request with a cap below the row count: the server must report truncation.
        const cap = grid.data.length - 1;
        const c = parseReadData(await full('read_data', dataViewRequest(o, cap)));
        console.log(`read_data ${kind} ${o.schema}.${o.name} (Data View rows=${cap}): rows=${c.rows.length} truncated=${c.truncated} maxRows=${c.maxRows}`);
      }
      return grid.data.length;
    } catch (e) { console.log(`read_data ${kind} ${o.schema}.${o.name}: ERROR ${e.message.split('\n')[0]}`); return 0; }
  };
  for (const o of tables) if (await preview('table', o)) break;
  for (const o of views) {
    await ddl(`view ${o.schema}.${o.name}`, { connection: 'dcdev', scriptType: 'View', schema: o.schema, name: o.name });
    if (await preview('view', o)) break;
  }
  const t = parseReadData(await full('read_data', { sql: 'SELECT TOP (3) name FROM sys.objects', maxRows: 2 }));
  console.log(`read_data truncation probe (TOP 3, maxRows 2): rows=${t.rows.length} truncated=${t.truncated} maxRows=${t.maxRows}`);
  for (const [id, type] of [['procedures', 'StoredProcedure'], ['tvfs', 'TableFunction'], ['scalars', 'ScalarFunction'], ['dbTriggers', 'DatabaseTrigger'], ['types', 'Type']]) {
    const [o] = parseObjectList(await data('list_objects', { objectType: cat(id).listType }), cat(id));
    if (o) await ddl(`${type} ${o.schema ? `${o.schema}.` : ''}${o.name}`, { connection: 'dcdev', scriptType: type, ...(o.schema ? { schema: o.schema } : {}), name: o.name });
    else console.log(`DDL ${type}: none in master`);
  }
  // Login DDL: report only placeholder/hash checks, not the (personal) login name.
  const [login] = parseObjectList(await data('list_objects', { objectType: 'Login' }), cat('logins')).filter(l => l.detail?.startsWith('S') || l.detail?.startsWith('SQL'));
  if (login) {
    const text = ddlText(await data('script_object', scriptArgs({ connection: 'dcdev', scriptType: 'Login', name: login.name })), 'dcdev');
    console.log(`DDL SQL login: CREATE LOGIN=${/CREATE LOGIN/i.test(text)} passwordPlaceholder=${/PASSWORD\s*=/i.test(text)} hash=${/0x0[12]00[0-9A-F]{8}/i.test(text) || /HASHED/i.test(text)}`);
  } else console.log('DDL SQL login: none visible');
} finally { client.dispose(); }
