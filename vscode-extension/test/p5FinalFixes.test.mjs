// P5 final fix wave: the pure parts of title refresh (I1), install target names (I2), unapplied object edits (I3),
// the history "more" flag (M1), the legacy edits backup name (M2) and the set-up re-run on a target change (M7).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {
  EXISTING_TABLE_GRANT_TEXT, INSTALL_SAFETY_TEXT, LIST_TOP, historyUri, installPrompt, moreNotLoaded, parseAuditId, parseHistoryCommand,
  parseHistoryEntries, parseHistoryStatus, parseHistoryUri, runsHistorySetup, statusTargetText,
} from '../out/history/historyModel.js';
import { updatesObjectBase } from '../out/query/runScript.js';
import { TITLE_AFTER_SAVE_MESSAGE, expectedTitle, titleAction } from '../out/query/sqlDocTitles.js';
import { KEEP_EDITS_BUTTON, REPLACE_BUTTON, reopenObjectDecision, unappliedEditsPrompt } from '../out/explorer/objectEdit.js';
import { baseFile, backingFile, legacyEditsBackupName, isLegacyEditPath, isValidDocId } from '../out/query/sqlDocNames.js';

const profile = (name, over = {}) => ({
  name, server: 'DC\\DEV', database: 'Sales', auth: 'windows', readOnly: false, insights: false, open: true,
  encrypt: 'optional', trustServerCertificate: true, ...over,
});
const status = (over = {}) => ({ tableExists: false, tableCompatible: false, triggerExists: false, triggerEnabled: false, canInstall: true, ...over });
const queryAddr = title => ({ kind: 'query', id: 'aaaabbbb', title });
const objectAddr = title => ({ kind: 'object', id: '0123456789abcdef', title });
const ref = { connection: 'dev', scriptType: 'View', schema: 'dbo', name: 'vOrders' };

// ---- I1: title refresh decisions ----

test('titleAction: a query doc retitles to its bound profile after Change Connection or a profile edit', () => {
  const addr = queryAddr('Query 3 - DC∖DEV - Sales');
  const assoc = { connection: 'prod', kind: 'query' };
  assert.deepEqual(titleAction({ address: addr, assoc, profile: profile('prod', { server: 'PROD1', database: 'ClientB' }), isDirty: false }),
    { kind: 'retitle', title: 'Query 3 - PROD1 - ClientB' });
  // Same profile, database edited.
  assert.deepEqual(titleAction({ address: addr, assoc: { connection: 'dev', kind: 'query' }, profile: profile('dev', { database: 'Sales2' }), isDirty: false }),
    { kind: 'retitle', title: 'Query 3 - DC∖DEV - Sales2' });
});

test('titleAction: an up-to-date title, no binding, a removed profile or a non-Query title is left alone', () => {
  const addr = queryAddr('Query 3 - DC∖DEV - Sales');
  assert.deepEqual(titleAction({ address: addr, assoc: { connection: 'dev', kind: 'query' }, profile: profile('dev'), isDirty: false }), { kind: 'none' });
  assert.deepEqual(titleAction({ address: addr, assoc: undefined, profile: profile('dev'), isDirty: false }), { kind: 'none' });
  assert.deepEqual(titleAction({ address: addr, assoc: { connection: 'gone', kind: 'query' }, profile: undefined, isDirty: false }), { kind: 'none' });
  assert.deepEqual(titleAction({ address: queryAddr('Recovered query aaaabbbb'), assoc: { connection: 'dev', kind: 'query' }, profile: profile('dev', { server: 'X' }), isDirty: false }), { kind: 'none' });
});

test('titleAction: a dirty (or running) document is never closed: the rename waits for the next save', () => {
  const addr = queryAddr('Query 1 - DC∖DEV - Sales');
  const input = { address: addr, assoc: { connection: 'p', kind: 'query' }, profile: profile('p', { server: 'S2' }) };
  assert.deepEqual(titleAction({ ...input, isDirty: true }), { kind: 'defer', title: 'Query 1 - S2 - Sales' });
  assert.deepEqual(titleAction({ ...input, isDirty: false, running: true }), { kind: 'defer', title: 'Query 1 - S2 - Sales' });
  assert.equal(TITLE_AFTER_SAVE_MESSAGE, 'The tab title updates after you save.');
});

test('titleAction: object docs follow profile edits, but keep their title after Change Connection (rebound)', () => {
  const addr = objectAddr('dbo.vOrders - DC∖DEV - Sales');
  const assoc = { connection: 'dev', kind: 'object', object: ref, target: { server: 'DC\\DEV', database: 'Sales' } };
  assert.deepEqual(titleAction({ address: addr, assoc, profile: profile('dev', { server: 'DC\\NEW' }), isDirty: false }),
    { kind: 'retitle', title: 'dbo.vOrders - DC∖NEW - Sales' });
  assert.deepEqual(titleAction({ address: addr, assoc, profile: profile('dev'), isDirty: false }), { kind: 'none' });
  const rebound = { ...assoc, connection: 'prod', object: { ...ref, connection: 'prod' }, rebound: true };
  assert.deepEqual(titleAction({ address: addr, assoc: rebound, profile: profile('prod', { server: 'PROD1' }), isDirty: false }), { kind: 'none' });
});

test('expectedTitle: titles go through titlePart (a / in a name, an empty database)', () => {
  assert.equal(expectedTitle({ address: queryAddr('Query 2 - a - b'), assoc: { connection: 'r', kind: 'query' }, profile: profile('r', { database: '' }) }),
    'Query 2 - DC∖DEV - default');
  assert.equal(expectedTitle({
    address: objectAddr('x'), assoc: { connection: 'dev', kind: 'object', object: { ...ref, name: 'a/b' } }, profile: profile('dev'),
  }), 'dbo.a∕b - DC∖DEV - Sales');
  // The kinds must agree (a malformed binding is left alone).
  assert.equal(expectedTitle({ address: objectAddr('x'), assoc: { connection: 'dev', kind: 'query' }, profile: profile('dev') }), undefined);
});

// ---- I2: names the server reports ----

test('parseHistoryStatus reads serverName / databaseName; blank ones are left out', () => {
  assert.deepEqual(parseHistoryStatus({ tableExists: true, serverName: ' SRV\\A ', databaseName: 'ClientB' }), {
    tableExists: true, tableCompatible: false, triggerExists: false, triggerEnabled: false, canInstall: false, serverName: 'SRV\\A', databaseName: 'ClientB',
  });
  const blank = parseHistoryStatus({ serverName: '  ', databaseName: 3 });
  assert.equal('serverName' in blank, false);
  assert.equal('databaseName' in blank, false);
});

test('statusTargetText: the server-reported names first, the profile only for what is missing', () => {
  const raw = profile('r', { auth: 'raw', server: '', database: '', rawConnectionString: 'not parseable' });
  assert.equal(statusTargetText({ serverName: 'SRV1', databaseName: 'ClientB' }, raw), 'SRV1\\ClientB');
  assert.equal(statusTargetText({}, raw), 'connection string\\default');
  assert.equal(statusTargetText({}, profile('a')), 'DC\\DEV\\Sales');
  assert.equal(statusTargetText({ serverName: 'SRV1' }, profile('a', { database: '' })), 'SRV1\\default');
  assert.equal(statusTargetText({ databaseName: 'Real' }, profile('a')), 'DC\\DEV\\Real');
});

test('installPrompt detail names DDL_Audit_Writer, the rollback-by-another-trigger exception and how to remove it (RI1)', () => {
  const p = installPrompt(status(), 'SRV1/ClientB', 'dev');
  assert.equal(p.message, "Create DDL history on SRV1/ClientB (connection 'dev')?");
  assert.equal(INSTALL_SAFETY_TEXT,
    'The trigger runs as the low-privilege user DDL_Audit_Writer (INSERT/SELECT on dbo.DDL_AuditLog only). If logging fails, the DDL statement '
    + 'still runs and is not logged, except when another trigger on DDL_AuditLog rolls back. To remove: DROP TRIGGER [DDL_Audit] ON DATABASE; DROP USER [DDL_Audit_Writer].');
  assert.ok(p.detail.endsWith(INSTALL_SAFETY_TEXT), p.detail);
  assert.doesNotMatch(p.detail, /harmless|runs as dbo/i);
  assert.doesNotMatch(p.detail, /grants INSERT and SELECT on the existing/, 'no existing table: no grant sentence');
});

test('installPrompt: an existing table gets the grant sentence (RI1), and status warnings are appended (RM4)', () => {
  const p = installPrompt(status({ tableExists: true, tableCompatible: true, warnings: ['CommandText is varchar(max); non-Latin text in DDL will be stored lossy.'] }), 'S/D', 'c');
  assert.equal(EXISTING_TABLE_GRANT_TEXT, 'This grants INSERT and SELECT on the existing dbo.DDL_AuditLog to the new user DDL_Audit_Writer.');
  assert.ok(p.detail.includes(`${EXISTING_TABLE_GRANT_TEXT} ${INSTALL_SAFETY_TEXT}`), p.detail);
  assert.ok(p.detail.endsWith(`${INSTALL_SAFETY_TEXT} CommandText is varchar(max); non-Latin text in DDL will be stored lossy.`), p.detail);
  assert.doesNotMatch(p.detail, /table dbo\.DDL_AuditLog and/, 'the table is not created');
});

test('parseHistoryStatus reads warnings (strings only), and leaves them out when there are none (RM4)', () => {
  assert.deepEqual(parseHistoryStatus({ warnings: ['a', '', 3, ' b '] }).warnings, ['a', ' b ']);
  assert.equal('warnings' in parseHistoryStatus({ warnings: [] }), false);
  assert.equal('warnings' in parseHistoryStatus({}), false);
});

test('parseAuditId: bigint-safe ids from numbers or digit strings; unsafe or malformed ones are rejected (NET-020)', () => {
  assert.equal(parseAuditId(3000000001), 3000000001);
  assert.equal(parseAuditId('3000000001'), 3000000001);
  assert.equal(parseAuditId(' 42 '), 42);
  assert.equal(parseAuditId(Number.MAX_SAFE_INTEGER), Number.MAX_SAFE_INTEGER);
  for (const bad of [Number.MAX_SAFE_INTEGER + 1, '9007199254740993', -1, 1.5, 'x', '', '12345678901234567', null, undefined, NaN]) {
    assert.equal(parseAuditId(bad), undefined, String(bad));
  }
  assert.deepEqual(parseHistoryEntries([{ id: 3000000001, postTime: 't' }, { id: '3000000002', postTime: 't' }]).map(e => e.id), [3000000001, 3000000002]);
  assert.equal(parseHistoryCommand({ id: '3000000001', commandText: 'x' }).id, 3000000001);
  const uri = historyUri({ kind: 'entry', connection: 'c', target: 'S/D', id: 12345678901, label: 'dbo.p #12345678901' });
  assert.equal(parseHistoryUri(uri).id, 12345678901, 'more than 10 digits round-trips');
});

test('updatesObjectBase: only a whole-document run without errors (RM3)', () => {
  const ok = { resultSets: [], messages: [], hadErrors: false, batches: 1, elapsedMs: 1 };
  assert.equal(updatesObjectBase(ok, true), true);
  assert.equal(updatesObjectBase(ok, false), false, 'selection run');
  assert.equal(updatesObjectBase({ ...ok, hadErrors: true }, true), false, 'hadErrors');
  assert.equal(updatesObjectBase({ ...ok, messages: [{ kind: 'error', text: 'Msg 1', line: 1 }] }, true), false, 'an error message');
  assert.equal(updatesObjectBase({ ...ok, batches: 0 }, true), false, 'nothing ran');
});

// ---- I3: unapplied object edits ----

test('reopenObjectDecision: asks only when saved edits differ from the base, or when it cannot tell', () => {
  const text = t => ({ kind: 'text', text: t });
  assert.equal(reopenObjectDecision({ kind: 'missing' }, { kind: 'missing' }), 'load');
  assert.equal(reopenObjectDecision({ kind: 'missing' }, text('x')), 'load');
  assert.equal(reopenObjectDecision(text('ALTER VIEW v AS SELECT 1'), text('ALTER VIEW v AS SELECT 1')), 'load');
  assert.equal(reopenObjectDecision(text('a\r\nb'), text('a\nb')), 'load', 'line endings are ignored');
  assert.equal(reopenObjectDecision(text('ALTER VIEW v AS SELECT 2'), text('ALTER VIEW v AS SELECT 1')), 'ask');
  assert.equal(reopenObjectDecision(text('edited'), { kind: 'missing' }), 'ask', 'no base: written by an older build');
  assert.equal(reopenObjectDecision({ kind: 'unknown' }, text('x')), 'ask');
  assert.equal(reopenObjectDecision(text('x'), { kind: 'unknown' }), 'ask');
});

test('the unapplied-edits modal text and buttons', () => {
  assert.equal(unappliedEditsPrompt('dbo.vOrders'), 'You have saved, unapplied edits to dbo.vOrders. Replace them with the current server version?');
  assert.equal(REPLACE_BUTTON, 'Replace');
  assert.equal(KEEP_EDITS_BUTTON, 'Keep my edits');
});

test('baseFile: <root>/sqldocs/object/<id>.base.sql, never a backing file name, and ids are validated', () => {
  const root = path.join('C:', 'storage');
  assert.equal(baseFile(root, '0123456789abcdef'), path.join(root, 'sqldocs', 'object', '0123456789abcdef.base.sql'));
  assert.notEqual(baseFile(root, '0123456789abcdef'), backingFile(root, 'object', '0123456789abcdef'));
  assert.equal(isValidDocId('0123456789abcdef.base'), false, 'listBacking skips base files');
  assert.throws(() => baseFile(root, '../x'));
});

// ---- M1 ----

test('moreNotLoaded uses the raw listed count, not the filtered one', () => {
  assert.equal(moreNotLoaded(LIST_TOP), true);
  assert.equal(moreNotLoaded(LIST_TOP - 1), false);
  assert.equal(moreNotLoaded(0), false);
});

// ---- M2 ----

test('legacyEditsBackupName: edits.old-<yyyyMMddHHmmss> in local time, outside the legacy edits path', () => {
  assert.equal(legacyEditsBackupName(new Date(2026, 9, 1, 7, 5, 9)), 'edits.old-20261001070509');
  const root = path.join('C:', 'storage');
  assert.equal(isLegacyEditPath(path.join(root, legacyEditsBackupName(new Date()), 'a.sql'), root), false);
});

// ---- M7 ----

test('runsHistorySetup: an edit that moves the connection to another server or database re-runs the set-up', () => {
  const before = profile('a', { ddlHistory: true });
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: true, database: 'Other' }), before), true, 'database changed');
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: true, server: 'srv2' }), before), true, 'server changed');
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: true, server: 'dc\\dev ' }), before), false, 'same target (case, spaces)');
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: false, database: 'Other' }), before), false, 'turned off');
  assert.equal(runsHistorySetup(true, profile('a', { ddlHistory: true, database: 'Other' })), false, 'no previous profile');
});

// ---- Fix round 3 ----

test('installDecision: a status that cannot install on a read/write connection and says why gets warnBlocked, not the modal (NET-022)', async () => {
  const { installDecision } = await import('../out/history/historyModel.js');
  const conflict = 'A user named DDL_Audit_Writer already exists with more rights than INSERT/SELECT on dbo.DDL_AuditLog; nothing was created. Remove its extra rights or drop it, then retry.';
  assert.equal(installDecision(status({ canInstall: false, warnings: [conflict] }), false), 'warnBlocked');
  assert.equal(installDecision(status({ canInstall: false, warnings: [conflict] }), true), 'warnReadOnly', 'read-only stays read-only');
  assert.equal(installDecision(status({ canInstall: false }), false), 'warnReadOnly', 'no reason given');
  assert.equal(installDecision(status({ canInstall: true, warnings: ['lossy'] }), false), 'confirmInstall', 'a lossy note does not block');
  assert.equal(installDecision(status({ tableExists: true, tableCompatible: false, canInstall: false, warnings: [conflict] }), false), 'warnIncompatible');
});

test('loggingSuppressed: parsed only when true, and an empty list then says why (NET-022)', async () => {
  const { LOGGING_SUPPRESSED_WARNING, emptyHistoryOutcome } = await import('../out/history/historyModel.js');
  assert.equal(LOGGING_SUPPRESSED_WARNING, 'dbo.DDL_AuditLog has triggers, so DDL_Audit does not record changes.');
  assert.equal(parseHistoryStatus({ loggingSuppressed: true }).loggingSuppressed, true);
  assert.equal('loggingSuppressed' in parseHistoryStatus({ loggingSuppressed: false }), false);
  const on = { tableExists: true, tableCompatible: true, triggerExists: true, triggerEnabled: true, canInstall: false };
  assert.equal(emptyHistoryOutcome({ ...on, loggingSuppressed: true }), 'loggingSuppressed');
  assert.equal(emptyHistoryOutcome(on), 'noHistory');
  assert.equal(emptyHistoryOutcome({ ...on, triggerEnabled: false, loggingSuppressed: true }), 'triggerDisabled');
  assert.equal(emptyHistoryOutcome({ ...on, triggerExists: false, loggingSuppressed: true }), 'triggerMissing');
});

test('resultsToCarry: a retitled document takes over the old results unless it already has its own (R2M1)', async () => {
  const { resultsToCarry } = await import('../out/query/sqlDocTitles.js');
  const old = { kind: 'done' };
  assert.equal(resultsToCarry(old, undefined), old);
  assert.equal(resultsToCarry(old, { kind: 'running' }), undefined);
  assert.equal(resultsToCarry(undefined, undefined), undefined);
});
