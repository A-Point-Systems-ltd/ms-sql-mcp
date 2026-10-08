import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { cursorMcpApi, CursorMcpRegistrar, duplicateEntryAction, hasMsSqlEntry } from '../out/cursorMcp.js';
import { cursorServerEnv } from '../out/connections/serverEnv.js';

const p = (name, extra = {}) => ({ name, server: 's', database: 'd', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true, ...extra });
const SETTINGS = { insights: true, allowAdhocConnections: false, serverPath: '' };

function fakeApi() {
  const calls = [];
  return {
    calls,
    registerServer: cfg => calls.push(['register', cfg]),
    unregisterServer: name => calls.push(['unregister', name]),
  };
}

function make(overrides = {}) {
  const api = fakeApi();
  const logs = [];
  const warnings = [];
  const state = { exe: 'C:\\x\\MssqlMcp.exe', profiles: [p('a')], passwords: new Map(), settings: SETTINGS, ...overrides };
  const reg = new CursorMcpRegistrar(api, {
    exePath: () => state.exe,
    profiles: () => state.profiles,
    passwords: async () => state.passwords,
    settings: () => state.settings,
    log: { info: (_s, m) => logs.push(m), warn: (_s, m) => logs.push(m), error: (_s, m) => logs.push(m) },
    warn: m => warnings.push(m),
  });
  return { api, reg, logs, warnings, state };
}

test('cursorMcpApi detects missing, partial and full APIs', () => {
  assert.equal(cursorMcpApi(undefined), undefined);
  assert.equal(cursorMcpApi({}), undefined);
  assert.equal(cursorMcpApi({ cursor: {} }), undefined);
  assert.equal(cursorMcpApi({ cursor: { mcp: {} } }), undefined);
  assert.equal(cursorMcpApi({ cursor: { mcp: { registerServer() {} } } }), undefined);
  assert.equal(cursorMcpApi({ cursor: { mcp: { unregisterServer() {} } } }), undefined);
  const full = { registerServer() {}, unregisterServer() {} };
  assert.equal(cursorMcpApi({ cursor: { mcp: full } }), full);
});

test('cursorServerEnv is string-only with blanked inherited sources and the script runner off', () => {
  const env = cursorServerEnv('[{"name":"a"}]', SETTINGS);
  assert.deepEqual(Object.keys(env).sort(), ['CONNECTION_STRING', 'MSSQL_ALLOW_ADHOC_CONNECTIONS', 'MSSQL_CONNECTIONS', 'MSSQL_CONNECTIONS_FILE', 'MSSQL_PROBE_TOOLS', 'MSSQL_SCRIPT_RUNNER', 'USE_INSIGHTS_LAYER']);
  assert.ok(Object.values(env).every(v => typeof v === 'string'));
  assert.equal(env.MSSQL_CONNECTIONS, '[{"name":"a"}]');
  assert.equal(env.CONNECTION_STRING, '');
  assert.equal(env.MSSQL_CONNECTIONS_FILE, '');
  assert.equal(env.MSSQL_SCRIPT_RUNNER, '');
  assert.equal(env.MSSQL_PROBE_TOOLS, '', 'an agent-facing server never gets the probe tools');
  assert.equal(env.USE_INSIGHTS_LAYER, 'true');
  assert.equal(env.MSSQL_ALLOW_ADHOC_CONNECTIONS, 'false');
  const flipped = cursorServerEnv('[]', { ...SETTINGS, insights: false, allowAdhocConnections: true });
  assert.equal(flipped.USE_INSIGHTS_LAYER, 'false');
  assert.equal(flipped.MSSQL_ALLOW_ADHOC_CONNECTIONS, 'true');
});

test('sync registers once under the ms-sql key with string-only env', async () => {
  const { api, reg } = make();
  await reg.sync();
  assert.equal(api.calls.length, 1);
  const [kind, cfg] = api.calls[0];
  assert.equal(kind, 'register');
  assert.equal(cfg.name, 'APoint-ms-sql');
  assert.equal(cfg.server.command, 'C:\\x\\MssqlMcp.exe');
  assert.deepEqual(cfg.server.args, []);
  assert.ok(Object.values(cfg.server.env).every(v => typeof v === 'string'));
  for (const k of ['MSSQL_CONNECTIONS', 'CONNECTION_STRING', 'MSSQL_CONNECTIONS_FILE', 'USE_INSIGHTS_LAYER', 'MSSQL_ALLOW_ADHOC_CONNECTIONS', 'MSSQL_SCRIPT_RUNNER']) assert.ok(k in cfg.server.env, k);
  assert.deepEqual(JSON.parse(cfg.server.env.MSSQL_CONNECTIONS).map(x => x.name), ['a']);
});

test('a second sync with no change makes no calls', async () => {
  const { api, reg } = make();
  await reg.sync();
  api.calls.length = 0;
  await reg.sync();
  assert.deepEqual(api.calls, []);
});

test('a profile change unregisters then registers', async () => {
  const { api, reg, state } = make();
  await reg.sync();
  api.calls.length = 0;
  state.profiles = [p('a'), p('b')];
  await reg.sync();
  assert.deepEqual(api.calls.map(c => c[0]), ['unregister', 'register']);
  assert.equal(api.calls[0][1], 'APoint-ms-sql');
  assert.deepEqual(JSON.parse(api.calls[1][1].server.env.MSSQL_CONNECTIONS).map(x => x.name), ['a', 'b']);
});

test('a settings change re-registers', async () => {
  const { api, reg, state } = make();
  await reg.sync();
  api.calls.length = 0;
  state.settings = { ...SETTINGS, allowAdhocConnections: true };
  await reg.sync();
  assert.deepEqual(api.calls.map(c => c[0]), ['unregister', 'register']);
});

test('no usable profile unregisters, and does nothing when never registered', async () => {
  const { api, reg, state } = make();
  state.profiles = [p('a', { open: false })];
  await reg.sync();
  assert.deepEqual(api.calls, []);
  state.profiles = [p('a')];
  await reg.sync();
  api.calls.length = 0;
  state.profiles = [p('a', { open: false })];
  await reg.sync();
  assert.deepEqual(api.calls, [['unregister', 'APoint-ms-sql']]);
  await reg.sync();
  assert.equal(api.calls.length, 1);
});

test('a profile that only lacks its password is not usable; no exe unregisters', async () => {
  const { api, reg, state } = make({ profiles: [p('s', { auth: 'sql', user: 'u' })] });
  await reg.sync();
  assert.deepEqual(api.calls, []);
  state.profiles = [p('a')];
  await reg.sync();
  api.calls.length = 0;
  state.exe = undefined;
  await reg.sync();
  assert.deepEqual(api.calls, [['unregister', 'APoint-ms-sql']]);
});

test('missing passwords warn once per distinct set and skip those profiles', async () => {
  const { api, reg, state, warnings } = make({ profiles: [p('a'), p('s1', { auth: 'sql', user: 'u' })] });
  await reg.sync();
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /^APoint-ms-sql: 1 connection\(s\) skipped\. /);
  assert.match(warnings[0], /'s1'/);
  assert.deepEqual(JSON.parse(api.calls[0][1].server.env.MSSQL_CONNECTIONS).map(x => x.name), ['a']);
  await reg.sync();
  assert.equal(warnings.length, 1);
  state.profiles = [p('a'), p('s1', { auth: 'sql', user: 'u' }), p('s2', { auth: 'sql', user: 'u' })];
  await reg.sync();
  assert.equal(warnings.length, 2);
  assert.match(warnings[1], /^APoint-ms-sql: 2 connection\(s\) skipped\. /);
  state.passwords = new Map([['s1', 'x'], ['s2', 'y']]);
  await reg.sync();
  assert.equal(warnings.length, 2);
});

test('dispose unregisters once and stops further syncs', async () => {
  const { api, reg } = make();
  await reg.sync();
  api.calls.length = 0;
  reg.dispose();
  assert.deepEqual(api.calls, [['unregister', 'APoint-ms-sql']]);
  reg.dispose();
  await reg.sync();
  assert.equal(api.calls.length, 1);
});

test('dispose without a registration makes no calls', () => {
  const { api, reg } = make();
  reg.dispose();
  assert.deepEqual(api.calls, []);
});

test('concurrent syncs are serialized and coalesced', async () => {
  const api = fakeApi();
  let release;
  const gate = new Promise(r => { release = r; });
  let first = true;
  let reads = 0;
  const reg = new CursorMcpRegistrar(api, {
    exePath: () => 'C:/x/MssqlMcp.exe',
    profiles: () => [p('a')],
    // The first password read is slow, so two more syncs arrive while it runs.
    passwords: async () => { reads++; if (first) { first = false; await gate; } return new Map(); },
    settings: () => SETTINGS,
    log: { info() {}, warn() {}, error() {} },
    warn() {},
  });
  const all = [reg.sync(), reg.sync(), reg.sync()];
  release();
  await Promise.all(all);
  assert.equal(reads, 2, 'one running sync plus one queued at most');
  assert.equal(api.calls.filter(c => c[0] === 'register').length, 1);
});

test('the log never contains passwords or the connections payload', async () => {
  const secret = 'S3cret!pw';
  const { reg, logs, state } = make({ profiles: [p('a', { auth: 'sql', user: 'u' }), p('b', { auth: 'sql', user: 'u' })], passwords: new Map([['a', secret]]) });
  await reg.sync();
  state.profiles = [p('a', { auth: 'sql', user: 'u' })];
  await reg.sync();
  reg.dispose();
  const text = logs.join('\n');
  assert.ok(logs.length > 0);
  assert.ok(!text.includes(secret));
  assert.ok(!text.includes('MSSQL_CONNECTIONS'));
  assert.ok(!text.includes('Password'));
  assert.match(logs[0], /^Registered 'APoint-ms-sql' with Cursor \(1 connections?\)$/);
});

test('hasMsSqlEntry reads mcp.json read-only and skips missing or invalid files', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cursor-mcp-'));
  try {
    const f = path.join(dir, 'mcp.json');
    assert.equal(hasMsSqlEntry(f), false);
    fs.writeFileSync(f, '{ not json');
    assert.equal(hasMsSqlEntry(f), false);
    fs.writeFileSync(f, JSON.stringify({ mcpServers: { other: {} } }));
    assert.equal(hasMsSqlEntry(f), false);
    const body = JSON.stringify({ mcpServers: { 'APoint-ms-sql': { command: 'x' } } });
    fs.writeFileSync(f, body);
    assert.equal(hasMsSqlEntry(f), true);
    assert.equal(fs.readFileSync(f, 'utf8'), body);
    fs.writeFileSync(f, JSON.stringify({ mcpServers: { 'ms-sql': { command: 'x' } } }));
    assert.equal(hasMsSqlEntry(f), true, 'the legacy key counts as a duplicate too');
    fs.writeFileSync(f, '[]');
    assert.equal(hasMsSqlEntry(f), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('duplicateEntryAction warns while the entry exists until "Don\'t show again", and clears that once it is gone', () => {
  assert.equal(duplicateEntryAction(true, false), 'warn');
  assert.equal(duplicateEntryAction(true, true), 'none');
  assert.equal(duplicateEntryAction(false, true), 'clear');
  assert.equal(duplicateEntryAction(false, false), 'none');
});
