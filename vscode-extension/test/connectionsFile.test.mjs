import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildConnectionsFile, needsSecretDecision } from '../out/register/connectionsFile.js';
import { addJsonArgs, cmdQuote, registerClaudeCode } from '../out/register/claudeCode.js';

const p = (name, extra = {}) => ({ name, server: 's', database: 'd', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true, ...extra });
const sql = (name, extra = {}) => p(name, { auth: 'sql', user: 'u', ...extra });

test('open profiles only, no default key, server shape', () => {
  const r = buildConnectionsFile([p('a'), p('b', { open: false }), p('c', { readOnly: true })], new Map(), false);
  const arr = JSON.parse(r.json);
  assert.deepEqual(arr.map(x => x.name), ['a', 'c']);
  assert.ok(arr.every(x => !('default' in x) && Object.keys(x).sort().join() === 'connectionString,insights,name,readOnly'));
  assert.equal(arr[1].readOnly, true);
});

test('placeholder form when includePasswords=false', () => {
  const r = buildConnectionsFile([sql('Prod-1')], new Map([['Prod-1', 'secret!']]), false);
  assert.ok(!r.json.includes('secret!'));
  assert.match(JSON.parse(r.json)[0].connectionString, /Password=\$\{env:MSSQLMCP_PWD_PROD_1\}(;|$)/);
  assert.deepEqual(r.envVars, ['MSSQLMCP_PWD_PROD_1']);
});

test('placeholder form works even without a stored password', () => {
  const r = buildConnectionsFile([sql('x')], new Map(), false);
  assert.equal(JSON.parse(r.json).length, 1);
});

test('real passwords when includePasswords=true; missing ones skipped', () => {
  const r = buildConnectionsFile([sql('a'), sql('b')], new Map([['a', 'pw1']]), true);
  const arr = JSON.parse(r.json);
  assert.deepEqual(arr.map(x => x.name), ['a']);
  assert.match(arr[0].connectionString, /Password=pw1/);
  assert.deepEqual(r.skipped.map(s => s.name), ['b']);
  assert.deepEqual(r.envVars, []);
});

test('colliding variable names are rejected', () => {
  assert.throws(() => buildConnectionsFile([sql('a-b'), sql('a.b')], new Map(), false), /same variable/);
});

test('raw strings with a password are skipped in placeholder mode, kept otherwise', () => {
  const raw = p('r', { auth: 'raw', rawConnectionString: 'Server=s;User Id=u;Password=zzz' });
  const a = buildConnectionsFile([raw], new Map(), false);
  assert.equal(JSON.parse(a.json).length, 0);
  assert.equal(a.skipped.length, 1);
  assert.ok(!a.json.includes('zzz'));
  assert.equal(JSON.parse(buildConnectionsFile([raw], new Map(), true).json).length, 1);
  const safe = p('r2', { auth: 'raw', rawConnectionString: 'Server=s;Integrated Security=True' });
  assert.equal(JSON.parse(buildConnectionsFile([safe], new Map(), false).json).length, 1);
});

test('needsSecretDecision', () => {
  assert.equal(needsSecretDecision([p('a')]), false);
  assert.equal(needsSecretDecision([sql('a', { open: false })]), false);
  assert.equal(needsSecretDecision([sql('a')]), true);
  assert.equal(needsSecretDecision([p('r', { auth: 'raw', rawConnectionString: 'Server=s;Pwd=x' })]), true);
});

const entry = { command: 'C:\\Users\\u\\gs\\MssqlMcp.exe', args: [], env: { MSSQL_CONNECTIONS_FILE: 'C:\\Users\\u\\gs\\connections.json' } };

test('add-json args carry a stdio entry', () => {
  const a = addJsonArgs(entry);
  assert.deepEqual(a.slice(0, 5), ['mcp', 'add-json', '--scope', 'user', 'ms-sql']);
  assert.deepEqual(JSON.parse(a[5]), { type: 'stdio', ...entry });
});

test('cmdQuote escapes quotes and trailing backslashes', () => {
  assert.equal(cmdQuote('a b'), '"a b"');
  assert.equal(cmdQuote('x"y'), '"x\\"y"');
  assert.equal(cmdQuote('C:\\d\\'), '"C:\\d\\\\"');
});

const recorder = (codes) => {
  const calls = [];
  return { calls, run: async (file, args, opts) => { calls.push({ file, args, shell: opts.shell }); const c = codes(file, args); return { code: c, stderr: c === 0 ? '' : 'boom' }; } };
};

test('claude exe: remove then add, shell:false', async () => {
  const r = recorder(() => 0);
  assert.deepEqual(await registerClaudeCode(entry, r.run), { status: 'registered' });
  assert.deepEqual(r.calls.map(c => [c.file, c.shell, c.args[1]]), [['claude', false, 'remove'], ['claude', false, 'add-json']]);
});

test('remove failure is ignored, add failure is reported', async () => {
  const r = recorder((f, a) => (a[1] === 'remove' ? 1 : 2));
  const out = await registerClaudeCode(entry, r.run);
  assert.equal(out.status, 'failed');
});

test('falls back to claude.cmd through a shell with quoted args', async () => {
  const r = recorder(f => (f === 'claude' ? 'ENOENT' : 0));
  assert.deepEqual(await registerClaudeCode(entry, r.run), { status: 'registered' });
  const add = r.calls.at(-1);
  assert.equal(add.file, 'claude.cmd');
  assert.equal(add.shell, true);
  assert.ok(add.args.every(a => a.startsWith('"') && a.endsWith('"')));
});

test('unavailable when neither exists, or when cmd metacharacters are present', async () => {
  assert.equal((await registerClaudeCode(entry, recorder(() => 'ENOENT').run)).status, 'unavailable');
  const r = recorder(f => (f === 'claude' ? 'ENOENT' : 0));
  const bad = { ...entry, command: 'C:\\a&b\\MssqlMcp.exe' };
  assert.equal((await registerClaudeCode(bad, r.run)).status, 'unavailable');
  assert.ok(r.calls.every(c => !c.shell));
});
