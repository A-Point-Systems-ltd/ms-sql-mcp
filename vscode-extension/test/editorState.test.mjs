import { test } from 'node:test';
import assert from 'node:assert/strict';
import { editorRunState, findProfile } from '../out/query/editorState.js';

const profile = (over = {}) => ({ name: 'dev', server: 'DC\\DEV', database: 'db1', auth: 'windows', readOnly: false, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true, ...over });
const query = (connection = 'dev') => ({ connection, kind: 'query' });
const object = (connection = 'dev') => ({ connection, kind: 'object', object: { connection, scriptType: 'View', schema: 'dbo', name: 'v1' } });

test('unassociated editor: not connected, "Connect" status', () => {
  const s = editorRunState(undefined, undefined);
  assert.equal(s.connected, false);
  assert.equal(s.canRun, false);
  assert.equal(s.blockedReadOnly, false);
  assert.equal(s.statusText, '$(database) Connect');
  assert.match(s.tooltip, /^MSSQL-MCP: /);
  // A profile without an association changes nothing.
  assert.deepEqual(editorRunState(undefined, profile()), s);
});

test('read-write query document can run', () => {
  const s = editorRunState(query(), profile());
  assert.deepEqual(
    { connected: s.connected, canRun: s.canRun, blockedReadOnly: s.blockedReadOnly, statusText: s.statusText, tooltip: s.tooltip },
    { connected: true, canRun: true, blockedReadOnly: false, statusText: '$(database) dev', tooltip: 'MSSQL-MCP: dev - DC\\DEV/db1 - read-write. Click to change.' });
  assert.equal(s.reason, undefined);
});

test('read-only query document can run (read-only SELECT batches only) and shows a lock', () => {
  const s = editorRunState(query(), profile({ readOnly: true }));
  assert.equal(s.connected, true);
  assert.equal(s.canRun, true);
  assert.equal(s.blockedReadOnly, false);
  assert.equal(s.statusText, '$(database) dev $(lock)');
  assert.equal(s.tooltip, 'MSSQL-MCP: dev - DC\\DEV/db1 - read-only. Click to change.');
});

test('read-write object document can run', () => {
  const s = editorRunState(object(), profile());
  assert.equal(s.canRun, true);
  assert.equal(s.blockedReadOnly, false);
});

test('object document on a read-only profile is blocked', () => {
  const s = editorRunState(object(), profile({ readOnly: true }));
  assert.equal(s.connected, true);
  assert.equal(s.canRun, false);
  assert.equal(s.blockedReadOnly, true);
  assert.equal(s.statusText, '$(database) dev $(lock)');
  assert.match(s.tooltip, /^MSSQL-MCP: dev - DC\\DEV\/db1 - read-only\. .*read-write connection.* Click to change\.$/);
  assert.match(s.reason, /read-only/);
});

test('removed connection: connected but cannot run; tooltip says it was removed', () => {
  const s = editorRunState(query('gone'), undefined);
  assert.equal(s.connected, true);
  assert.equal(s.canRun, false);
  assert.equal(s.blockedReadOnly, false);
  assert.equal(s.statusText, '$(database) gone');
  assert.match(s.tooltip, /connection was removed/);
  assert.match(s.reason, /connection was removed/);
  // Also for an object document.
  assert.equal(editorRunState(object('gone'), undefined).blockedReadOnly, false);
});

test('closed connection: connected but cannot run; tooltip says to open it first', () => {
  const s = editorRunState(query(), profile({ open: false }));
  assert.equal(s.connected, true);
  assert.equal(s.canRun, false);
  assert.equal(s.blockedReadOnly, false);
  assert.match(s.tooltip, /open the connection first/);
  assert.match(s.reason, /open the connection first/);
  // Closed wins over the read-only object block.
  const o = editorRunState(object(), profile({ open: false, readOnly: true }));
  assert.equal(o.canRun, false);
  assert.equal(o.blockedReadOnly, false);
  assert.match(o.tooltip, /open the connection first/);
});

test('raw profiles show "connection string" instead of server/database', () => {
  const s = editorRunState(query(), profile({ auth: 'raw', rawConnectionString: 'Server=x', server: '', database: '' }));
  assert.equal(s.tooltip, 'MSSQL-MCP: dev - connection string - read-write. Click to change.');
});

test('the profile name wins over the stored association casing', () => {
  assert.equal(editorRunState(query('DEV'), profile()).statusText, '$(database) dev');
});

test('findProfile matches names case-insensitively', () => {
  const list = [profile(), profile({ name: 'prod' })];
  assert.equal(findProfile(list, 'PROD').name, 'prod');
  assert.equal(findProfile(list, 'nope'), undefined);
  assert.equal(findProfile(list, undefined), undefined);
});
