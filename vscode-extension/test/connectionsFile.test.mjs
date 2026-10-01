import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildConnectionsFile, needsSecretDecision } from '../out/register/connectionsFile.js';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { execFileSync } from 'node:child_process';
import { addJsonArgs, cmdQuote, findClaude, powershellCommand, registerClaudeCode } from '../out/register/claudeCode.js';

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
  assert.match(JSON.parse(r.json)[0].connectionString, /Password="\$\{env:MSSQLMCP_PWD_PROD_1\}"(;|$)/);
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
  return { calls, run: async (file, args, opts) => { calls.push({ file, args, shell: opts.shell }); const r = codes(file, args, opts); return typeof r === 'object' ? r : { code: r, stderr: r === 0 ? '' : 'boom' }; } };
};
const exe = () => 'C:\\bin\\claude.exe';
const cmd = () => 'C:\\npm\\claude.cmd';

test('findClaude scans PATH with PATHEXT', () => {
  const env = { PATH: `C:\\a${path.delimiter}C:\\b`, PATHEXT: '.COM;.EXE;.CMD' };
  const want = path.join('C:\\b', 'claude.cmd');
  assert.equal(findClaude(env, p => p === want), want);
  assert.equal(findClaude(env, () => false), undefined);
});

test('claude exe: remove then add, shell:false', async () => {
  const r = recorder(() => 0);
  assert.deepEqual(await registerClaudeCode(entry, r.run, exe), { status: 'registered' });
  assert.deepEqual(r.calls.map(c => [c.file, c.shell, c.args[1]]), [['C:\\bin\\claude.exe', false, 'remove'], ['C:\\bin\\claude.exe', false, 'add-json']]);
});

test('remove failure is ignored, add failure is reported', async () => {
  const r = recorder((f, a) => (a[1] === 'remove' ? 1 : 2));
  assert.equal((await registerClaudeCode(entry, r.run, exe)).status, 'failed');
});

test('claude.cmd runs through a shell with quoted args', async () => {
  const r = recorder(() => 0);
  assert.deepEqual(await registerClaudeCode(entry, r.run, cmd), { status: 'registered' });
  const add = r.calls.at(-1);
  assert.equal(add.shell, true);
  assert.ok(add.args.every(a => a.startsWith('"') && a.endsWith('"')));
});

test('unavailable: not on PATH, shell says not recognized, exit 9009, cmd metacharacters', async () => {
  assert.equal((await registerClaudeCode(entry, recorder(() => 0).run, () => undefined)).status, 'unavailable');
  const nr = recorder(() => ({ code: 1, stderr: "'claude.cmd' is not recognized as an internal or external command" }));
  assert.equal((await registerClaudeCode(entry, nr.run, cmd)).status, 'unavailable');
  assert.equal((await registerClaudeCode(entry, recorder(() => 9009).run, cmd)).status, 'unavailable');
  const r = recorder(() => 0);
  const bad = { ...entry, command: 'C:\\a&b\\MssqlMcp.exe' };
  assert.equal((await registerClaudeCode(bad, r.run, cmd)).status, 'unavailable');
  assert.equal(r.calls.length, 0);
});

test('powershell copy command survives Windows PowerShell 5.1 argument passing', { skip: process.platform !== 'win32' }, () => {
  const tricky = { command: "C:\\Users\\O'Brien\\My Dir\\MssqlMcp.exe", args: [], env: { MSSQL_CONNECTIONS_FILE: "C:\\Users\\O'Brien\\My Dir\\connections.json", X: 'a "q" b\\' } };
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ps-'));
  const ps1 = path.join(dir, 't.ps1');
  fs.writeFileSync(ps1, powershellCommand(tricky, `& '${process.execPath.replace(/'/g, "''")}' -e "console.log(process.argv[6])"`) + '\n');
  const out = execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ps1], { encoding: 'utf8' }).trim();
  assert.deepEqual(JSON.parse(out), { type: 'stdio', ...tricky });
});
