// Manual end-to-end check (not shipped): runs the Data View editing scripts (column metadata, UPDATE with the
// concurrency guard, DELETE, INSERT with defaults, rollback on a concurrent change) through the real server's
// run_script against a throw-away table in tempdb. Prints only counts and outcomes.
// Usage: RUN_EDIT_CHECK=1 [EDIT_CHECK_SERVER=(localdb)\MSSQLLocalDB] node scripts/check-editing.mjs [path-to-MssqlMcp.exe]
import assert from 'node:assert/strict';
import { McpStdioClient } from '../out/client/mcpStdioClient.js';
import { buildServerConnections, explorerProcessEnv } from '../out/connections/serverEnv.js';
import { applyEdit, buildSaveScript, columnMetaSql, deleteRows, emptyPending, parseColumnMeta, writableColumns } from '../out/grid/editModel.js';
import { dataViewSql, stripRowNumber } from '../out/grid/gridModel.js';
import { parseRunScriptResult } from '../out/query/runScript.js';

if (process.env.RUN_EDIT_CHECK !== '1') { console.log('Skipped (set RUN_EDIT_CHECK=1).'); process.exit(0); }
const exe = process.argv[2] ?? new URL('../bin/MssqlMcp.exe', import.meta.url).pathname.replace(/^\/(\w:)/, '$1');
const server = process.env.EDIT_CHECK_SERVER || '(localdb)\\MSSQLLocalDB';
const profile = { name: 'edit', server, database: 'tempdb', auth: 'windows', readOnly: false, insights: false, open: true, encrypt: 'optional', trustServerCertificate: true };
const log = { info() {}, debug() {}, trace() {}, warn: (_s, m) => console.error('warn:', m), error: (_s, m) => console.error('error:', m) };
const client = new McpStdioClient(exe, explorerProcessEnv(buildServerConnections([profile], new Map()), { MSSQL_SCRIPT_RUNNER: 'true' }), log);
const T = { schema: 'dbo', name: 'apms_edit_check' };
const run = async script => parseRunScriptResult(await client.callTool('run_script', { connection: 'edit', script, maxRows: 1000 }));
const errors = r => r.messages.filter(m => m.kind === 'error').map(m => m.text);
const read = async () => stripRowNumber((await run(dataViewSql(T, 100, undefined, { pk: ['Id'] }))).resultSets[0]);

try {
  await client.initialize();
  await run(`IF OBJECT_ID(N'dbo.apms_edit_check') IS NOT NULL DROP TABLE dbo.apms_edit_check;
CREATE TABLE dbo.apms_edit_check (Id int IDENTITY PRIMARY KEY, Name nvarchar(50) NOT NULL, Active bit NOT NULL DEFAULT 1,
  At datetime NULL, Amount decimal(10,2) NULL, Notes ntext NULL, Calc AS (Id * 2));
INSERT dbo.apms_edit_check (Name, Active, At, Amount, Notes) VALUES (N'Acme', 1, '2024-01-02T10:00:00.003', 10.5, N'n1'), (N'Beta', 0, NULL, NULL, NULL), (N'Gamma', 1, NULL, 1, NULL);`);
  const meta = parseColumnMeta((await run(columnMetaSql(T))).resultSets[0].rows);
  let set = await read();
  console.log('columns', set.columns.map(c => `${c.name}:${c.type}`).join(' '), '| writable', writableColumns(set.columns, meta).join(','));
  assert.deepEqual(writableColumns(set.columns, meta), [false, true, true, true, true, true, false]);

  // Update (with the datetime round trip), delete, insert with defaults.
  const p = emptyPending();
  applyEdit(p, set.rows, 0, 1, "Acme's");
  applyEdit(p, set.rows, 0, 3, '2025-05-01 13:45');
  applyEdit(p, set.rows, 0, 4, '99.95');
  applyEdit(p, set.rows, 1, 2, 'true');
  deleteRows(p, set.rows.length, [2]);
  p.inserts.push([undefined, 'Delta', undefined, undefined, undefined, 'note', undefined]);
  const save = buildSaveScript(T, set.columns, set.rows, ['Id'], p);
  const r1 = await run(save.script);
  assert.deepEqual(errors(r1), []);
  set = await read();
  console.log('after save:', set.rows.map(r => `${r[0]}|${r[1]}|${r[2]}|${r[3]}|${r[4]}|${r[6]}`).join('  '));
  assert.deepEqual(set.rows.map(r => r[1]), ["Acme's", 'Beta', 'Delta']);
  assert.equal(set.rows[2][2], true, 'bit default applied');
  assert.equal(String(set.rows[0][4]), '99.95');

  // Concurrent change: another session edits row 1; the pending edit of the same column is refused and nothing is saved.
  const stale = emptyPending();
  applyEdit(stale, set.rows, 0, 1, 'Mine');
  applyEdit(stale, set.rows, 1, 1, 'Beta2');
  await run(`UPDATE dbo.apms_edit_check SET Name = N'Theirs' WHERE Id = ${set.rows[0][0]}`);
  const r2 = await run(buildSaveScript(T, set.columns, set.rows, ['Id'], stale).script);
  console.log('concurrent change error:', errors(r2)[0]?.split('\n')[0]);
  assert.match(errors(r2).join(' '), /Row 1 was changed or deleted by someone else/);
  const after = await read();
  assert.deepEqual(after.rows.map(r => r[1]), ['Theirs', 'Beta', 'Delta'], 'rolled back: row 2 unchanged too');

  // A bad value fails the whole save (XACT_ABORT) and keeps the table unchanged.
  const bad = emptyPending();
  applyEdit(bad, after.rows, 1, 1, 'Beta3');
  applyEdit(bad, after.rows, 2, 3, 'not a date');
  const r3 = await run(buildSaveScript(T, after.columns, after.rows, ['Id'], bad).script);
  console.log('bad value error:', errors(r3)[0]?.split('\n')[0]);
  assert.ok(errors(r3).length);
  assert.equal((await read()).rows[1][1], 'Beta');
  console.log('OK');
} finally {
  await run('IF OBJECT_ID(N\'dbo.apms_edit_check\') IS NOT NULL DROP TABLE dbo.apms_edit_check;').catch(() => {});
  client.dispose?.();
  process.exit(process.exitCode ?? 0);
}
