import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildConnectionsFile, refreshConfirmationReason } from '../out/register/connectionsFile.js';
import { traceablePayload } from '../out/client/parse.js';

const p = (name, extra = {}) => ({ name, server: 's', database: 'd', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true, ...extra });
const sql = (name, extra = {}) => p(name, { auth: 'sql', user: 'u', ...extra });

test('refresh with the same env vars and a non-empty file writes silently', () => {
  const prev = buildConnectionsFile([sql('a'), p('w')], new Map(), false).json;
  const next = buildConnectionsFile([sql('a'), p('w', { readOnly: true })], new Map(), false);
  assert.equal(refreshConfirmationReason(prev, next), undefined);
});

test('refresh that needs a new env var asks for confirmation and names it', () => {
  const prev = buildConnectionsFile([sql('a')], new Map(), false).json;
  const next = buildConnectionsFile([sql('a'), sql('b')], new Map(), false);
  const reason = refreshConfirmationReason(prev, next);
  assert.ok(reason);
  assert.match(reason, /MSSQLMCP_PWD_B/);
  assert.doesNotMatch(reason, /MSSQLMCP_PWD_A/);
});

test('refresh that would write an empty file asks for confirmation', () => {
  const prev = buildConnectionsFile([p('w')], new Map(), false).json;
  const next = buildConnectionsFile([p('w', { open: false })], new Map(), false);
  assert.equal(JSON.parse(next.json).length, 0);
  assert.match(refreshConfirmationReason(prev, next), /no connections/i);
});

test('dropping an env var is fine; an unreadable previous file counts as having none', () => {
  const prev = buildConnectionsFile([sql('a'), sql('b')], new Map(), false).json;
  assert.equal(refreshConfirmationReason(prev, buildConnectionsFile([sql('a')], new Map(), false)), undefined);
  assert.match(refreshConfirmationReason(undefined, buildConnectionsFile([sql('a')], new Map(), false)), /MSSQLMCP_PWD_A/);
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
