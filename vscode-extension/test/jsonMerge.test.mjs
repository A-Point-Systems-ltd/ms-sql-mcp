import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { mergeMcpServer } from '../out/register/jsonMerge.js';
import { backupPath, normalizeName, passwordEnvVar, timestamp } from '../out/register/naming.js';
import { writeClientConfig } from '../out/register/configWriter.js';
import { claudeDesktopConfigPaths, cursorConfigPath } from '../out/register/clientPaths.js';
import { copyStableExe } from '../out/register/stableExe.js';

const entry = { command: 'C:\\x\\MssqlMcp.exe', args: [], env: { MSSQL_CONNECTIONS_FILE: 'C:\\x\\connections.json' } };
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'mssqlmcp-'));

test('creates file content when none exists', () => {
  assert.deepEqual(JSON.parse(mergeMcpServer(undefined, 'APoint-ms-sql', entry)).mcpServers['APoint-ms-sql'], entry);
});

test('preserves other servers and top-level keys', () => {
  const out = JSON.parse(mergeMcpServer(JSON.stringify({ theme: 'dark', mcpServers: { other: { command: 'o' } } }), 'APoint-ms-sql', entry));
  assert.equal(out.theme, 'dark');
  assert.equal(out.mcpServers.other.command, 'o');
  assert.equal(out.mcpServers['APoint-ms-sql'].command, entry.command);
});

test('replaces an existing entry with the same key', () => {
  const out = JSON.parse(mergeMcpServer(JSON.stringify({ mcpServers: { 'APoint-ms-sql': { command: 'old' } } }), 'APoint-ms-sql', entry));
  assert.equal(out.mcpServers['APoint-ms-sql'].command, entry.command);
});

test('refuses invalid JSON instead of overwriting', () => {
  assert.throws(() => mergeMcpServer('{ not json', 'APoint-ms-sql', entry), /not valid JSON/);
  assert.throws(() => mergeMcpServer('[1]', 'APoint-ms-sql', entry), /not valid JSON/);
});

test('accepts a UTF-8 BOM and an empty file', () => {
  assert.ok(JSON.parse(mergeMcpServer('\uFEFF{"a":1}', 'APoint-ms-sql', entry)).a === 1);
  assert.ok(JSON.parse(mergeMcpServer('  ', 'APoint-ms-sql', entry)).mcpServers['APoint-ms-sql']);
});

test('name normalization and placeholder variable', () => {
  assert.equal(normalizeName('Prod-DB.1'), 'PROD_DB_1');
  assert.equal(passwordEnvVar('crm_test'), 'MSSQLMCP_PWD_CRM_TEST');
});

test('backup file naming', () => {
  const d = new Date(2026, 8, 5, 7, 4, 9);
  assert.equal(timestamp(d), '20260905070409');
  assert.equal(backupPath('C:\\a\\mcp.json', d), 'C:\\a\\mcp.json.20260905070409.bak');
});

test('cursor path is ~/.cursor/mcp.json', () => {
  assert.equal(cursorConfigPath('C:\\Users\\u'), path.join('C:\\Users\\u', '.cursor', 'mcp.json'));
});

test('claude desktop paths: APPDATA plus existing MSIX dirs only', () => {
  const root = tmp();
  const local = path.join(root, 'Local');
  const msix = path.join(local, 'Packages', 'Claude_abc', 'LocalCache', 'Roaming', 'Claude');
  fs.mkdirSync(msix, { recursive: true });
  fs.mkdirSync(path.join(local, 'Packages', 'Claude_nodir'), { recursive: true });
  fs.mkdirSync(path.join(local, 'Packages', 'Other_x', 'LocalCache', 'Roaming', 'Claude'), { recursive: true });
  const paths = claudeDesktopConfigPaths({ APPDATA: path.join(root, 'Roaming'), LOCALAPPDATA: local });
  assert.deepEqual(paths, [
    path.join(root, 'Roaming', 'Claude', 'claude_desktop_config.json'),
    path.join(msix, 'claude_desktop_config.json'),
  ]);
  assert.deepEqual(claudeDesktopConfigPaths({ APPDATA: 'A' }), [path.join('A', 'Claude', 'claude_desktop_config.json')]);
});

test('writeClientConfig creates a new file without backup', () => {
  const p = path.join(tmp(), 'sub', 'mcp.json');
  const r = writeClientConfig(p, 'APoint-ms-sql', entry);
  assert.equal(r.backup, undefined);
  assert.deepEqual(JSON.parse(fs.readFileSync(p, 'utf8')).mcpServers['APoint-ms-sql'], entry);
  assert.deepEqual(fs.readdirSync(path.dirname(p)), ['mcp.json']);
});

test('writeClientConfig keeps a .bak of the old content and other servers', () => {
  const dir = tmp();
  const p = path.join(dir, 'mcp.json');
  const old = JSON.stringify({ mcpServers: { other: { command: 'o' } } });
  fs.writeFileSync(p, old);
  const r = writeClientConfig(p, 'APoint-ms-sql', entry, new Date(2026, 0, 2, 3, 4, 5));
  assert.equal(path.basename(r.backup), 'mcp.json.20260102030405.bak');
  assert.equal(fs.readFileSync(r.backup, 'utf8'), old);
  const now = JSON.parse(fs.readFileSync(p, 'utf8'));
  assert.equal(now.mcpServers.other.command, 'o');
  assert.ok(now.mcpServers['APoint-ms-sql']);
  assert.equal(fs.readdirSync(dir).filter(f => f.endsWith('.tmp')).length, 0);
});

test('mergeMcpServer removes the legacy key and leaves other servers untouched', () => {
  const text = JSON.stringify({ theme: 'dark', mcpServers: { 'ms-sql': { command: 'old' }, other: { command: 'o' } } });
  const out = JSON.parse(mergeMcpServer(text, 'APoint-ms-sql', entry, 'ms-sql'));
  assert.deepEqual(Object.keys(out.mcpServers).sort(), ['APoint-ms-sql', 'other']);
  assert.deepEqual(out.mcpServers.other, { command: 'o' });
  assert.equal(out.theme, 'dark');
  const keep = JSON.parse(mergeMcpServer(text, 'APoint-ms-sql', entry));
  assert.ok(keep.mcpServers['ms-sql'], 'without a legacy key nothing is removed');
});

test('writeClientConfig removes the legacy key in the same write, after the .bak, and reports it', () => {
  const dir = tmp();
  const p = path.join(dir, 'mcp.json');
  const old = JSON.stringify({ mcpServers: { 'ms-sql': { command: 'old' }, other: { command: 'o' } } });
  fs.writeFileSync(p, old);
  const r = writeClientConfig(p, 'APoint-ms-sql', entry, new Date(2026, 0, 2, 3, 4, 5), 'ms-sql');
  assert.equal(r.removedLegacy, true);
  assert.equal(fs.readFileSync(r.backup, 'utf8'), old, 'the backup still holds the legacy entry');
  const now = JSON.parse(fs.readFileSync(p, 'utf8'));
  assert.deepEqual(Object.keys(now.mcpServers).sort(), ['APoint-ms-sql', 'other']);
  const again = writeClientConfig(p, 'APoint-ms-sql', entry, new Date(2026, 0, 2, 3, 4, 6), 'ms-sql');
  assert.equal(again.removedLegacy, false);
});

test('writeClientConfig never touches invalid JSON and makes no backup', () => {
  const dir = tmp();
  const p = path.join(dir, 'mcp.json');
  fs.writeFileSync(p, '{ broken');
  assert.throws(() => writeClientConfig(p, 'APoint-ms-sql', entry), /not valid JSON/);
  assert.equal(fs.readFileSync(p, 'utf8'), '{ broken');
  assert.deepEqual(fs.readdirSync(dir), ['mcp.json']);
});

test('copyStableExe copies per version, keeps older versions, reuses same-size copy', () => {
  const dir = tmp();
  const src = path.join(dir, 'src.exe');
  fs.writeFileSync(src, 'abc');
  const a = copyStableExe(src, dir, '1.0.0', 'MssqlMcp.exe');
  assert.equal(a, path.join(dir, 'bin', '1.0.0', 'MssqlMcp.exe'));
  const b = copyStableExe(src, dir, '1.1.0', 'MssqlMcp.exe');
  assert.ok(fs.existsSync(a) && fs.existsSync(b));
  fs.writeFileSync(src, 'abcdef');
  copyStableExe(src, dir, '1.1.0', 'MssqlMcp.exe');
  assert.equal(fs.readFileSync(b, 'utf8'), 'abcdef');
  assert.throws(() => copyStableExe(src, dir, '..\\x', 'MssqlMcp.exe'), /version/);
});

test('backups within the same second get a counter suffix', () => {
  const dir = tmp();
  const p = path.join(dir, 'mcp.json');
  fs.writeFileSync(p, '{}');
  const d = new Date(2026, 0, 2, 3, 4, 5);
  const a = writeClientConfig(p, 'APoint-ms-sql', entry, d);
  const b = writeClientConfig(p, 'APoint-ms-sql', entry, d);
  assert.equal(path.basename(a.backup), 'mcp.json.20260102030405.bak');
  assert.equal(path.basename(b.backup), 'mcp.json.20260102030405-2.bak');
});

test('copyStableExe recopies a same-size file with a different mtime', () => {
  const dir = tmp();
  const src = path.join(dir, 'src.exe');
  fs.writeFileSync(src, 'aaa');
  const t = copyStableExe(src, dir, '2.0.0', 'MssqlMcp.exe');
  fs.writeFileSync(src, 'bbb');
  fs.utimesSync(src, new Date(2020, 0, 1), new Date(2020, 0, 1));
  copyStableExe(src, dir, '2.0.0', 'MssqlMcp.exe');
  assert.equal(fs.readFileSync(t, 'utf8'), 'bbb');
  const m = fs.statSync(t).mtimeMs;
  copyStableExe(src, dir, '2.0.0', 'MssqlMcp.exe');
  assert.equal(fs.statSync(t).mtimeMs, m);
});
