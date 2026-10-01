import { test } from 'node:test';
import assert from 'node:assert/strict';
import { URI } from 'vscode-uri';
import {
  HISTORY_SCHEME, NO_EARLIER_TEXT, AUDIT_OBJECT_TYPES, emptyHistoryOutcome, filterEntries, incompatibleWarning, latestDefinitionEntry,
  triggerMissingWarning, currentDefinitionSql, currentDiffTitle, entryDiffTitle, formatPostTime, historyDocKeys,
  historyPickItems, historyUri, installDecision, installPrompt, isModuleType, isNotInstalledError, parseHistoryCommand,
  parseHistoryEntries, parseHistoryStatus, parseHistoryUri, previousEntry, runsHistorySetup, supportsHistory, targetText,
} from '../out/history/historyModel.js';
import { sqlString } from '../out/explorer/sqlText.js';
import { traceablePayload } from '../out/client/parse.js';
import { describeNode } from '../out/explorer/treeModel.js';
import { CATEGORIES } from '../out/explorer/catalog.js';
import { buildServerConnections, definitionVersion } from '../out/connections/serverEnv.js';

const status = (over = {}) => ({ tableExists: true, tableCompatible: true, triggerExists: true, triggerEnabled: true, canInstall: false, ...over });
const entry = (id, postTime, over = {}) => ({ id, postTime, loginName: 'DOM\\dana', hostName: 'PC1', programName: 'SSMS', eventType: 'ALTER_PROCEDURE', objectType: 'PROCEDURE', schemaName: 'dbo', length: 120, ...over });
const profile = (name, over = {}) => ({ name, server: 'DC\\DEV', database: 'Sales', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'mandatory', trustServerCertificate: true, ...over });

test('sqlString: N-quoted literal with doubled single quotes', () => {
  assert.equal(sqlString('dbo.p'), "N'dbo.p'");
  assert.equal(sqlString("O'Brien''s"), "N'O''Brien''''s'");
  assert.equal(sqlString(''), "N''");
  assert.equal(sqlString('[x]]y]'), "N'[x]]y]'");
});

test('currentDefinitionSql: OBJECT_DEFINITION of the bracketed name; database triggers by name', () => {
  assert.equal(currentDefinitionSql({ connection: 'c', scriptType: 'StoredProcedure', schema: 'dbo', name: "it's" }),
    "SELECT OBJECT_DEFINITION(OBJECT_ID(N'[dbo].[it''s]')) AS d");
  assert.equal(currentDefinitionSql({ connection: 'c', scriptType: 'View', schema: 's]x', name: 'v' }),
    "SELECT OBJECT_DEFINITION(OBJECT_ID(N'[s]]x].[v]')) AS d");
  const db = currentDefinitionSql({ connection: 'c', scriptType: 'DatabaseTrigger', name: "t'1" });
  assert.match(db, /^SELECT m\.definition AS d FROM sys\.triggers t JOIN sys\.sql_modules m ON m\.object_id = t\.object_id WHERE t\.parent_class = 0 AND t\.name = N't''1'$/);
});

test('history types and module types', () => {
  for (const t of ['Table', 'View', 'StoredProcedure', 'TableFunction', 'ScalarFunction', 'TableTrigger', 'DatabaseTrigger', 'Type']) assert.ok(supportsHistory(t), t);
  for (const t of ['Index', 'ForeignKey', 'Login', 'DatabaseUser', '', undefined]) assert.equal(supportsHistory(t), false, String(t));
  for (const t of ['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction', 'TableTrigger', 'DatabaseTrigger']) assert.ok(isModuleType(t), t);
  for (const t of ['Table', 'Type', 'Index']) assert.equal(isModuleType(t), false, t);
});

test('parseHistoryStatus: case-insensitive booleans, missing ones are false', () => {
  assert.deepEqual(parseHistoryStatus({ TableExists: true, tableCompatible: true, TRIGGEREXISTS: true, triggerEnabled: false, canInstall: true }),
    { tableExists: true, tableCompatible: true, triggerExists: true, triggerEnabled: false, canInstall: true });
  assert.deepEqual(parseHistoryStatus(undefined), { tableExists: false, tableCompatible: false, triggerExists: false, triggerEnabled: false, canInstall: false });
});

test('installDecision: every table x trigger x read-only combination (canInstall as the server computes it)', () => {
  const tables = { missing: { tableExists: false, tableCompatible: false }, compatible: { tableExists: true, tableCompatible: true }, incompatible: { tableExists: true, tableCompatible: false } };
  const triggers = { missing: { triggerExists: false, triggerEnabled: false }, enabled: { triggerExists: true, triggerEnabled: true }, disabled: { triggerExists: true, triggerEnabled: false } };
  const expected = {
    'missing/missing': ['confirmInstall', 'warnReadOnly'],
    'missing/enabled': ['confirmInstall', 'warnReadOnly'],
    'missing/disabled': ['confirmInstall', 'warnReadOnly'],
    'compatible/missing': ['confirmInstall', 'warnReadOnly'],
    'compatible/enabled': ['none', 'none'],
    'compatible/disabled': ['warnDisabled', 'warnDisabled'],
    'incompatible/missing': ['warnIncompatible', 'warnIncompatible'],
    'incompatible/enabled': ['warnIncompatible', 'warnIncompatible'],
    'incompatible/disabled': ['warnIncompatible', 'warnIncompatible'],
  };
  for (const [tk, t] of Object.entries(tables)) {
    for (const [gk, g] of Object.entries(triggers)) {
      for (const readOnly of [false, true]) {
        // The server's formula (DdlAudit.GetStatusAsync).
        const canInstall = !readOnly && (!t.tableExists || (t.tableCompatible && !g.triggerExists));
        const got = installDecision({ ...t, ...g, canInstall }, readOnly);
        assert.equal(got, expected[`${tk}/${gk}`][readOnly ? 1 : 0], `${tk}/${gk} readOnly=${readOnly}`);
      }
    }
  }
});

test('installDecision: confirmInstall only when the server reports canInstall', () => {
  assert.equal(installDecision(status({ tableExists: false, tableCompatible: false, canInstall: false }), false), 'warnReadOnly');
  assert.equal(installDecision(status({ triggerExists: false, triggerEnabled: false, canInstall: false }), false), 'warnReadOnly');
  assert.equal(installDecision(status({ tableExists: false, tableCompatible: false, canInstall: true }), false), 'confirmInstall');
  assert.equal(installDecision(status({ tableExists: false, tableCompatible: false, canInstall: true }), true), 'warnReadOnly');
});

test('incompatible-table and missing-trigger texts', () => {
  assert.equal(incompatibleWarning('S/D'), 'dbo.DDL_AuditLog on S/D does not have the columns the DDL_Audit trigger writes, so DDL history cannot be set up here. Ask a DBA to align or rename the existing table.');
  assert.equal(triggerMissingWarning('S/D'), 'The DDL_Audit trigger is not installed on S/D, so changes are not recorded.');
});

test('emptyHistoryOutcome: missing trigger, disabled trigger, or simply nothing recorded', () => {
  assert.equal(emptyHistoryOutcome(status({ triggerExists: false, triggerEnabled: false })), 'triggerMissing');
  assert.equal(emptyHistoryOutcome(status({ tableExists: false, triggerExists: false, triggerEnabled: false })), 'triggerMissing');
  assert.equal(emptyHistoryOutcome(status({ triggerEnabled: false })), 'triggerDisabled');
  assert.equal(emptyHistoryOutcome(status()), 'noHistory');
});

test('filterEntries: by audit ObjectType (null kept); database triggers only without a schema', () => {
  assert.deepEqual(AUDIT_OBJECT_TYPES.StoredProcedure, ['PROCEDURE']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.TableFunction, ['FUNCTION']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.ScalarFunction, ['FUNCTION']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.View, ['VIEW']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.Table, ['TABLE']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.TableTrigger, ['TRIGGER']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.DatabaseTrigger, ['TRIGGER']);
  assert.deepEqual(AUDIT_OBJECT_TYPES.Type, ['TYPE']);
  const all = [
    entry(6, 'a', { objectType: 'PROCEDURE' }),
    entry(5, 'b', { objectType: 'TABLE' }),
    entry(4, 'c', { objectType: undefined }),
    entry(3, 'd', { objectType: 'procedure' }),
    entry(2, 'e', { objectType: 'VIEW' }),
  ];
  assert.deepEqual(filterEntries(all, 'StoredProcedure').map(e => e.id), [6, 4, 3]);
  assert.deepEqual(filterEntries(all, 'Table').map(e => e.id), [5, 4]);
  assert.deepEqual(filterEntries(all, 'View').map(e => e.id), [4, 2]);
  assert.deepEqual(filterEntries(all, 'Unknown').map(e => e.id), [6, 5, 4, 3, 2]);
  const trg = [
    entry(9, 'a', { objectType: 'TRIGGER', schemaName: undefined }),
    entry(8, 'b', { objectType: 'TRIGGER', schemaName: '' }),
    entry(7, 'c', { objectType: 'TRIGGER', schemaName: 'dbo' }),
    entry(6, 'd', { objectType: 'TABLE', schemaName: undefined }),
  ];
  assert.deepEqual(filterEntries(trg, 'DatabaseTrigger').map(e => e.id), [9, 8]);
  assert.deepEqual(filterEntries(trg, 'TableTrigger').map(e => e.id), [9, 8, 7]);
});

test('latestDefinitionEntry: the newest non-DROP entry; the compare item needs one', () => {
  const entries = [entry(9, 'a', { eventType: 'DROP_PROCEDURE' }), entry(8, 'b', { eventType: 'ALTER_PROCEDURE' }), entry(7, 'c', { eventType: 'CREATE_PROCEDURE' })];
  assert.equal(latestDefinitionEntry(entries).id, 8);
  assert.equal(latestDefinitionEntry([entry(1, 'a', { eventType: 'drop_view' })]), undefined);
  assert.equal(latestDefinitionEntry([entry(1, 'a', { eventType: undefined })]).id, 1);
  assert.equal(historyPickItems([entry(1, 'a', { eventType: 'DROP_VIEW' })], true).length, 1, 'no compare item with only a DROP');
  assert.equal(historyPickItems(entries, true)[0].action.kind, 'current');
});

test('installPrompt names server/db and lists only what is missing', () => {
  const both = installPrompt(status({ tableExists: false, triggerExists: false, triggerEnabled: false }), 'DC\\DEV/Sales', 'dev');
  assert.equal(both.message, "Create DDL history on DC\\DEV/Sales (connection 'dev')?");
  assert.match(both.detail, /table dbo\.DDL_AuditLog/);
  assert.match(both.detail, /database trigger DDL_Audit/);
  assert.match(both.detail, /The trigger records every DDL change in this database\./);
  const table = installPrompt(status({ tableExists: false }), 'x/y', 'c');
  assert.match(table.detail, /table dbo\.DDL_AuditLog/);
  assert.doesNotMatch(table.detail, /database trigger DDL_Audit/);
  const trig = installPrompt(status({ triggerExists: false, triggerEnabled: false }), 'x/y', 'c');
  assert.doesNotMatch(trig.detail, /table dbo\.DDL_AuditLog/);
  assert.match(trig.detail, /database trigger DDL_Audit/);
});

test('targetText: server/db from the profile, raw connection strings parsed', () => {
  assert.equal(targetText(profile('a')), 'DC\\DEV/Sales');
  assert.equal(targetText(profile('r', { auth: 'raw', server: '', database: '', rawConnectionString: 'Data Source=S1;Initial Catalog=D1' })), 'S1/D1');
});

test('runsHistorySetup: only when ddlHistory is on and new or toggled on (relative to the form baseline)', () => {
  assert.equal(runsHistorySetup(undefined, profile('a', { ddlHistory: true })), true, 'new');
  assert.equal(runsHistorySetup(undefined, profile('a')), false, 'new without');
  assert.equal(runsHistorySetup(false, profile('a', { ddlHistory: true })), true, 'toggled on');
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: true })), false, 'unchanged');
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: false })), false, 'toggled off');
});

test('parseHistoryEntries: case-insensitive fields, rows without a numeric id are dropped', () => {
  const rows = parseHistoryEntries([
    { Id: 7, PostTime: '2026-03-05T14:07:09.123', LoginName: 'u', HostName: 'h', ProgramName: 'p', EventType: 'CREATE_TABLE', ObjectType: 'TABLE', SchemaName: 'dbo', Length: 42 },
    { id: 'x' },
    { id: 3, postTime: '2026-01-01T00:00:00.000', loginName: null, length: null },
  ]);
  assert.equal(rows.length, 2);
  assert.deepEqual(rows[0], { id: 7, postTime: '2026-03-05T14:07:09.123', loginName: 'u', hostName: 'h', programName: 'p', eventType: 'CREATE_TABLE', objectType: 'TABLE', schemaName: 'dbo', length: 42 });
  assert.equal(rows[1].loginName, undefined);
  assert.equal(rows[1].length, undefined);
  assert.deepEqual(parseHistoryEntries(undefined), []);
});

test('parseHistoryCommand: command text (empty string when null)', () => {
  assert.deepEqual(parseHistoryCommand({ Id: 5, PostTime: 't', CommandText: 'ALTER PROC p AS SELECT 1' }), { id: 5, postTime: 't', commandText: 'ALTER PROC p AS SELECT 1' });
  assert.equal(parseHistoryCommand({ id: 5, commandText: null }).commandText, '');
});

test('formatPostTime: dd/MM/yyyy HH:mm:ss from the server text, no time zone shift', () => {
  assert.equal(formatPostTime('2026-03-05T14:07:09.123'), '05/03/2026 14:07:09');
  assert.equal(formatPostTime('2026-12-31T23:59:59'), '31/12/2026 23:59:59');
  assert.equal(formatPostTime('2026-12-31T23:59:59.000+02:00'), '31/12/2026 23:59:59');
  assert.equal(formatPostTime('2026-12-31 08:00:01'), '31/12/2026 08:00:01');
  assert.equal(formatPostTime('soon'), 'soon');
  assert.equal(formatPostTime(''), '(no time)');
});

test('historyPickItems: newest first entries; modules start with Compare with current', () => {
  const entries = [entry(9, '2026-03-05T14:07:09.123'), entry(4, '2026-01-02T03:04:05.000', { loginName: undefined, hostName: undefined, programName: undefined, length: undefined, eventType: 'CREATE_PROCEDURE' })];
  const items = historyPickItems(entries, true);
  assert.equal(items.length, 3);
  assert.deepEqual(items[0].action, { kind: 'current' });
  assert.equal(items[0].label, '$(diff) Compare latest recorded version with the current definition');
  assert.equal(items[1].label, '05/03/2026 14:07:09 · ALTER_PROCEDURE');
  assert.equal(items[1].description, 'DOM\\dana');
  assert.equal(items[1].detail, 'PC1 · SSMS · 120 chars');
  assert.deepEqual(items[1].action, { kind: 'entry', index: 0 });
  assert.equal(items[2].label, '02/01/2026 03:04:05 · CREATE_PROCEDURE');
  assert.deepEqual(items[2].action, { kind: 'entry', index: 1 });
  assert.equal(typeof items[2].description, 'string');
  assert.equal(items[2].detail, '? · ? · ? chars');
  const table = historyPickItems(entries, false);
  assert.equal(table.length, 2);
  assert.ok(table.every(i => i.action.kind === 'entry'));
  assert.deepEqual(historyPickItems([], true), []);
});

test('previousEntry: the next older entry; the oldest has none', () => {
  const entries = [entry(9, 'a'), entry(4, 'b'), entry(1, 'c')];
  assert.equal(previousEntry(entries, 0).id, 4);
  assert.equal(previousEntry(entries, 1).id, 1);
  assert.equal(previousEntry(entries, 2), undefined);
  assert.equal(previousEntry(entries, 7), undefined);
});

test('diff titles', () => {
  const entries = [entry(9, '2026-03-05T14:07:09.123'), entry(4, '2026-01-02T03:04:05.000', { loginName: 'avi' })];
  assert.equal(entryDiffTitle('dbo.p', entries, 0), 'dbo.p: 02/01/2026 03:04:05 ↔ 05/03/2026 14:07:09 (DOM\\dana)');
  assert.equal(entryDiffTitle('dbo.p', entries, 1), 'dbo.p: (none) ↔ 02/01/2026 03:04:05 (avi)');
  assert.equal(currentDiffTitle('dbo.p', entries[0]), 'dbo.p: 05/03/2026 14:07:09 ↔ current');
});

test('history uri: encode / parse round-trip, also through vscode-uri', () => {
  const refs = [
    { kind: 'entry', connection: 'dev&x=1#%~', target: 'DC\\DEV/Sales', id: 42, label: 'dbo.p #42' },
    { kind: 'empty', label: 'dbo.p (none)', more: false },
    { kind: 'empty', label: 'a/b?c', more: true },
    { kind: 'current', connection: 'c', target: 's/d', object: { connection: 'c', scriptType: 'StoredProcedure', schema: 'd b?', name: "n'&=%" }, label: 'x', nonce: 'r1' },
    { kind: 'current', connection: 'c', target: 's,1433/d?', object: { connection: 'c', scriptType: 'DatabaseTrigger', name: 'trg' }, label: 'trg', nonce: '2' },
  ];
  for (const ref of refs) {
    const s = historyUri(ref);
    assert.ok(s.startsWith(`${HISTORY_SCHEME}:/`), s);
    const expected = { ...ref, label: undefined };
    const strip = r => ({ ...r, label: undefined });
    assert.deepEqual(strip(parseHistoryUri(s)), expected, s);
    const u = URI.parse(s);
    assert.equal(u.scheme, HISTORY_SCHEME);
    assert.ok(u.path.endsWith('.sql'), u.path);
    assert.equal(u.fragment, '');
    assert.deepEqual(strip(parseHistoryUri(u.toString())), expected, 'toString');
    assert.deepEqual(strip(parseHistoryUri(u.toString(true))), expected, 'toString(true)');
  }
  // Same entry id, another server/db (an edited connection): another document, so no cached text is reused.
  const a = historyUri({ kind: 'entry', connection: 'c', target: 'S1/D', id: 1, label: 'x' });
  const b = historyUri({ kind: 'entry', connection: 'c', target: 'S2/D', id: 1, label: 'x' });
  assert.notEqual(URI.parse(a).toString(), URI.parse(b).toString());
  assert.equal(parseHistoryUri('mssql-history:/x.sql?v=entry&c=a&i=abc'), undefined);
  assert.equal(parseHistoryUri('mssql-history:/x.sql?v=entry&c=a&i=-1'), undefined);
  assert.equal(parseHistoryUri('mssql-history:/x.sql?v=nope'), undefined);
  assert.equal(parseHistoryUri('mssql-history:/x.sql?v=current&c=a&n=x'), undefined, 'no type');
});

test('the empty document text', () => {
  assert.equal(NO_EARLIER_TEXT, '-- No earlier version recorded');
});

test('isNotInstalledError matches the server text', () => {
  assert.ok(isNotInstalledError('DDL history is not installed on this database (dbo.DDL_AuditLog is missing).'));
  assert.equal(isNotInstalledError('Login failed'), false);
});

test('historyDocKeys: object docs and DDL docs of history connections with a history type', () => {
  const profiles = [profile('on', { ddlHistory: true }), profile('off'), profile('Closed', { ddlHistory: true, open: false })];
  const proc = (c, over = {}) => ({ connection: c, scriptType: 'StoredProcedure', schema: 'dbo', name: 'p', ...over });
  const objects = [
    ['mssql-sql:/~sql/object/1/a', { connection: 'ON', kind: 'object', object: proc('ON') }],
    ['mssql-sql:/~sql/object/2/b', { connection: 'off', kind: 'object', object: proc('off') }],
    ['mssql-sql:/~sql/query/3/c', { connection: 'on', kind: 'query' }],
    ['mssql-sql:/~sql/object/4/d', { connection: 'gone', kind: 'object', object: proc('gone') }],
    ['mssql-sql:/~sql/object/5/e', { connection: 'closed', kind: 'object', object: proc('closed') }],
  ];
  const ddl = [
    ['mssql-ddl:/on/Table/x', proc('on', { scriptType: 'Table' })],
    ['mssql-ddl:/on/Index/x', proc('on', { scriptType: 'Index' })],
    ['mssql-ddl:/off/Table/x', proc('off', { scriptType: 'Table' })],
  ];
  // 'closed' has ddlHistory but is closed: no runner can serve it, so no button.
  assert.deepEqual(historyDocKeys(objects, ddl, profiles), ['mssql-sql:/~sql/object/1/a', 'mssql-ddl:/on/Table/x']);
});

test('tree: object nodes of history connections get the .history contextValue suffix', () => {
  const def = id => CATEGORIES.find(c => c.id === id);
  const obj = (id, scriptType) => ({ kind: 'object', def: def(id), ref: { connection: 'c', scriptType, schema: 'dbo', name: 'x' } });
  assert.equal(describeNode(obj('tables', 'Table'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.table.history');
  assert.equal(describeNode(obj('views', 'View'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.view.history');
  assert.equal(describeNode(obj('procedures', 'StoredProcedure'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.StoredProcedure.history');
  assert.equal(describeNode(obj('types', 'Type'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.Type.history');
  assert.equal(describeNode(obj('dbTriggers', 'DatabaseTrigger'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.DatabaseTrigger.history');
  assert.equal(describeNode(obj('users', 'DatabaseUser'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.DatabaseUser');
  assert.equal(describeNode(obj('tables', 'Table')).contextValue, 'msSqlMcp.obj.table');
  assert.equal(describeNode(obj('tables', 'Table'), undefined, { history: false }).contextValue, 'msSqlMcp.obj.table');
  const child = scriptType => ({ kind: 'child', ref: { connection: 'c', scriptType, schema: 'dbo', name: 'k' } });
  assert.equal(describeNode(child('TableTrigger'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.child.history');
  assert.equal(describeNode(child('Index'), undefined, { history: true }).contextValue, 'msSqlMcp.obj.child');
});

test('ddl_history results are traced as counts only, never command text or logins', () => {
  const list = { success: true, data: [entry(1, 't'), entry(2, 't')] };
  const tl = traceablePayload('ddl_history', list);
  assert.deepEqual(tl, { entries: 2 });
  const get = { success: true, data: { id: 5, postTime: 't', loginName: 'DOM\\dana', commandText: 'ALTER PROC secret AS SELECT 1' } };
  const tg = traceablePayload('ddl_history', get);
  assert.deepEqual(tg, { id: 5, commandTextLength: 29 });
  assert.ok(!JSON.stringify(tg).includes('secret') && !JSON.stringify(tg).includes('dana'));
  const st = { success: true, data: status() };
  assert.equal(traceablePayload('ddl_history', st), st);
});

test('ddlHistory is not part of the agent server env nor of the definition version', () => {
  const s = { insights: true, allowAdhocConnections: false, serverPath: '' };
  const off = [profile('a')];
  const on = [profile('a', { ddlHistory: true })];
  assert.equal(definitionVersion('1.0.0', on, s, new Map()), definitionVersion('1.0.0', off, s, new Map()));
  assert.equal(buildServerConnections(on, new Map()), buildServerConnections(off, new Map()));
  assert.ok(!buildServerConnections(on, new Map()).includes('ddlHistory'));
});

test('package.json: Show DDL History menus match the history contextValues and the per-resource key', async () => {
  const { readFileSync } = await import('node:fs');
  const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  const menus = pkg.contributes.menus;
  assert.ok(pkg.contributes.commands.some(c => c.command === 'msSqlMcp.showHistory' && c.title === 'Show DDL History' && c.icon === '$(history)'));
  const regexOf = when => new RegExp(when.match(/viewItem =~ \/(.*)\/$/)[1]);
  const tree = menus['view/item/context'].find(m => m.command === 'msSqlMcp.showHistory');
  const re = regexOf(tree.when);
  for (const v of ['msSqlMcp.obj.table.history', 'msSqlMcp.obj.StoredProcedure.history', 'msSqlMcp.obj.child.history']) assert.ok(re.test(v), v);
  for (const v of ['msSqlMcp.obj.table', 'msSqlMcp.obj.child', 'msSqlMcp.conn.open']) assert.equal(re.test(v), false, v);
  const data = regexOf(menus['view/item/context'].find(m => m.command === 'msSqlMcp.dataView').when);
  for (const v of ['msSqlMcp.obj.table', 'msSqlMcp.obj.view.history']) assert.ok(data.test(v), v);
  assert.equal(data.test('msSqlMcp.obj.StoredProcedure.history'), false);
  assert.ok(menus['editor/title'].some(m => m.command === 'msSqlMcp.showHistory' && m.when === 'resource in msSqlMcp.historyDocs'));
});
