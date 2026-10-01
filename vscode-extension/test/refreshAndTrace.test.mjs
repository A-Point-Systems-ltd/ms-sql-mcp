import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { buildConnectionsFile, planConnectionsFileRefresh, refreshConnectionsFileOnDisk } from '../out/register/connectionsFile.js';
import { traceablePayload } from '../out/client/parse.js';

const p = (name, extra = {}) => ({ name, server: 's', database: 'd', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true, ...extra });
const sql = (name, extra = {}) => p(name, { auth: 'sql', user: 'u', ...extra });
const names = json => JSON.parse(json).map(x => x.name);

function tempFile(initialProfiles) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mssqlmcp-refresh-'));
  const file = path.join(dir, 'connections.json');
  fs.writeFileSync(file, buildConnectionsFile(initialProfiles, new Map(), false).json);
  return file;
}

test('narrowing changes are always written: read-only, close', () => {
  const prev = buildConnectionsFile([sql('a'), p('w')], new Map(), false).json;
  const tightened = planConnectionsFileRefresh([sql('a'), p('w', { readOnly: true })], new Map(), false, prev);
  assert.equal(tightened.action, 'write');
  assert.deepEqual(tightened.pending, []);
  assert.equal(JSON.parse(tightened.json).find(x => x.name === 'w').readOnly, true);

  const closed = planConnectionsFileRefresh([sql('a'), p('w', { open: false })], new Map(), false, prev);
  assert.equal(closed.action, 'write');
  assert.deepEqual(names(closed.json), ['a']);
});

test('a profile that needs a new env var is left out and reported; the rest is written', () => {
  const prev = buildConnectionsFile([sql('a')], new Map(), false).json;
  const plan = planConnectionsFileRefresh([sql('a'), sql('b')], new Map(), false, prev);
  assert.equal(plan.action, 'write');
  assert.deepEqual(names(plan.json), ['a']);
  assert.deepEqual(plan.pending, [{ name: 'b', envVar: 'MSSQLMCP_PWD_B' }]);
  assert.doesNotMatch(plan.json, /MSSQLMCP_PWD_B/);
});

test('an unreadable previous file counts as referencing no variables', () => {
  const plan = planConnectionsFileRefresh([sql('a'), p('w')], new Map(), false, undefined);
  assert.deepEqual(names(plan.json), ['w']);
  assert.deepEqual(plan.pending.map(x => x.name), ['a']);
});

test('no connections left means delete', () => {
  const prev = buildConnectionsFile([p('w')], new Map(), false).json;
  assert.equal(planConnectionsFileRefresh([p('w', { open: false })], new Map(), false, prev).action, 'delete');
  // Only a pending profile left: still nothing usable.
  const only = planConnectionsFileRefresh([p('w', { open: false }), sql('b')], new Map(), false, prev);
  assert.equal(only.action, 'delete');
  assert.deepEqual(only.pending.map(x => x.name), ['b']);
});

test('with passwords written to the file nothing is pending', () => {
  const prev = buildConnectionsFile([p('w')], new Map(), false).json;
  const plan = planConnectionsFileRefresh([p('w'), sql('b')], new Map([['b', 'pw']]), true, prev);
  assert.deepEqual(names(plan.json), ['w', 'b']);
  assert.deepEqual(plan.pending, []);
});

test('on disk: closing the last open connection removes connections.json', () => {
  const file = tempFile([p('w')]);
  const r = refreshConnectionsFileOnDisk(file, [p('w', { open: false })], new Map(), false);
  assert.equal(r.action, 'delete');
  assert.equal(fs.existsSync(file), false);
});

test('on disk: close while a new placeholder profile is pending rewrites without both', () => {
  const file = tempFile([p('w'), p('x')]);
  const r = refreshConnectionsFileOnDisk(file, [p('w'), p('x', { open: false }), sql('b')], new Map(), false);
  assert.equal(r.action, 'write');
  assert.deepEqual(names(fs.readFileSync(file, 'utf8')), ['w']);
  assert.deepEqual(r.pending.map(x => x.name), ['b']);
});

test('on disk: missing file is never created', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mssqlmcp-refresh-'));
  const file = path.join(dir, 'connections.json');
  assert.equal(refreshConnectionsFileOnDisk(file, [p('w')], new Map(), false).action, 'none');
  assert.equal(fs.existsSync(file), false);
});

test('read_data results are traced as a row count only', () => {
  const payload = { success: true, data: [{ Name: 'Dana Cohen' }, { Name: 'x' }], truncated: true, maxRows: 2 };
  const t = traceablePayload('read_data', payload);
  assert.deepEqual(t, { rowCount: 2, truncated: true });
  assert.ok(!JSON.stringify(t).includes('Dana'));
  assert.deepEqual(traceablePayload('read_data', { Success: true, Data: [] }), { rowCount: 0, truncated: false });
  assert.deepEqual(traceablePayload('read_data', undefined), { rowCount: 0, truncated: false });
});

test('other tool results are traced unchanged', () => {
  const payload = { success: true, data: { ddl: 'CREATE TABLE' } };
  assert.equal(traceablePayload('script_object', payload), payload);
});

test('run_script results are traced as counts only, never rows or message text', () => {
  const payload = {
    success: true,
    data: {
      resultSets: [
        { batch: 1, columns: [{ name: 'Name', type: 'nvarchar' }], rows: [['Dana'], ['Avi']], rowCount: 2, truncated: false },
        { batch: 2, columns: [{ name: 'Id', type: 'int' }], rows: [[1]], rowCount: 5, truncated: true },
      ],
      messages: [{ kind: 'rows', text: '(2 rows affected)', line: null }, { kind: 'info', text: 'secret Dana', line: 3 }],
      hadErrors: false, batches: 2, elapsedMs: 12,
    },
  };
  const t = traceablePayload('run_script', payload);
  assert.deepEqual(t, { resultSets: 2, rows: 3, messages: 2, hadErrors: false });
  assert.ok(!JSON.stringify(t).includes('Dana'));
  assert.deepEqual(traceablePayload('run_script', { Success: true, Data: { HadErrors: true } }), { resultSets: 0, rows: 0, messages: 0, hadErrors: true });
  assert.deepEqual(traceablePayload('run_script', undefined), { resultSets: 0, rows: 0, messages: 0, hadErrors: false });
});
