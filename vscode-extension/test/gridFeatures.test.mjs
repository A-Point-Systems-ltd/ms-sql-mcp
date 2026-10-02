import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  LOAD_CAP_NOTE, MAX_LOADED_ROWS, NO_ORDER_NOTE, ROW_NUMBER_COLUMN, cellMatches, countMatches, dataViewPageSql, dataViewSql,
  defaultViewState, filterMatches, filterPredicate, isTextFilterable, likeEscape, numericStats, orderKeys, parseDataViewMessage,
  parseGridAction, parsePrimaryKey, parseResultsMessage, parseViewState, reconcileFilters, stripRowNumber, usablePrimaryKey, whereSql,
} from '../out/grid/gridModel.js';
import {
  UTF8_BOM, buildCsv, buildJsonRow, buildTsv, csvField, exportConfirmText, exportFileName, exportedMessage, project, tsvField,
} from '../out/grid/gridExport.js';
import { prettyJson, prettyXml, viewerContent, viewerTitle } from '../out/grid/cellFormat.js';

const T = { schema: 'dbo', name: 'Orders' };
const f = (column, op, value = '', type = 'nvarchar') => ({ column, type, op, value });
const cols = (...names) => names.map(n => (Array.isArray(n) ? { name: n[0], type: n[1] } : { name: n, type: 'nvarchar' }));

// --- WHERE / paging SQL ---------------------------------------------------------------------------------------------

test('filter predicates: bracket() columns, sqlString() literals, LIKE with ESCAPE', () => {
  assert.equal(filterPredicate(f('Name', 'contains', 'ab')), "CAST([Name] AS NVARCHAR(MAX)) LIKE N'%ab%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('Name', 'starts', 'ab')), "CAST([Name] AS NVARCHAR(MAX)) LIKE N'ab%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('Name', 'eq', 'ab')), "CAST([Name] AS NVARCHAR(MAX)) = N'ab'");
  assert.equal(filterPredicate(f('Name', 'null')), '[Name] IS NULL');
  assert.equal(filterPredicate(f('Name', 'notnull', 'ignored')), '[Name] IS NOT NULL');
  assert.equal(filterPredicate(f('Name', 'contains', '')), undefined, 'an empty value is inactive');
  assert.equal(filterPredicate(f('Name', 'sideways', 'x')), undefined);
});

test('filter injection attempts stay inside the literal / identifier', () => {
  assert.equal(likeEscape('50%_[x]\\'), '50\\%\\_\\[x]\\\\');
  assert.equal(filterPredicate(f('Na]me', 'eq', "x' OR 1=1 --")), "CAST([Na]]me] AS NVARCHAR(MAX)) = N'x'' OR 1=1 --'");
  assert.equal(filterPredicate(f("a'] ; DROP TABLE t --", 'null')), "[a']] ; DROP TABLE t --] IS NULL");
  assert.equal(filterPredicate(f('c', 'contains', "'; DROP TABLE x; --")), "CAST([c] AS NVARCHAR(MAX)) LIKE N'%''; DROP TABLE x; --%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('c', 'contains', '100%')), "CAST([c] AS NVARCHAR(MAX)) LIKE N'%100\\%%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('c', 'starts', 'a_b')), "CAST([c] AS NVARCHAR(MAX)) LIKE N'a\\_b%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('c', 'contains', '[abc]')), "CAST([c] AS NVARCHAR(MAX)) LIKE N'%\\[abc]%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('c', 'contains', 'C:\\temp')), "CAST([c] AS NVARCHAR(MAX)) LIKE N'%C:\\\\temp%' ESCAPE N'\\'");
  assert.equal(filterPredicate(f('שם', 'eq', 'שלום 😀 Ünï')), "CAST([שם] AS NVARCHAR(MAX)) = N'שלום 😀 Ünï'");
  // The '=' operator does not escape LIKE characters (they are literal there).
  assert.equal(filterPredicate(f('c', 'eq', '50%_[\\')), "CAST([c] AS NVARCHAR(MAX)) = N'50%_[\\'");
});

test('types that cannot be compared as text allow only is null / is not null', () => {
  for (const t of ['image', 'varbinary', 'binary', 'timestamp', 'xml', 'geography', 'geometry', 'hierarchyid', 'sql_variant', 'db.dbo.Udt', '']) {
    assert.ok(!isTextFilterable(t), t);
    assert.equal(filterPredicate(f('c', 'contains', 'x', t)), undefined, t);
    assert.equal(filterPredicate(f('c', 'null', '', t)), '[c] IS NULL', t);
  }
  for (const t of ['nvarchar', 'int', 'datetime2', 'uniqueidentifier', 'text', 'ntext', 'decimal']) assert.ok(isTextFilterable(t), t);
});

test('WHERE joins active filters with AND', () => {
  assert.equal(whereSql([]), '');
  assert.equal(whereSql([f('a', 'contains', '')]), '');
  assert.equal(whereSql([f('a', 'null'), f('b', 'eq', 'x')]), " WHERE [a] IS NULL AND CAST([b] AS NVARCHAR(MAX)) = N'x'");
});

test('ORDER BY keys: sort column, then primary-key tie-breakers', () => {
  assert.deepEqual(orderKeys(undefined, []), []);
  assert.deepEqual(orderKeys(undefined, ['Id']), ['[Id] ASC']);
  assert.deepEqual(orderKeys({ column: 'Name', type: 'nvarchar', dir: 'desc' }, ['Id', 'Line']), ['[Name] DESC', '[Id] ASC', '[Line] ASC']);
  assert.deepEqual(orderKeys({ column: 'Id', type: 'int', dir: 'desc' }, ['Id']), ['[Id] DESC'], 'the sort column is not repeated');
  assert.deepEqual(orderKeys({ column: 'Doc', type: 'xml', dir: 'asc' }, ['K]']), ['[K]]] ASC'], 'unsortable sort ignored');
});

test('first page: TOP (n+1) with WHERE and ORDER BY', () => {
  assert.equal(dataViewSql(T, 200, { column: 'Name', type: 'nvarchar', dir: 'asc' }, { pk: ['Id'], filters: [f('City', 'starts', "O'B")] }),
    "SELECT TOP (201) * FROM [dbo].[Orders] WHERE CAST([City] AS NVARCHAR(MAX)) LIKE N'O''B%' ESCAPE N'\\' ORDER BY [Name] ASC, [Id] ASC");
  assert.equal(dataViewSql(T, 5, undefined, { pk: ['Id'] }), 'SELECT TOP (6) * FROM [dbo].[Orders] ORDER BY [Id] ASC');
});

test('Load more page: ROW_NUMBER paging, validated bounds, (SELECT NULL) without keys', () => {
  assert.equal(dataViewPageSql(T, 201, 401, { column: 'Name', type: 'nvarchar', dir: 'desc' }, { pk: ['Id'], filters: [f('City', 'null')] }),
    'SELECT * FROM (SELECT *, ROW_NUMBER() OVER (ORDER BY [Name] DESC, [Id] ASC) AS [__apms_rn] FROM [dbo].[Orders] WHERE [City] IS NULL) AS q'
    + ' WHERE [__apms_rn] BETWEEN 201 AND 401 ORDER BY [__apms_rn]');
  assert.equal(dataViewPageSql({ name: 'x]' }, 1, 2),
    'SELECT * FROM (SELECT *, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [__apms_rn] FROM [x]]]) AS q WHERE [__apms_rn] BETWEEN 1 AND 2 ORDER BY [__apms_rn]');
  for (const [a, b] of [[0, 5], [5, 4], [1.5, 3], ['1', 3], [1, MAX_LOADED_ROWS + 2], [NaN, 1], [1, Infinity]]) {
    assert.throws(() => dataViewPageSql(T, a, b), RangeError, `${a}..${b}`);
  }
  assert.equal(MAX_LOADED_ROWS, 10000);
  assert.equal(NO_ORDER_NOTE, 'Row order is not guaranteed without a primary key or sort; pages may overlap.');
  assert.equal(LOAD_CAP_NOTE, 'Refine the filter to see more.');
});

test('the paging helper column is stripped from results', () => {
  const set = { columns: [{ name: 'Id', type: 'int' }, { name: ROW_NUMBER_COLUMN, type: 'bigint' }], rows: [[1, 1], [2, 2]], truncated: false };
  assert.deepEqual(stripRowNumber(set), { columns: [{ name: 'Id', type: 'int' }], rows: [[1], [2]], truncated: false });
  const plain = { columns: [{ name: 'Id', type: 'int' }], rows: [[1]] };
  assert.equal(stripRowNumber(plain), plain);
});

test('primary key from describe_table constraints, used only when every key is a result column', () => {
  const data = { constraints: [{ name: 'UQ_x', type: 'UNIQUE_CONSTRAINT', keys: 'Code' }, { name: 'PK_x', type: 'PRIMARY_KEY_CONSTRAINT', keys: 'OrderId,Line' }] };
  assert.deepEqual(parsePrimaryKey(data), ['OrderId', 'Line']);
  assert.deepEqual(parsePrimaryKey({ Constraints: [{ Type: 'PRIMARY_KEY_CONSTRAINT', Keys: 'Id' }] }), ['Id']);
  assert.deepEqual(parsePrimaryKey({ constraints: [] }), []);
  assert.deepEqual(parsePrimaryKey(undefined), []);
  assert.deepEqual(usablePrimaryKey(['OrderId', 'Line'], cols('OrderId', 'Line', 'Qty')), ['OrderId', 'Line']);
  assert.deepEqual(usablePrimaryKey(['a', 'b'], cols('a,b')), [], 'a key column name holding a comma cannot be matched');
});

test('stored filters follow the new columns', () => {
  assert.deepEqual(reconcileFilters([f('A', 'eq', '1'), f('Gone', 'null')], cols(['A', 'varchar'])), [{ column: 'A', type: 'varchar', op: 'eq', value: '1' }]);
});

// --- Message validation -------------------------------------------------------------------------------------------

const dims = { rows: 4, cols: 3 };
const view = (over = {}) => ({ order: [2, 0, 1], hidden: [1], freeze: 1, wrap: false, stripe: true, ...over });

test('view state: a permutation, hidden subset (not all), freeze in range, booleans, optional widths', () => {
  assert.deepEqual(parseViewState(view(), 3), view());
  assert.deepEqual(parseViewState(view({ widths: [40, 120, 600] }), 3), view({ widths: [40, 120, 600] }));
  assert.deepEqual(defaultViewState(3), { order: [0, 1, 2], hidden: [], freeze: 1, wrap: false, stripe: true });
  for (const bad of [view({ order: [0, 1] }), view({ order: [0, 0, 1] }), view({ order: [0, 1, 3] }), view({ hidden: [0, 1, 2] }),
    view({ hidden: [5] }), view({ freeze: 4 }), view({ freeze: -1 }), view({ freeze: 0.5 }), view({ wrap: 'yes' }),
    view({ widths: [40, 120] }), view({ widths: [40, 120, 1e9] }), view({ widths: [40, 120, 10.5] }), null, []]) {
    assert.equal(parseViewState(bad, 3), undefined, JSON.stringify(bad));
  }
});

test('grid actions: gen, indexes and selection rectangles are validated', () => {
  const g = 5;
  assert.deepEqual(parseGridAction({ type: 'copyRow', gen: g, row: 3, cols: [2, 0], format: 'json' }, dims, g), { type: 'copyRow', row: 3, cols: [2, 0], format: 'json' });
  assert.deepEqual(parseGridAction({ type: 'openCell', gen: g, row: 0, col: 2 }, dims, g), { type: 'openCell', row: 0, col: 2 });
  assert.deepEqual(parseGridAction({ type: 'export', gen: g, rows: [3, 1, 0], cols: [1] }, dims, g), { type: 'export', rows: [3, 1, 0], cols: [1] });
  assert.deepEqual(parseGridAction({ type: 'export', gen: g, rows: [], cols: [1] }, dims, g), { type: 'export', rows: [], cols: [1] });
  assert.deepEqual(parseGridAction({ type: 'copySelection', gen: g, rows: [2, 0], cols: [1, 2], r1: 1, c1: 0, r2: 2, c2: 1 }, dims, g),
    { type: 'copySelection', rows: [2, 0], cols: [1, 2], r1: 1, c1: 0, r2: 2, c2: 1 });
  assert.deepEqual(parseGridAction({ type: 'viewState', gen: g, view: view() }, dims, g), { type: 'viewState', view: view() });
  const bad = [
    { type: 'copyRow', gen: 4, row: 0, cols: [0], format: 'tsv' }, { type: 'copyRow', gen: g, row: 4, cols: [0], format: 'tsv' },
    { type: 'copyRow', gen: g, row: 0, cols: [], format: 'tsv' }, { type: 'copyRow', gen: g, row: 0, cols: [0, 0], format: 'tsv' },
    { type: 'copyRow', gen: g, row: 0, cols: [0], format: 'xml' }, { type: 'copyRow', row: 0, cols: [0], format: 'tsv' },
    { type: 'copySelection', gen: g, rows: [0, 1], cols: [0], r1: 0, c1: 0, r2: 0, c2: 0 },
    { type: 'copySelection', gen: g, rows: [0], cols: [0], r1: 1, c1: 0, r2: 0, c2: 0 },
    { type: 'copySelection', gen: g, rows: [0], cols: [0], r1: -1, c1: 0, r2: -1, c2: 0 },
    { type: 'copySelection', gen: g, rows: [0], cols: [0], r1: 9, c1: 0, r2: 9, c2: 0 },
    { type: 'copySelection', gen: g, rows: [9], cols: [0], r1: 0, c1: 0, r2: 0, c2: 0 },
    { type: 'copySelection', gen: g, rows: [0], cols: [0], r1: 0.5, c1: 0, r2: 0.5, c2: 0 },
    { type: 'export', gen: g, rows: [0], cols: [] }, { type: 'export', gen: g, rows: [0, 0], cols: [0] }, { type: 'export', gen: g, rows: '0', cols: [0] },
    { type: 'openCell', gen: g, row: 0, col: 3 }, { type: 'viewState', gen: g, view: view({ order: [0] }) }, { type: 'nope', gen: g },
  ];
  for (const m of bad) assert.equal(parseGridAction(m, dims, g), undefined, JSON.stringify(m));
});

test('Data View messages: filter ops allow-list, text ops refused on binary columns, load more scroll, gen', () => {
  const types = ['nvarchar', 'varbinary', 'int'];
  assert.deepEqual(parseDataViewMessage({ type: 'filter', gen: 2, filters: [{ col: 0, op: 'contains', value: 'x' }, { col: 1, op: 'null', value: 'ignored' }], focus: 0 }, dims, 2, types),
    { type: 'filter', filters: [{ col: 0, op: 'contains', value: 'x' }, { col: 1, op: 'null', value: '' }], focus: 0 });
  assert.deepEqual(parseDataViewMessage({ type: 'filter', gen: 2, filters: [] }, dims, 2, types), { type: 'filter', filters: [] });
  assert.deepEqual(parseDataViewMessage({ type: 'loadMore', gen: 2, scroll: [120, 0] }, dims, 2), { type: 'loadMore', scroll: [120, 0] });
  assert.deepEqual(parseDataViewMessage({ type: 'loadMore', gen: 2, scroll: 'x' }, dims, 2), { type: 'loadMore', scroll: [0, 0] });
  assert.deepEqual(parseDataViewMessage({ type: 'copyRow', gen: 2, row: 1, cols: [0], format: 'tsv' }, dims, 2), { type: 'copyRow', row: 1, cols: [0], format: 'tsv' });
  const bad = [
    { type: 'filter', gen: 1, filters: [] }, { type: 'filter', gen: 2, filters: [{ col: 3, op: 'eq', value: 'x' }] },
    { type: 'filter', gen: 2, filters: [{ col: 0, op: 'like', value: 'x' }] }, { type: 'filter', gen: 2, filters: [{ col: 0, op: 'eq', value: 1 }] },
    { type: 'filter', gen: 2, filters: [{ col: 0, op: 'eq', value: 'x'.repeat(4001) }] },
    { type: 'filter', gen: 2, filters: [{ col: 0, op: 'eq', value: 'a' }, { col: 0, op: 'eq', value: 'b' }] },
    { type: 'filter', gen: 2, filters: [{ col: 1, op: 'contains', value: '0x' }] }, { type: 'filter', gen: 2, filters: 'all' },
    { type: 'loadMore', gen: 1 },
  ];
  for (const m of bad) assert.equal(parseDataViewMessage(m, dims, 2, types), undefined, JSON.stringify(m));
});

test('Results messages: grid actions need a valid set', () => {
  const sets = [{ rows: 2, cols: 2 }];
  assert.deepEqual(parseResultsMessage({ type: 'export', set: 0, gen: 1, rows: [1, 0], cols: [1, 0] }, sets, 1), { type: 'export', set: 0, rows: [1, 0], cols: [1, 0] });
  assert.equal(parseResultsMessage({ type: 'export', set: 1, gen: 1, rows: [], cols: [0] }, sets, 1), undefined);
  assert.equal(parseResultsMessage({ type: 'filter', set: 0, gen: 1, filters: [] }, sets, 1), undefined, 'filters are local in Results');
  assert.equal(parseResultsMessage({ type: 'loadMore', set: 0, gen: 1 }, sets, 1), undefined);
});

// --- Export builders ----------------------------------------------------------------------------------------------

const ecols = names => names.map(name => ({ name }));

test('TSV: tabs and line breaks become spaces, NULL is empty, header optional', () => {
  assert.equal(tsvField('a\tb\r\nc\nd\re'), 'a b c d e');
  assert.equal(tsvField(null), '');
  assert.equal(buildTsv(undefined, [[1, 'x\ty'], [null, '0x00FF']]), '1\tx y\r\n\t0x00FF');
  assert.equal(buildTsv(ecols(['Id', '', 'a\tb']), [[1, 2, 3]]), 'Id\t(No column name)\ta b\r\n1\t2\t3');
});

test('CSV: BOM, RFC 4180 quoting, CRLF, NULL empty, Hebrew kept', () => {
  assert.equal(UTF8_BOM.charCodeAt(0), 0xfeff);
  assert.equal(csvField('plain'), 'plain');
  assert.equal(csvField('a,b'), '"a,b"');
  assert.equal(csvField('say "hi"'), '"say ""hi"""');
  assert.equal(csvField('two\nlines'), '"two\nlines"');
  assert.equal(csvField(null), '');
  const csv = buildCsv(ecols(['Id', 'שם']), [[1, 'שלום, עולם'], [2, null]]);
  assert.equal(csv, `${UTF8_BOM}Id,שם\r\n1,"שלום, עולם"\r\n2,\r\n`);
  assert.ok(Buffer.from(csv, 'utf8').subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])), 'UTF-8 BOM bytes');
});

test('projection follows hidden columns and display order; JSON row keys and nulls', () => {
  const rows = [[1, 'a', 'x'], [2, 'b', null]];
  assert.deepEqual(project(rows, [1, 0], [2, 0]), [[null, 2], ['x', 1]]);
  assert.deepEqual(project([[1]], [0], [0, 3]), [[1, undefined]]);
  assert.equal(buildJsonRow(ecols(['Id', 'Name', 'Name', '']), [7, null, '12.50', undefined]),
    JSON.stringify({ Id: 7, Name: null, 'Name (2)': '12.50', '(No column name)': null }, null, 2));
});

test('export texts and the default file name', () => {
  assert.equal(exportConfirmText(1200), "Export 1,200 rows to a CSV file? The data may contain personal information; keep the file inside the company. Text values that start with = + - @ are prefixed with ' so Excel does not run them as formulas.");
  assert.equal(exportConfirmText(1, false), 'Export 1 row to a CSV file? The data may contain personal information; keep the file inside the company.');
  assert.equal(exportedMessage(3, 'C:\\x\\a.csv'), 'Exported 3 rows to C:\\x\\a.csv');
  const d = new Date(2026, 9, 2, 7, 5);
  assert.equal(exportFileName('dbo.Orders', d), 'dbo.Orders_20261002_0705.csv');
  assert.equal(exportFileName('a/b:c*?', d), 'a_b_c___20261002_0705.csv');
  assert.equal(exportFileName('', d), 'results_20261002_0705.csv');
});

// --- Selection stats, search, local filter ------------------------------------------------------------------------

test('decimal sum is exact (no float error), with min, max and avg', () => {
  const s = numericStats(['0.1', '0.2', '0.3']);
  assert.deepEqual(s, { count: 3, sum: '0.6', min: '0.1', max: '0.3', avg: '0.2', exact: true });
  assert.equal(numericStats(['9223372036854775807', '1']).sum, '9223372036854775808');
  assert.equal(numericStats(['12345678901234567890.12', '0.005']).sum, '12345678901234567890.125');
  assert.deepEqual(numericStats(['-1.50', '1.5']), { count: 2, sum: '0.00', min: '-1.50', max: '1.5', avg: '0', exact: true });
  assert.equal(numericStats(['1', '2']).avg, '1.5');
  assert.equal(numericStats(['1', '1', '2']).avg, '1.333333');
  assert.equal(numericStats(['-1', '-1', '-2']).avg, '-1.333333');
  assert.equal(numericStats(['2', '0', '0']).avg, '0.666667');
  assert.equal(numericStats(['', 'abc', '5']).count, 1);
  assert.equal(numericStats([]), undefined);
  const approx = numericStats(['1e3', '0.5']);
  assert.equal(approx.exact, false);
  assert.equal(approx.sum, '1000.5');
});

test('quick search and local filters', () => {
  assert.ok(cellMatches('Hello World', 'o w'));
  assert.ok(!cellMatches('Hello', ''));
  assert.equal(countMatches(['שלום', 'abc', 'ABC', 'x'], 'abc'), 2);
  assert.equal(countMatches([], 'a'), 0);
  assert.ok(filterMatches('Tel Aviv', 'contains', 'aviv'));
  assert.ok(filterMatches('Tel Aviv', 'starts', 'tel'));
  assert.ok(!filterMatches('Tel Aviv', 'starts', 'aviv'));
  assert.ok(filterMatches('ABC', 'eq', 'abc'));
  assert.ok(!filterMatches(null, 'contains', 'a'));
  assert.ok(filterMatches(null, 'contains', ''), 'empty operand is inactive');
  assert.ok(filterMatches(null, 'null', ''));
  assert.ok(!filterMatches('x', 'null', ''));
  assert.ok(filterMatches('x', 'notnull', ''));
});

// --- Cell viewer ----------------------------------------------------------------------------------------------------

test('JSON pretty-print keeps numbers exactly; non-objects are not JSON', () => {
  assert.equal(prettyJson('{"a":1,"b":[1,2],"c":{},"d":"x,{y}:\\"z\\""}'), '{\n  "a": 1,\n  "b": [\n    1,\n    2\n  ],\n  "c": {},\n  "d": "x,{y}:\\"z\\""\n}');
  assert.equal(prettyJson('{"id":12345678901234567890}'), '{\n  "id": 12345678901234567890\n}');
  assert.equal(prettyJson('[]'), '[]');
  assert.equal(prettyJson('"text"'), undefined);
  assert.equal(prettyJson('42'), undefined);
  assert.equal(prettyJson('{bad json}'), undefined);
});

test('XML indent: nested elements, text-only elements on one line, malformed is refused', () => {
  assert.equal(prettyXml('<?xml version="1.0"?><root a="1"><item>x</item><item/><!-- c --><b><c>1</c></b></root>'),
    '<?xml version="1.0"?>\n<root a="1">\n  <item>x</item>\n  <item/>\n  <!-- c -->\n  <b>\n    <c>1</c>\n  </b>\n</root>');
  assert.equal(prettyXml('<a><b></a>'), undefined);
  assert.equal(prettyXml('<a>'), undefined);
  assert.equal(prettyXml('not xml'), undefined);
  assert.equal(prettyXml('<a><![CDATA[<x>]]></a>'), '<a>\n  <![CDATA[<x>]]>\n</a>');
});

test('viewer content and title', () => {
  assert.deepEqual(viewerContent('{"a":1}'), { text: '{\n  "a": 1\n}', language: 'json' });
  assert.deepEqual(viewerContent('<a><b>1</b></a>'), { text: '<a>\n  <b>1</b>\n</a>', language: 'xml' });
  assert.deepEqual(viewerContent('0x00FF'), { text: '0x00FF', language: 'plaintext' });
  assert.deepEqual(viewerContent('<not closed'), { text: '<not closed', language: 'plaintext' });
  assert.deepEqual(viewerContent(null), { text: 'NULL', language: 'plaintext' });
  assert.deepEqual(viewerContent(12.5), { text: '12.5', language: 'plaintext' });
  assert.equal(viewerTitle('Notes', 0, 'dbo.Orders'), 'Notes · row 1 - dbo.Orders');
  assert.equal(viewerTitle('', 4, 'results'), '(No column name) · row 5 - results');
});

// --- Grid HTML (Task 3 parts) --------------------------------------------------------------------------------------

import { gridScript, renderDataView, renderGrid } from '../out/grid/gridHtml.js';

const EVIL = '<script>alert(1)</script>"\'&';

test('toolbar, filter row, columns checklist and menus: escaped, keyboard-accessible buttons', () => {
  const html = renderGrid({
    id: 'gd', gen: 3, sortMode: 'server', columns: [{ name: EVIL, type: 'nvarchar' }, { name: 'Bin', type: 'varbinary' }], rows: [[EVIL, '0x00']],
    filters: [{ col: 0, op: 'starts', value: EVIL }, { col: 1, op: 'null', value: '' }],
  });
  assert.ok(!html.includes('<script>alert(1)'));
  assert.ok(!html.includes(EVIL), 'the raw value never appears unescaped');
  assert.ok(!html.includes('"\'&'), 'no attribute break-out');
  assert.match(html, /<div class="gbar" role="toolbar" aria-label="Grid tools">/);
  for (const act of ['columns', 'wrap', 'stripe', 'prev', 'next', 'export']) assert.match(html, new RegExp(`<button type="button" class="tb" data-act="${act}"`), act);
  assert.match(html, /data-act="wrap" title="Wrap all rows" aria-pressed="false"/);
  assert.match(html, /data-act="stripe" title="Row striping" aria-pressed="true"/);
  assert.match(html, /<input class="gq" type="search" placeholder="Search" aria-label="Search loaded rows">/);
  // Filter row: the stored value is escaped into the input, the operator is selected.
  assert.match(html, /<option value="starts" selected>starts with<\/option>/);
  assert.ok(html.includes(`value="&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;"`));
  // A varbinary column offers only any / is null / is not null, its input disabled.
  assert.match(html, /<th class="fc" data-fc="1" data-textless="1"><select aria-label="Filter operator for Bin"><option value="contains">any<\/option><option value="null" selected>is null<\/option><option value="notnull">is not null<\/option><\/select><input type="text" data-c="1" aria-label="Filter Bin" placeholder="filter" value="" disabled><\/th>/);
  // Columns checklist and menu items.
  assert.ok(html.includes(`<label><input type="checkbox" data-col="0" checked> &lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;</label>`));
  assert.match(html, /<button type="button" data-act="showall">Show all<\/button>/);
  for (const label of ['Freeze up to here', 'Unfreeze columns', 'Hide column', 'Copy', 'Copy cell', 'Copy row (tab-separated)', 'Copy row (JSON)', 'Open in viewer', 'Select all']) {
    assert.ok(new RegExp(`role="menuitem"[^>]*>([^<]*)</button>`, 'g').test(html), label);
    assert.ok(html.includes(`>${label}</button>`), label);
  }
  assert.match(html, /class="ghover gview" title="Open in viewer"/);
  assert.match(html, /class="ghover growbtn" title="Row actions"/);
  // Headers are draggable (reorder); the dynamic style element is empty until the script writes integer rules.
  assert.equal((html.match(/draggable="true"/g) ?? []).length, 2);
  assert.match(html, /<style class="gdyn"><\/style>/);
});

test('view state, focus and scroll render as integer data attributes', () => {
  const html = renderGrid({
    id: 'g0', gen: 1, sortMode: 'local', columns: [{ name: 'a', type: 'int' }, { name: 'b', type: 'int' }], rows: [],
    view: { order: [1, 0], hidden: [0], freeze: 2, wrap: true, stripe: false, widths: [80, 200] }, focus: 1, scroll: [300, 20],
  });
  assert.match(html, /data-order="1,0" data-hidden="0" data-freeze="2" data-wrap="1" data-stripe="0" data-widths="80,200" data-scroll="300,20" data-focus="1"/);
  assert.match(html, /--c0:80px;--c1:200px/);
});

test('Data View page: Load more only when offered, toolbar in the grid, scripts parse, no innerHTML', () => {
  const model = { objectName: 'dbo.T', connection: 'dev', top: 200, gen: 4, result: { columns: [{ name: 'Id', type: 'int' }], rows: [[1]], truncated: true } };
  const more = renderDataView({ ...model, canLoadMore: true }, 'nn');
  assert.match(more, /<button type="button" class="tb" id="more"[^>]*><span>Load more<\/span><\/button>/);
  assert.doesNotMatch(more, /id="more"[^>]*style="display:none"/);
  assert.match(renderDataView(model, 'nn'), /id="more"[^>]*style="display:none"/);
  assert.match(more, /type: 'loadMore', gen: Number\(grid.getAttribute\('data-gen'\)\)/);
  assert.ok(!more.includes('innerHTML'));
  const script = more.slice(more.indexOf('<script nonce="nn">') + 19, more.lastIndexOf('</script>'));
  assert.doesNotThrow(() => new Function(script));
  // An error with a previous result keeps the grid (and its filter row) under the error text.
  const err = renderDataView({ ...model, error: 'Invalid column name.' }, 'nn');
  assert.match(err, /<p class="error">Invalid column name\.<\/p>/);
  assert.match(err, /class="dgrid" id="gd"/);
  // Without a result the TOP box and Reload stay for a retry.
  const failed = renderDataView({ objectName: 'dbo.T', connection: 'dev', top: 200, gen: 1, error: 'No open connections.' }, 'nn');
  assert.match(failed, /id="reload"/);
});

test('client script: menus, selection, filters, search and export post only indexes', () => {
  const s = gridScript();
  assert.match(s, /type: 'copySelection', gen: gen, rows: disp\.rows\.slice\(s\.r1, s\.r2 \+ 1\), cols: disp\.cols\.slice\(s\.c1, s\.c2 \+ 1\)/);
  assert.match(s, /type: 'export', gen: gen, rows: disp\.rows\.slice\(\), cols: disp\.cols\.slice\(\)/);
  assert.match(s, /type: 'copyRow', gen: gen, row: m\.row, cols: disp\.cols\.slice\(\), format:/);
  assert.match(s, /type: 'filter', gen: gen, filters: readFilters\(\)/);
  assert.match(s, /dyn\.textContent = css\.join/);
  assert.match(s, /SERVER_DEBOUNCE = 400/);
  for (const banned of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'appendChild', 'createElement(\'div', 'eval(']) {
    assert.ok(!s.includes(banned), banned);
  }
});

// --- Fix round 1 ---------------------------------------------------------------------------------------------------

import { NO_PK_NOTE, displayedParams, errorNamesKey, nextPageRequest, pagingNote, sortIndicator } from '../out/grid/gridModel.js';
import { isTextColumn, neutralizedCsvField } from '../out/grid/gridExport.js';

test('first query fails: the old parameters stay shown, and Load more pages with them from the old offset', () => {
  const columns = cols(['Id', 'int'], 'Name', 'City');
  const loadedWith = { sort: { column: 'Name', type: 'nvarchar', dir: 'asc' }, filters: [f('City', 'eq', 'Haifa')], pk: ['Id'], top: 50 };
  // The user sorted by City desc and filtered on a bad value; that query failed.
  const requested = { sort: { column: 'City', type: 'nvarchar', dir: 'desc' }, filters: [f('City', 'contains', 'x')] };
  const shown = displayedParams(requested, loadedWith, true);
  assert.deepEqual(shown, { sort: loadedWith.sort, filters: loadedWith.filters });
  assert.deepEqual(sortIndicator(shown.sort, columns), { col: 1, dir: 'asc' }, 'the indicator matches the loaded rows');
  // A success shows what was asked for; without loaded rows a failure keeps it too.
  assert.deepEqual(displayedParams(requested, loadedWith, false), requested);
  assert.deepEqual(displayedParams(requested, undefined, true), requested);
  // 120 rows on screen: the next page is 121..171 (one extra row), maxRows 50, with the loaded rows' sort and filter.
  const page = nextPageRequest(T, loadedWith, 120);
  assert.deepEqual({ from: page.from, to: page.to, maxRows: page.maxRows }, { from: 121, to: 171, maxRows: 50 });
  assert.equal(page.script, "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (ORDER BY [Name] ASC, [Id] ASC) AS [__apms_rn] FROM [dbo].[Orders]"
    + " WHERE CAST([City] AS NVARCHAR(MAX)) = N'Haifa') AS q WHERE [__apms_rn] BETWEEN 121 AND 171 ORDER BY [__apms_rn]");
  assert.ok(!page.script.includes("N'%x%'") && !page.script.includes('[City] DESC'), 'never the failed parameters');
  assert.equal(nextPageRequest(T, loadedWith, 9980).maxRows, 20, 'the last page stops at the cap');
  assert.equal(nextPageRequest(T, loadedWith, 10000), undefined);
  assert.equal(sortIndicator(undefined, columns), undefined);
  assert.equal(sortIndicator({ column: 'Gone', type: 'int', dir: 'asc' }, columns), undefined);
});

test('paging notes: none with a primary key; the overlap note without one, sorted or not', () => {
  assert.equal(pagingNote({ column: 'Name', type: 'nvarchar', dir: 'asc' }, ['Id']), undefined);
  assert.equal(pagingNote({ column: 'Name', type: 'nvarchar', dir: 'asc' }, []), NO_PK_NOTE);
  assert.equal(NO_PK_NOTE, 'Without a primary key, rows with equal sort values may repeat or be skipped between pages.');
  assert.equal(pagingNote(undefined, []), NO_ORDER_NOTE);
});

test('the primary key is dropped from a retry only when the error names a key column', () => {
  assert.ok(errorNamesKey("Invalid column name 'Order,Id'.", ['Order', 'Id']));
  assert.ok(errorNamesKey("Invalid column name 'ORDERID'.", ['OrderId']));
  assert.ok(!errorNamesKey('Arithmetic overflow error converting expression.', ['OrderId']));
  assert.ok(!errorNamesKey('anything', []));
});

test('CSV formula neutralization: text columns only, quoted with a leading quote; others raw', () => {
  const columns = [{ name: 'Note', type: 'nvarchar' }, { name: 'Amount', type: 'decimal' }, { name: 'When', type: 'datetime2' }, { name: 'Bin', type: 'varbinary' }];
  const csv = buildCsv(columns, [['=1+2', '-5.00', '2026-10-02', '0x00'], ['+cmd', '-1', '-x', '0x01'], ['@SUM(A1)', null, null, null], ['\tx', null, null, null], ['\rx', null, null, null], ['plain', null, null, null], ['a=b', null, null, null]]);
  const lines = csv.slice(1).split('\r\n');
  assert.equal(lines[1], '"\'=1+2",-5.00,2026-10-02,0x00');
  assert.equal(lines[2], '"\'+cmd",-1,-x,0x01', 'a date / numeric / binary column is never prefixed');
  assert.equal(lines[3], '"\'@SUM(A1)",,,');
  assert.equal(lines[4], '"\'\tx",,,');
  assert.equal(lines[5], '"\'\rx",,,');
  assert.equal(lines[6], 'plain,,,');
  assert.equal(lines[7], 'a=b,,,', 'only a leading character counts');
  assert.equal(neutralizedCsvField('-"q"'), '"\'-""q"""');
  assert.equal(neutralizedCsvField(null), '');
  // Turned off: raw values.
  assert.equal(buildCsv(columns, [['=1+2', '1', '', '']], false).slice(1).split('\r\n')[1], '=1+2,1,,');
  for (const t of ['nvarchar', 'VARCHAR', 'char', 'nchar', 'text', 'ntext', 'sysname', 'xml']) assert.ok(isTextColumn(t), t);
  for (const t of ['int', 'decimal', 'datetime', 'date', 'varbinary', 'uniqueidentifier', undefined]) assert.ok(!isTextColumn(t), String(t));
  // TSV copy stays raw.
  assert.equal(buildTsv(undefined, [['=1+2', '@x']]), '=1+2\t@x');
});
