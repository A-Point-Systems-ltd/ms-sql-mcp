import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { copiedVersion, removableVersions, staleVersion, upgradeRegistrations, withNewCommand } from '../out/register/upgradeRegistrations.js';

const storage = 'C:\\Users\\u\\AppData\\Roaming\\Cursor\\User\\globalStorage\\apoint.ms-sql-mcp';
const exeOf = (dir, v) => path.win32.join(dir, 'bin', v, 'MssqlMcp.exe');

test('only an older copy under globalStorage\\bin\\<version>\\ is stale (case-insensitive)', () => {
  assert.equal(staleVersion(exeOf(storage, '1.2.2'), storage, '1.2.3'), '1.2.2');
  assert.equal(staleVersion(exeOf(storage, '1.2.2').replace('C:', 'c:').toUpperCase(), storage, '1.2.3'), '1.2.2');
  assert.equal(staleVersion(exeOf(storage, '1.2.3'), storage, '1.2.3'), undefined);
  assert.equal(staleVersion('C:\\Development\\MCPs\\MS-SQL-Release\\MssqlMcp.exe', storage, '1.2.3'), undefined);
  assert.equal(staleVersion(path.win32.join(storage, 'bin', '1.2.2', 'other', 'MssqlMcp.exe'), storage, '1.2.3'), undefined);
  assert.equal(staleVersion(path.win32.join(storage, 'bin', '1.2.2', 'Other.exe'), storage, '1.2.3'), undefined);
  assert.equal(staleVersion(undefined, storage, '1.2.3'), undefined);
  assert.equal(copiedVersion(exeOf(storage, '1.2.3'), storage), '1.2.3');
});

test('only the command of our entry changes; env and other servers are kept', () => {
  const text = JSON.stringify({ other: 1, mcpServers: { n8n: { command: 'x' }, 'APoint-ms-sql': { command: exeOf(storage, '1.2.2'), args: [], env: { MSSQL_CONNECTIONS_FILE: 'f' } } } });
  const next = JSON.parse(withNewCommand(text, exeOf(storage, '1.2.3'), cmd => staleVersion(cmd, storage, '1.2.3') !== undefined));
  assert.deepEqual(next, { other: 1, mcpServers: { n8n: { command: 'x' }, 'APoint-ms-sql': { command: exeOf(storage, '1.2.3'), args: [], env: { MSSQL_CONNECTIONS_FILE: 'f' } } } });
  assert.equal(withNewCommand(text, 'new', () => false), undefined);
  assert.equal(withNewCommand('{"mcpServers":{}}', 'new', () => true), undefined);
  assert.equal(withNewCommand('not json', 'new', () => true), undefined);
});

test('old version folders go unless a client still uses them', () => {
  assert.deepEqual(removableVersions(['1.2.0', '1.2.2', '1.2.3', 'bad name'], '1.2.3', new Set(['1.2.0'])), ['1.2.2']);
});

/** A real temp folder: storage\bin\<versions>, a Cursor and a Claude Desktop config. */
function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mssql-upgrade-'));
  const dir = path.join(root, 'storage');
  for (const v of ['1.2.1', '1.2.2']) {
    fs.mkdirSync(path.join(dir, 'bin', v), { recursive: true });
    fs.writeFileSync(path.join(dir, 'bin', v, 'MssqlMcp.exe'), 'old');
  }
  const cursor = path.join(root, 'mcp.json');
  const desktop = path.join(root, 'claude_desktop_config.json');
  fs.writeFileSync(cursor, JSON.stringify({ mcpServers: { 'APoint-ms-sql': { command: exeOf(dir, '1.2.2'), args: [], env: { A: 'secret' } } } }));
  fs.writeFileSync(desktop, JSON.stringify({ mcpServers: { 'APoint-ms-sql': { command: 'C:\\Release\\MssqlMcp.exe' } } }));
  const logs = [];
  const log = { info: (_s, m) => logs.push(m), warn: (_s, m) => logs.push(m), error: (_s, m) => logs.push(m) };
  const ensureExe = async () => {
    fs.mkdirSync(path.join(dir, 'bin', '1.2.3'), { recursive: true });
    fs.writeFileSync(exeOf(dir, '1.2.3'), 'new');
    return exeOf(dir, '1.2.3');
  };
  return { root, dir, cursor, desktop, logs, log, ensureExe };
}

test('an upgrade moves stale entries to the new copy, keeps foreign ones and deletes unused copies', async () => {
  const f = fixture();
  const writes = [];
  const registered = [];
  const updated = await upgradeRegistrations({
    storageDir: f.dir, version: '1.2.3', ensureExe: f.ensureExe, log: f.log,
    configFiles: [{ label: 'Cursor', path: f.cursor }, { label: 'Claude Desktop', path: f.desktop }, { label: 'Missing', path: path.join(f.root, 'none.json') }],
    writeConfig: (p, text) => { writes.push(p); fs.writeFileSync(p, text); },
    claudeCode: {
      read: () => ({ type: 'stdio', command: exeOf(f.dir, '1.2.1'), args: [], env: { B: 'b' } }),
      register: async entry => { registered.push(entry); return true; },
    },
  });
  assert.deepEqual(updated, ['Cursor', 'Claude Code']);
  assert.deepEqual(writes, [f.cursor]);
  const cursor = JSON.parse(fs.readFileSync(f.cursor, 'utf8')).mcpServers['APoint-ms-sql'];
  assert.equal(cursor.command, exeOf(f.dir, '1.2.3'));
  assert.deepEqual(cursor.env, { A: 'secret' });
  assert.equal(JSON.parse(fs.readFileSync(f.desktop, 'utf8')).mcpServers['APoint-ms-sql'].command, 'C:\\Release\\MssqlMcp.exe');
  assert.deepEqual(registered, [{ command: exeOf(f.dir, '1.2.3'), args: [], env: { B: 'b' } }]);
  assert.deepEqual(fs.readdirSync(path.join(f.dir, 'bin')), ['1.2.3']);
  assert.ok(!f.logs.some(m => m.includes('secret')));
});

test('nothing stale: no copy is made, nothing is written, and copies still in use stay', async () => {
  const f = fixture();
  fs.writeFileSync(f.cursor, JSON.stringify({ mcpServers: { 'APoint-ms-sql': { command: exeOf(f.dir, '1.2.2') } } }));
  let copied = false;
  const updated = await upgradeRegistrations({
    storageDir: f.dir, version: '1.2.2', ensureExe: async () => { copied = true; return ''; }, log: f.log,
    configFiles: [{ label: 'Cursor', path: f.cursor }], writeConfig: () => assert.fail('no write expected'),
  });
  assert.deepEqual(updated, []);
  assert.equal(copied, false);
  assert.deepEqual(fs.readdirSync(path.join(f.dir, 'bin')).sort(), ['1.2.2']);
});

test('a failed Claude Code re-registration keeps its old copy', async () => {
  const f = fixture();
  fs.rmSync(f.cursor);
  const updated = await upgradeRegistrations({
    storageDir: f.dir, version: '1.2.3', ensureExe: f.ensureExe, log: f.log, configFiles: [{ label: 'Cursor', path: f.cursor }],
    writeConfig: () => assert.fail('no write expected'),
    claudeCode: { read: () => ({ command: exeOf(f.dir, '1.2.1') }), register: async () => false },
  });
  assert.deepEqual(updated, []);
  assert.deepEqual(fs.readdirSync(path.join(f.dir, 'bin')).sort(), ['1.2.1', '1.2.3']);
});
