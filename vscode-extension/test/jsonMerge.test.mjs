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
  assert.deepEqual(JSON.parse(mergeMcpServer(undefined, 'ms-sql', entry)).mcpServers['ms-sql'], entry);
});

test('preserves other servers and top-level keys', () => {
  const out = JSON.parse(mergeMcpServer(JSON.stringify({ theme: 'dark', mcpServers: { other: { command: 'o' } } }), 'ms-sql', entry));
  assert.equal(out.theme, 'dark');
  assert.equal(out.mcpServers.other.command, 'o');
  assert.equal(out.mcpServers['ms-sql'].command, entry.command);
});

test('replaces an existing entry with the same key', () => {
  const out = JSON.parse(mergeMcpServer(JSON.stringify({ mcpServers: { 'ms-sql': { command: 'old' } } }), 'ms-sql', entry));
  assert.equal(out.mcpServers['ms-sql'].command, entry.command);
});

test('refuses invalid JSON instead of overwriting', () => {
  assert.throws(() => mergeMcpServer('{ not json', 'ms-sql', entry), /not valid JSON/);
  assert.throws(() => mergeMcpServer('[1]', 'ms-sql', entry), /not valid JSON/);
});

test('accepts a UTF-8 BOM and an empty file', () => {
  assert.ok(JSON.parse(mergeMcpServer('\uFEFF{"a":1}', 'ms-sql', entry)).a === 1);
  assert.ok(JSON.parse(mergeMcpServer('  ', 'ms-sql', entry)).mcpServers['ms-sql']);
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
  const r = writeClientConfig(p, 'ms-sql', entry);
  assert.equal(r.backup, undefined);
  assert.deepEqual(JSON.parse(fs.readFileSync(p, 'utf8')).mcpServers['ms-sql'], entry);
  assert.deepEqual(fs.readdirSync(path.dirname(p)), ['mcp.json']);
});

test('writeClientConfig keeps a .bak of the old content and other servers', () => {
  const dir = tmp();
  const p = path.join(dir, 'mcp.json');
  const old = JSON.stringify({ mcpServers: { other: { command: 'o' } } });
  fs.writeFileSync(p, old);
  const r = writeClientConfig(p, 'ms-sql', entry, new Date(2026, 0, 2, 3, 4, 5));
  assert.equal(path.basename(r.backup), 'mcp.json.20260102030405.bak');
  assert.equal(fs.readFileSync(r.backup, 'utf8'), old);
  const now = JSON.parse(fs.readFileSync(p, 'utf8'));
  assert.equal(now.mcpServers.other.command, 'o');
  assert.ok(now.mcpServers['ms-sql']);
  assert.equal(fs.readdirSync(dir).filter(f => f.endsWith('.tmp')).length, 0);
});

test('writeClientConfig never touches invalid JSON and makes no backup', () => {
  const dir = tmp();
  const p = path.join(dir, 'mcp.json');
  fs.writeFileSync(p, '{ broken');
  assert.throws(() => writeClientConfig(p, 'ms-sql', entry), /not valid JSON/);
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
