import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  canBringOnline, databaseLabel, defaultFormValues, describeDatabaseList, formToProbeProfile, isDatabaseUnavailableError, isOnline,
  parseDatabaseList, parseFormMessage, stateCacheKey, stateOf,
} from '../out/connections/connectionFormModel.js';
import { renderConnectionForm } from '../out/connections/connectionFormHtml.js';
import { TOON_TOOLS, withJsonRows } from '../out/client/mcpStdioClient.js';

const dbs = [{ name: 'master', state: 'ONLINE' }, { name: 'Sales', state: 'OFFLINE' }, { name: 'Restore_Me', state: 'RESTORING' }];

test('databaseLabel marks databases that are not online', () => {
  assert.equal(databaseLabel(dbs[0]), 'master');
  assert.equal(databaseLabel(dbs[1]), 'Sales (offline)');
  assert.equal(databaseLabel(dbs[2]), 'Restore_Me (restoring)');
  assert.equal(databaseLabel({ name: 'X', state: 'RECOVERY_PENDING' }), 'X (recovery pending)');
});

test('only OFFLINE can be brought online; ONLINE is online in any case', () => {
  assert.equal(canBringOnline('OFFLINE'), true);
  assert.equal(canBringOnline('offline'), true);
  assert.equal(canBringOnline('RESTORING'), false);
  assert.equal(canBringOnline(undefined), false);
  assert.equal(isOnline('online'), true);
  assert.equal(isOnline(null), false);
});

test('describeDatabaseList counts databases that are not online', () => {
  assert.equal(describeDatabaseList(dbs), '3 database(s) found (2 not online). Pick one from the Database field.');
  assert.equal(describeDatabaseList([dbs[0]]), '1 database(s) found. Pick one from the Database field.');
});

test('stateOf is case-insensitive and ignores blanks', () => {
  assert.equal(stateOf(dbs, ' sales '), 'OFFLINE');
  assert.equal(stateOf(dbs, 'nope'), undefined);
  assert.equal(stateOf(dbs, '  '), undefined);
});

test('parseDatabaseList accepts either casing and drops malformed rows', () => {
  assert.deepEqual(
    parseDatabaseList([{ name: 'a', state: 'ONLINE' }, { Name: 'b', State: 'OFFLINE' }, { name: 1 }, null, 'x']),
    [{ name: 'a', state: 'ONLINE' }, { name: 'b', state: 'OFFLINE' }]);
  assert.deepEqual(parseDatabaseList(undefined), []);
});

test('bringOnline is a known form message', () => {
  const values = { ...defaultFormValues(), server: 's', database: 'Sales' };
  assert.deepEqual(parseFormMessage({ type: 'bringOnline', values }), { type: 'bringOnline', values });
  assert.equal(parseFormMessage({ type: 'dropDatabase', values }), undefined);
});

test('the form has a hidden database-state note with a Bring online button', () => {
  const html = renderConnectionForm(defaultFormValues(), { mode: 'add', hasSavedPassword: false, nonce: 'n' });
  assert.match(html, /id="dbState" role="status"/);
  assert.match(html, /class="dbstate hidden"/);
  assert.match(html, /id="bringOnline" class="hidden"/);
  assert.match(html, /send\('bringOnline'\)/);
});

test('the extension always asks the TOON tools for JSON rows', () => {
  assert.deepEqual([...TOON_TOOLS].sort(), ['get_insight_history', 'list_insights', 'list_objects', 'read_data']);
  assert.deepEqual(withJsonRows('read_data', { sql: 'x' }), { sql: 'x', toon: false });
  assert.deepEqual(withJsonRows('list_objects', { objectType: 'Table', toon: true }), { objectType: 'Table', toon: true });
  const args = { sql: 'x' };
  assert.equal(withJsonRows('run_script', args), args);
  assert.equal(withJsonRows('probe_list_databases', args), args);
});

test('List databases with an empty Database field validates with the master override', () => {
  const v = { ...defaultFormValues(), server: 'srv', database: '' };
  assert.equal(formToProbeProfile(v, { hasPassword: false }).profile, undefined);
  assert.equal(formToProbeProfile(v, { hasPassword: false, database: 'master' }).profile?.database, 'master');
});

test('only "cannot open database" errors trigger a state lookup, never a failed login', () => {
  assert.equal(isDatabaseUnavailableError('Cannot open database "Sales" requested by the login. The login failed.'), true);
  assert.equal(isDatabaseUnavailableError("Database 'Sales' cannot be opened because it is offline."), true);
  assert.equal(isDatabaseUnavailableError("Database 'Sales' is being recovered. Waiting until recovery is finished."), true);
  assert.equal(isDatabaseUnavailableError("Login failed for user 'sa'."), false);
  assert.equal(isDatabaseUnavailableError('A network-related or instance-specific error occurred'), false);
});

test('the state cache key changes with the server or login, not with the database', () => {
  const v = { ...defaultFormValues(), server: 'SrvA', database: 'Sales' };
  assert.equal(stateCacheKey(v), stateCacheKey({ ...v, server: ' srva ', database: 'Other' }));
  assert.notEqual(stateCacheKey(v), stateCacheKey({ ...v, server: 'SrvB' }));
  assert.notEqual(stateCacheKey(v), stateCacheKey({ ...v, auth: 'sql', user: 'sa' }));
  const raw = { ...v, auth: 'raw', rawConnectionString: 'Server=a;Initial Catalog=x' };
  assert.notEqual(stateCacheKey(raw), stateCacheKey({ ...raw, rawConnectionString: 'Server=b;Initial Catalog=x' }));
});
