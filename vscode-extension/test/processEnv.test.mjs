import { test } from 'node:test';
import assert from 'node:assert/strict';
import { agentProviderEnv, definitionVersion, explorerProcessEnv, externalClientEnv } from '../out/connections/serverEnv.js';

const p = (name, extra = {}) => ({ name, server: 's', database: 'd', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true, ...extra });
const settings = { insights: true, allowAdhocConnections: false, serverPath: '' };

test('agent provider env removes inherited connection sources with null', () => {
  const env = agentProviderEnv('[]', settings);
  assert.equal(env.MSSQL_CONNECTIONS, '[]');
  assert.equal(env.CONNECTION_STRING, null);
  assert.equal(env.MSSQL_CONNECTIONS_FILE, null);
  assert.equal(env.MSSQL_SCRIPT_RUNNER, null);
  assert.ok('CONNECTION_STRING' in env && 'MSSQL_CONNECTIONS_FILE' in env && 'MSSQL_SCRIPT_RUNNER' in env);
  assert.equal(env.USE_INSIGHTS_LAYER, 'true');
  assert.equal(env.MSSQL_ALLOW_ADHOC_CONNECTIONS, 'false');
  assert.equal(agentProviderEnv('[]', { ...settings, insights: false, allowAdhocConnections: true }).MSSQL_ALLOW_ADHOC_CONNECTIONS, 'true');
});

test('explorer / probe env blanks inherited connection sources and forces safe flags', () => {
  const env = explorerProcessEnv('[{"name":"a"}]');
  assert.deepEqual(env, {
    MSSQL_CONNECTIONS: '[{"name":"a"}]',
    CONNECTION_STRING: '',
    MSSQL_CONNECTIONS_FILE: '',
    USE_INSIGHTS_LAYER: 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
  });
  assert.ok(!('MSSQL_SCRIPT_RUNNER' in env), 'the explorer process never sets the script-runner flag');
  assert.equal(explorerProcessEnv('[]', { LOG_FILE_PATH: 'x' }).LOG_FILE_PATH, 'x');
});

test('explorer env wins over an inherited process env when spread last', () => {
  const inherited = { CONNECTION_STRING: 'Server=evil', MSSQL_CONNECTIONS_FILE: 'C:\\evil.json', PATH: 'p' };
  const merged = { ...inherited, ...explorerProcessEnv('[]') };
  assert.equal(merged.CONNECTION_STRING, '');
  assert.equal(merged.MSSQL_CONNECTIONS_FILE, '');
  assert.equal(merged.PATH, 'p');
});

test('external client env points at the file and blanks the other sources', () => {
  const env = externalClientEnv('C:\\x\\connections.json', { ...settings, insights: false });
  assert.deepEqual(env, {
    MSSQL_CONNECTIONS_FILE: 'C:\\x\\connections.json',
    CONNECTION_STRING: '',
    MSSQL_CONNECTIONS: '',
    USE_INSIGHTS_LAYER: 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
    MSSQL_SCRIPT_RUNNER: '',
  });
});

test('definition version is pkgVersion+8-hex hash and is stable', () => {
  const v = definitionVersion('1.0.0', [p('a'), p('b')], settings, new Map());
  assert.match(v, /^1\.0\.0\+[0-9a-f]{8}$/);
  assert.equal(v, definitionVersion('1.0.0', [p('a'), p('b')], settings, new Map()));
  // Order of profiles does not matter.
  assert.equal(v, definitionVersion('1.0.0', [p('b'), p('a')], settings, new Map()));
});

test('definition version changes with every non-secret server input', () => {
  const base = definitionVersion('1.0.0', [p('a')], settings, new Map());
  const variants = [
    definitionVersion('1.0.1', [p('a')], settings, new Map()),
    definitionVersion('1.0.0', [p('a'), p('b')], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { readOnly: true })], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { insights: false })], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { auth: 'entraDefault' })], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { server: 's2' })], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { database: 'd2' })], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { encrypt: 'strict' })], settings, new Map()),
    definitionVersion('1.0.0', [p('a', { trustServerCertificate: false })], settings, new Map()),
    definitionVersion('1.0.0', [p('a')], { ...settings, insights: false }, new Map()),
    definitionVersion('1.0.0', [p('a')], { ...settings, allowAdhocConnections: true }, new Map()),
    definitionVersion('1.0.0', [p('a')], { ...settings, serverPath: 'C:\\x.exe' }, new Map()),
  ];
  for (const v of variants) assert.notEqual(v, base);
  assert.equal(new Set(variants).size, variants.length);
});

test('definition version ignores closed profiles and never depends on the password value', () => {
  const sql = p('s', { auth: 'sql', user: 'u' });
  const base = definitionVersion('1.0.0', [p('a')], settings, new Map());
  assert.equal(definitionVersion('1.0.0', [p('a'), p('c', { open: false })], settings, new Map()), base);
  const pw1 = definitionVersion('1.0.0', [sql], settings, new Map([['s', 'one']]));
  const pw2 = definitionVersion('1.0.0', [sql], settings, new Map([['s', 'two']]));
  assert.equal(pw1, pw2);
  // A newly saved password makes the profile exportable, which the running server must learn about.
  assert.notEqual(definitionVersion('1.0.0', [sql], settings, new Map()), pw1);
  assert.ok(!pw1.includes('one'));
});
