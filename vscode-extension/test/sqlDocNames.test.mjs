import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { URI, Utils } from 'vscode-uri';
import {
  SQL_DOC_ROOT, SQL_DOC_SCHEME, QueryCounter, backingFile, docTitle, isLegacyEditPath, isSqlDocDirectory, isValidDocId, needsSqlLanguage,
  newQueryId, objectDisplayName, parseSqlDocPath, profileTarget, queryNumberOf, queryObjectName, sqlDocPath, titlePart,
  writeFileCheck,
} from '../out/query/sqlDocNames.js';

const profile = (over = {}) => ({
  name: 'dev', server: 'DC\\DEV', database: 'Sales', auth: 'windows', readOnly: false, insights: false, open: true,
  encrypt: 'optional', trustServerCertificate: true, ...over,
});

test('scheme is mssql-sql', () => {
  assert.equal(SQL_DOC_SCHEME, 'mssql-sql');
});

test('title is <objectName> - <server> - <database>', () => {
  assert.equal(docTitle('Query 1', profileTarget(profile())), 'Query 1 - DC\\DEV - Sales');
  assert.equal(docTitle('dbo.vOrders', { server: 'srv,1433', database: 'my db' }), 'dbo.vOrders - srv,1433 - my db');
  assert.equal(queryObjectName(3), 'Query 3');
});

test('object display name is schema.name, or name without a schema', () => {
  assert.equal(objectDisplayName({ connection: 'dev', scriptType: 'View', schema: 'dbo', name: 'v' }), 'dbo.v');
  assert.equal(objectDisplayName({ connection: 'dev', scriptType: 'View', name: 'v' }), 'v');
});

test('profile target: structured profiles use server/database; raw ones parse Data Source / Initial Catalog', () => {
  assert.deepEqual(profileTarget(profile({ server: ' S1 ', database: ' D1 ' })), { server: 'S1', database: 'D1' });
  const raw = s => profile({ auth: 'raw', server: '', database: '', rawConnectionString: s });
  assert.deepEqual(profileTarget(raw('Data Source=DC\\DEV;Initial Catalog=Sales;Integrated Security=true')), { server: 'DC\\DEV', database: 'Sales' });
  assert.deepEqual(profileTarget(raw('Server=tcp:srv,1433')), { server: 'tcp:srv,1433', database: 'default' });
  assert.deepEqual(profileTarget(raw('Integrated Security=true')), { server: 'connection string', database: 'default' });
  assert.deepEqual(profileTarget(raw(undefined)), { server: 'connection string', database: 'default' });
  // A removed profile: the connection name stands in for the server.
  assert.deepEqual(profileTarget(undefined, 'gone'), { server: 'gone', database: 'default' });
});

test('title parts: "/" becomes U+2215, control characters are dropped, empty parts become "_"', () => {
  assert.equal(titlePart('a/b'), 'a∕b');
  assert.equal(titlePart('a\u0000b\u001fc\u007fd\u0085e'), 'abcde');
  assert.equal(titlePart('  '), '_');
  assert.equal(titlePart('DC\\DEV'), 'DC\\DEV', 'a backslash is kept (it survives vscode-uri, see the round-trip test)');
  assert.equal(docTitle('x/y', { server: 's/t', database: 'd\nb' }), 'x∕y - s∕t - db');
});

test('path is /~sql/<kind>/<id>/<title> (no extension) and parses back', () => {
  assert.equal(SQL_DOC_ROOT, '~sql');
  assert.equal(sqlDocPath({ kind: 'query', id: 'abcd1234', title: 'Query 1 - S - D' }), '/~sql/query/abcd1234/Query 1 - S - D');
  assert.deepEqual(parseSqlDocPath('/~sql/object/0123456789abcdef/dbo.v - S - D'), { kind: 'object', id: '0123456789abcdef', title: 'dbo.v - S - D' });
  // Whatever the title ends with belongs to it: no extension is stripped.
  assert.deepEqual(parseSqlDocPath('/~sql/query/abcd1234/x.sql'), { kind: 'query', id: 'abcd1234', title: 'x.sql' });
  // A slash or control character in the title never adds a segment.
  assert.equal(sqlDocPath({ kind: 'query', id: 'abcd1234', title: 'a/b\u0001' }), '/~sql/query/abcd1234/a∕b');
  for (const bad of ['/', '/~sql', '/~sql/query', '/~sql/query/abcd1234', '/~sql/query/abcd1234/', '/~sql/other/abcd1234/x',
    '/~sql/query/ABCD1234/x', '/~sql/query/abc/x', '/~sql/query/abcd1234/a/b', '~sql/query/abcd1234/x', '/~sql/query/../x',
    '/~sql/query/abcd1234/.', '/~sql/query/abcd1234/..',
    // The round-1 shape (never shipped) and other roots are rejected.
    '/query/abcd1234/x', '/sql/query/abcd1234/x', '/~SQL/query/abcd1234/x']) {
    assert.equal(parseSqlDocPath(bad), undefined, bad);
  }
});

test('the ~sql root makes the sql filenamePatterns "**/~sql/**" match every document path', () => {
  // A minimal glob check of the contributed pattern against the path, as VS Code matches it for non-file schemes.
  const matches = p => /(^|\/)~sql\//.test(p);
  for (const title of ['Query 1 - S - D', 'dbo.v - DC\\DEV - db', 'x.cs - s - d']) {
    assert.equal(matches(sqlDocPath({ kind: 'query', id: 'abcd1234', title })), true, title);
  }
});

test('ids: 8 random hex for queries; isValidDocId accepts 8-40 lowercase hex only', () => {
  const ids = new Set(Array.from({ length: 50 }, () => newQueryId()));
  for (const id of ids) assert.match(id, /^[0-9a-f]{8}$/);
  assert.ok(ids.size > 45, 'random');
  assert.equal(isValidDocId('0123abcd'), true);
  assert.equal(isValidDocId('0123456789abcdef'), true);
  for (const bad of ['', '0123abc', '0123ABCD', '..', '0123abcg', 'a'.repeat(41)]) assert.equal(isValidDocId(bad), false, bad);
});

const TITLES = [
  'Query 1 - DC\\DEV - Sales',
  'Query 2 - srv,1433 - my db',
  'dbo.Order Details - DC\\DEV - Northwind',
  'dbo.a#b - s?x - 100%',
  'dbo.%41%2F - srv - db',
  'dbo.טבלה - שרת - בסיס נתונים',
  'dbo.日本 - 😀 - Ünïcödé',
  'x∕y - s - d',
];

test('the title survives vscode-uri: URI.parse(uri.toString()) and Utils.basename give the exact title (the tab label)', () => {
  for (const title of TITLES) {
    const p = sqlDocPath({ kind: 'object', id: '0123456789abcdef', title });
    const uri = URI.from({ scheme: SQL_DOC_SCHEME, path: p });
    const back = URI.parse(uri.toString());
    assert.equal(back.scheme, SQL_DOC_SCHEME, title);
    assert.equal(back.path, p, title);
    assert.equal(back.toString(), uri.toString(), title);
    assert.equal(Utils.basename(back), title, title);
    assert.equal(Utils.basename(uri), title, title);
    assert.deepEqual(parseSqlDocPath(back.path), { kind: 'object', id: '0123456789abcdef', title }, title);
    assert.equal(back.query, '', title);
    assert.equal(back.fragment, '', title);
  }
});

test('query numbers: per session, start at 1, increment, skip numbers already open', () => {
  const c = new QueryCounter();
  assert.equal(c.next(new Set()), 1);
  assert.equal(c.next(new Set()), 2);
  assert.equal(c.next(new Set([3, 4, 6])), 5);
  assert.equal(c.next(new Set([6])), 7);
  const restored = new QueryCounter();
  assert.equal(restored.next(new Set([1, 2])), 3, 'tabs restored from the last session keep their numbers');
  assert.equal(queryNumberOf('Query 12 - S - D'), 12);
  for (const t of ['dbo.v - S - D', 'Query x - S - D', 'Query 0 - S - D', 'MyQuery 1 - S - D']) assert.equal(queryNumberOf(t), undefined, t);
});

test('writeFile flags: create / overwrite decide between write, FileNotFound and FileExists', () => {
  assert.equal(writeFileCheck(true, { create: false, overwrite: false }), 'write', 'editor save of an existing document');
  assert.equal(writeFileCheck(true, { create: false, overwrite: true }), 'write');
  assert.equal(writeFileCheck(true, { create: true, overwrite: true }), 'write');
  assert.equal(writeFileCheck(true, { create: true, overwrite: false }), 'FileExists');
  assert.equal(writeFileCheck(false, { create: true, overwrite: false }), 'write');
  assert.equal(writeFileCheck(false, { create: true, overwrite: true }), 'write');
  assert.equal(writeFileCheck(false, { create: false, overwrite: true }), 'FileNotFound');
  assert.equal(writeFileCheck(false, { create: false, overwrite: false }), 'FileNotFound');
});

test('directory paths: /, /<kind> and /<kind>/<id> only', () => {
  for (const p of ['/', '/~sql', '/~sql/', '/~sql/query', '/~sql/object', '/~sql/query/', '/~sql/query/abcd1234', '/~sql/object/0123456789abcdef/']) {
    assert.equal(isSqlDocDirectory(p), true, p);
  }
  for (const p of ['/~sql/query/abcd1234/Query 1 - S - D', '/query', '/query/abcd1234', '/~sql/other', '/~sql/query/ABCD1234', '/~sql/query/abc', '']) {
    assert.equal(isSqlDocDirectory(p), false, p);
  }
});

test('language: mssql-sql documents are always sql, other schemes are left alone', () => {
  assert.equal(needsSqlLanguage('mssql-sql', 'plaintext'), true);
  assert.equal(needsSqlLanguage('mssql-sql', 'sql'), false);
  assert.equal(needsSqlLanguage('file', 'plaintext'), false);
  assert.equal(needsSqlLanguage('untitled', 'plaintext'), false);
});

test('backing file is <root>/sqldocs/<kind>/<id>.sql; legacy edits/ paths are recognized', () => {
  assert.equal(backingFile(path.join('C:', 'store'), 'query', 'abcd1234'), path.join('C:', 'store', 'sqldocs', 'query', 'abcd1234.sql'));
  assert.throws(() => backingFile('r', 'query', '../x'));
  assert.throws(() => backingFile('r', 'other', 'abcd1234'));
  const root = path.join('C:', 'Users', 'u', 'globalStorage', 'apoint.ms-sql-mcp');
  assert.equal(isLegacyEditPath(path.join(root, 'edits', 'dev~12345678', 'View', 'dbo.v~12345678.sql'), root), true);
  assert.equal(isLegacyEditPath(path.join(root.toUpperCase(), 'EDITS', 'x.sql'), root), true, 'case-insensitive (Windows)');
  assert.equal(isLegacyEditPath(path.join(root, 'editsX', 'x.sql'), root), false);
  assert.equal(isLegacyEditPath(path.join(root, 'sqldocs', 'query', 'abcd1234.sql'), root), false);
  assert.equal(isLegacyEditPath(path.join('C:', 'work', 'edits', 'x.sql'), root), false);
});
