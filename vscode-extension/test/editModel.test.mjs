import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  applyEdit, buildSaveScript, columnMetaSql, deleteRows, editCapabilities, effectiveRows, emptyPending, isEditableType, parseColumnMeta,
  parseEditMessage, pendingCount, pendingProblems, revertRows, sqlLiteral, validateValue, writableColumns,
} from '../out/grid/editModel.js';
import { renderGrid } from '../out/grid/gridHtml.js';

const T = { schema: 'dbo', name: 'Cust]omers' };
const columns = [{ name: 'Id', type: 'int' }, { name: 'Name', type: 'nvarchar' }, { name: 'Active', type: 'bit' }, { name: 'At', type: 'datetime' }, { name: 'Notes', type: 'ntext' }];
const rows = [[1, 'Acme', true, '2024-01-02T10:00:00', 'x'], [2, null, false, null, null]];
const meta = parseColumnMeta([
  ['Id', 'int', true, false, 0, false], ['Name', 'nvarchar', false, false, 0, true], ['Active', 'bit', false, false, 1, false],
  ['At', 'datetime', false, false, 0, true], ['Notes', 'ntext', 0, 0, 0, 1],
]);

test('column metadata and writable columns: identity, computed and binary / spatial types are not writable', () => {
  assert.match(columnMetaSql(T), /OBJECT_ID\(N'\[dbo\]\.\[Cust\]\]omers\]'\)/);
  assert.deepEqual(meta[0], { name: 'Id', type: 'int', identity: true, computed: false, hasDefault: false, nullable: false });
  assert.deepEqual(writableColumns(columns, meta), [false, true, true, true, true]);
  assert.deepEqual(writableColumns([{ name: 'Missing', type: 'int' }], meta), [false]);
  for (const t of ['varbinary', 'timestamp', 'geography', 'sql_variant', 'db.dbo.udt', '']) assert.ok(!isEditableType(t), t);
});

test('capabilities: views, read-only connections and tables without a primary key', () => {
  assert.equal(editCapabilities({ isTable: false, readOnly: false, pk: ['Id'], metaLoaded: true }).update, false);
  assert.equal(editCapabilities({ isTable: true, readOnly: true, pk: ['Id'], metaLoaded: true }).insert, false);
  assert.deepEqual(editCapabilities({ isTable: true, readOnly: false, pk: [], metaLoaded: true }),
    { update: false, delete: false, insert: true, reason: 'No primary key: rows can be added, not changed or deleted.' });
  assert.deepEqual(editCapabilities({ isTable: true, readOnly: false, pk: ['Id'], metaLoaded: true }), { update: true, delete: true, insert: true });
});

test('values: validation and literals by type (numbers unquoted only when valid, dates cast, text as N\'\')', () => {
  assert.equal(validateValue('int', '12'), undefined);
  assert.ok(validateValue('int', '1.5'));
  assert.ok(validateValue('int', '1; DROP TABLE x'));
  assert.equal(validateValue('bit', 'TRUE'), undefined);
  assert.ok(validateValue('bit', 'yes'));
  assert.equal(validateValue('decimal', '-12.50'), undefined);
  assert.ok(validateValue('money', '1,000'));
  assert.equal(validateValue('float', '1e-7'), undefined);
  assert.ok(validateValue('uniqueidentifier', 'abc'));
  assert.ok(validateValue('int', null, false), 'NOT NULL');
  assert.equal(validateValue('nvarchar', ''), undefined);
  assert.equal(sqlLiteral('int', ' 42 '), '42');
  assert.equal(sqlLiteral('int', '1; DROP TABLE x'), "N'1; DROP TABLE x'", 'never raw SQL');
  assert.equal(sqlLiteral('bit', 'false'), '0');
  assert.equal(sqlLiteral('datetime', '2024-01-02T10:00:00.0033333'), "CAST(N'2024-01-02T10:00:00.0033333' AS datetime2(7))");
  assert.equal(sqlLiteral('datetimeoffset', '2024-01-02T10:00:00+02:00'), "CAST(N'2024-01-02T10:00:00+02:00' AS datetimeoffset(7))");
  assert.equal(sqlLiteral('nvarchar', "O'Brien"), "N'O''Brien'");
  assert.equal(sqlLiteral('nvarchar', null), 'NULL');
});

test('pending changes: edits back to the loaded value drop out; delete, revert and new rows', () => {
  const p = emptyPending();
  applyEdit(p, rows, 0, 1, 'Acme Ltd');
  applyEdit(p, rows, 1, 1, 'Beta');
  assert.equal(pendingCount(p), 2);
  applyEdit(p, rows, 1, 1, null);
  assert.equal(pendingCount(p), 1, 'back to NULL = unchanged');
  assert.deepEqual(effectiveRows(rows, p)[0], [1, 'Acme Ltd', true, '2024-01-02T10:00:00', 'x']);
  p.inserts.push([undefined, undefined, undefined, undefined, undefined]);
  assert.ok(applyEdit(p, rows, 2, 1, 'New'));
  assert.ok(!applyEdit(p, rows, 3, 1, 'x'), 'no such new row');
  deleteRows(p, 2, [1]);
  assert.equal(pendingCount(p), 3);
  revertRows(p, 2, [1, 2]);
  assert.equal(pendingCount(p), 1);
  assert.equal(p.inserts.length, 0);
});

test('save script: one transaction; updates by primary key with a concurrency guard; deletes; inserts with defaults', () => {
  const p = emptyPending();
  applyEdit(p, rows, 0, 1, "Acme's");
  applyEdit(p, rows, 0, 2, 'false');
  applyEdit(p, rows, 0, 4, 'note');
  deleteRows(p, 2, [1]);
  p.inserts.push([undefined, 'Gamma', undefined, null, undefined], [undefined, undefined, undefined, undefined, undefined]);
  const s = buildSaveScript(T, columns, rows, ['Id'], p);
  assert.deepEqual([s.updated, s.deleted, s.inserted], [1, 1, 2]);
  assert.equal(s.script, [
    'SET XACT_ABORT ON;',
    'BEGIN TRANSACTION;',
    "UPDATE [dbo].[Cust]]omers] SET [Name] = N'Acme''s', [Active] = 0, [Notes] = N'note' WHERE [Id] = 1 AND [Name] = N'Acme' AND [Active] = 1;",
    "IF @@ROWCOUNT <> 1 BEGIN IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; RAISERROR(N'Row %d was changed or deleted by someone else since it was loaded, so it was not updated. Nothing was saved: reload and try again.', 16, 1, 1); RETURN; END",
    'DELETE FROM [dbo].[Cust]]omers] WHERE [Id] = 2;',
    "IF @@ROWCOUNT <> 1 BEGIN IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; RAISERROR(N'Row %d was changed or deleted by someone else since it was loaded, so it was not deleted. Nothing was saved: reload and try again.', 16, 1, 2); RETURN; END",
    "INSERT INTO [dbo].[Cust]]omers] ([Name], [At]) VALUES (N'Gamma', NULL);",
    'INSERT INTO [dbo].[Cust]]omers] DEFAULT VALUES;',
    'COMMIT TRANSACTION;',
  ].join('\n'));
  const nullGuard = emptyPending();
  applyEdit(nullGuard, rows, 1, 1, 'x');
  assert.match(buildSaveScript(T, columns, rows, ['Id'], nullGuard).script, /WHERE \[Id\] = 2 AND \[Name\] IS NULL;/);
  assert.throws(() => buildSaveScript(T, columns, rows, [], nullGuard), /primary key/);
  const insertOnly = emptyPending();
  insertOnly.inserts.push([undefined, 'a', undefined, undefined, undefined]);
  assert.match(buildSaveScript(T, columns, rows, [], insertOnly).script, /INSERT INTO/);
});

test('pending problems name the row and column', () => {
  const p = emptyPending();
  applyEdit(p, rows, 0, 2, 'maybe');
  p.inserts.push([undefined, undefined, null, undefined, undefined]);
  assert.deepEqual(pendingProblems(p, columns, meta, 2).map(x => [x.row, x.col]), [[0, 2], [2, 2]]);
});

test('edit messages are validated against the grid, the writable columns and the capabilities', () => {
  const o = { gen: 4, loaded: 2, inserted: 1, writable: [false, true, true, true, true], caps: { update: true, delete: true, insert: true } };
  assert.deepEqual(parseEditMessage({ type: 'edit', gen: 4, row: 2, col: 1, value: 'x' }, o), { type: 'edit', row: 2, col: 1, value: 'x' });
  assert.deepEqual(parseEditMessage({ type: 'edit', gen: 4, row: 0, col: 1, value: null }, o), { type: 'edit', row: 0, col: 1, value: null });
  assert.equal(parseEditMessage({ type: 'edit', gen: 3, row: 0, col: 1, value: 'x' }, o), undefined, 'stale gen');
  assert.equal(parseEditMessage({ type: 'edit', gen: 4, row: 0, col: 0, value: '1' }, o), undefined, 'identity column');
  assert.equal(parseEditMessage({ type: 'edit', gen: 4, row: 3, col: 1, value: 'x' }, o), undefined, 'row out of range');
  assert.equal(parseEditMessage({ type: 'edit', gen: 4, row: 0, col: 1, value: 5 }, o), undefined);
  assert.equal(parseEditMessage({ type: 'edit', gen: 4, row: 0, col: 1, value: 'x' }, { ...o, caps: { update: false, delete: false, insert: true } }), undefined);
  assert.deepEqual(parseEditMessage({ type: 'deleteRows', gen: 4, rows: [0, 2], scroll: [5, 0] }, o), { type: 'deleteRows', rows: [0, 2], scroll: [5, 0] });
  assert.equal(parseEditMessage({ type: 'deleteRows', gen: 4, rows: [0, 0] }, o), undefined);
  assert.equal(parseEditMessage({ type: 'deleteRows', gen: 4, rows: [0] }, { ...o, caps: { update: false, delete: false, insert: true } }), undefined);
  assert.deepEqual(parseEditMessage({ type: 'save', gen: 4 }, o), { type: 'save' });
  assert.equal(parseEditMessage({ type: 'addRow', gen: 4 }, { ...o, caps: { update: true, delete: true, insert: false } }), undefined);
});

test('grid HTML marks edited cells, dirty / deleted / new rows and escapes pending values', () => {
  const p = emptyPending();
  applyEdit(p, rows, 0, 1, '<b>x</b>');
  deleteRows(p, 2, [1]);
  p.inserts.push([undefined, 'n', undefined, undefined, undefined]);
  const html = renderGrid({
    id: 'gd', gen: 1, sortMode: 'server', columns, rows,
    edit: { update: true, insert: true, delete: true, writable: [false, true, true, true, true], auto: [true, false, false, false, false],
      edits: p.edits, deletes: p.deletes, inserts: p.inserts, problems: [{ row: 2, col: 1, message: 'bad "value"' }] },
  });
  assert.match(html, /data-edit="uid" data-loaded="2"/);
  assert.match(html, /<tr data-r="0" data-dirty="1">.*<td data-ed="1">&lt;b&gt;x&lt;\/b&gt;<\/td>/);
  assert.match(html, /<tr data-r="1" data-del="1">/);
  assert.match(html, /<tr data-r="2" data-new="1"><th class="rn" title="New row">\*<span class="rh"><\/span><\/th><td class="dflt" data-auto="1">\(auto\)<\/td><td data-ed="1" data-err="1" title="bad &quot;value&quot;">n<\/td>/);
  assert.match(html, /<th data-c="1" data-sort="" data-w="1"/);
  assert.match(html, /<textarea class="gedit"/);
  assert.ok(!renderGrid({ id: 'gd', gen: 1, sortMode: 'server', columns, rows }).includes('data-act="delrows"'), 'no edit menu items when read-only');
});
