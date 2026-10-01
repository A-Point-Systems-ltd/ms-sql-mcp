import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildServerConnections } from '../out/connections/serverEnv.js';

const p = (name, extra = {}) => ({ name, server: 's', database: 'd', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true, ...extra });

test('only open profiles are exported and no default property exists', () => {
  const json = JSON.parse(buildServerConnections([p('a'), p('b'), p('c', { open: false })], new Map()));
  assert.deepEqual(json.map(x => x.name), ['a', 'b']);
  assert.ok(json.every(x => !('default' in x)));
});

test('flags are carried and can be forced for the explorer', () => {
  const [x] = JSON.parse(buildServerConnections([p('a', { readOnly: false, insights: true })], new Map(), { forceReadOnly: true, insights: false }));
  assert.equal(x.readOnly, true);
  assert.equal(x.insights, false);
});

test('sql profiles without a stored password are skipped and reported', async () => {
  const { missingPasswords } = await import('../out/connections/serverEnv.js');
  const profiles = [p('w'), p('s1', { auth: 'sql', user: 'u' }), p('s2', { auth: 'sql', user: 'u' }), p('s3', { auth: 'sql', user: 'u', open: false })];
  const pw = new Map([['s2', 'x']]);
  assert.deepEqual(JSON.parse(buildServerConnections(profiles, pw)).map(x => x.name), ['w', 's2']);
  assert.deepEqual(missingPasswords(profiles, pw), ['s1']);
});

test('an empty-string password counts as stored', async () => {
  const { missingPasswords } = await import('../out/connections/serverEnv.js');
  const profiles = [p('s', { auth: 'sql', user: 'u' })];
  assert.deepEqual(missingPasswords(profiles, new Map([['s', '']])), []);
});
