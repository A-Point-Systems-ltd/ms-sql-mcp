import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  DEFAULT_TOP, MAX_TOP, TOOLTIP_MAX, cellAt, clampTop, compareGridValues, copyText, dataViewSql, gridSortOrder,
  isNumericType, isSortableType, parseDataViewMessage, parseResultsMessage, parseTop, tooltipText,
} from '../out/grid/gridModel.js';
import { GRID_CSS, gridScript, renderDataView, renderGrid } from '../out/grid/gridHtml.js';

const NONCE = 'n0nce42';
const EVIL = '<script>alert(1)</script>"\'&';
const cols = (...specs) => specs.map(([name, type]) => ({ name, type }));

// --- gridHtml ----------------------------------------------------------------------------------------------------

test('grid HTML escapes cells, headers and tooltips', () => {
  const html = renderGrid({ id: 'g0', columns: cols([EVIL, '"><b>']), rows: [[EVIL]], sortMode: 'local' });
  assert.ok(!html.includes('<script>alert(1)</script>'));
  assert.ok(!html.includes('"><b>'));
  assert.ok(html.includes('<span class="hl">&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;</span>'));
  assert.ok(html.includes('title="&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp; (&quot;&gt;&lt;b&gt;)"'));
  assert.ok(html.includes('<td title="&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;">&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;</td>'));
});

test('grid HTML: row-number gutter with row handles, resize handles, one hover copy button, width variables', () => {
  const html = renderGrid({ id: 'g1', columns: cols(['a', 'int'], ['b', 'nvarchar']), rows: [[1, 'x'], [2, null]], sortMode: 'local', set: 1 });
  assert.equal((html.match(/<th class="rn">\d+<span class="rh"><\/span><\/th>/g) ?? []).length, 2);
  assert.equal((html.match(/<span class="rz"><\/span>/g) ?? []).length, 2);
  assert.equal((html.match(/class="gcopy"/g) ?? []).length, 1);
  assert.match(html, /<button type="button" class="gcopy" title="Copy to clipboard"[^>]*style="display:none"><svg /);
  assert.match(html, /data-set="1"/);
  assert.match(html, /style="--rn:\d+px;--c0:120px;--c1:120px"/);
  assert.match(html, /<tr data-r="1"><th class="rn">2<span class="rh"><\/span><\/th><td title="2">2<\/td><td class="null" data-null="1">NULL<\/td><\/tr>/);
  assert.throws(() => renderGrid({ id: 'x"><', columns: [], rows: [], sortMode: 'local' }));
});

test('tooltips are cut to 2000 characters; the cell keeps the full text', () => {
  const long = 'x'.repeat(5000);
  const html = renderGrid({ id: 'g0', columns: cols(['t', 'nvarchar']), rows: [[long]], sortMode: 'local' });
  const title = /<td title="([^"]*)">/.exec(html)[1];
  assert.equal(title.length, TOOLTIP_MAX);
  assert.ok(title.endsWith('…'));
  assert.ok(html.includes(`>${long}</td>`));
  assert.equal(tooltipText('short'), 'short');
  assert.equal(tooltipText('y'.repeat(2000)), 'y'.repeat(2000));
});

test('server sort mode: indicator on the sorted column, unsortable columns flagged in the tooltip', () => {
  const html = renderGrid({
    id: 'gd', columns: cols(['Id', 'int'], ['Doc', 'xml'], ['Shape', 'geography'], ['Node', 'MyDb.sys.hierarchyid']),
    rows: [], sortMode: 'server', sort: { col: 0, dir: 'desc' },
  });
  assert.match(html, /<th data-c="0" data-sort="desc" data-num="1" title="Id \(int\)">/);
  assert.match(html, /<th data-c="1" data-sort="" data-nosort="1" title="Doc \(xml\) - cannot be sorted \(xml\)">/);
  assert.match(html, /<th data-c="2" data-sort="" data-nosort="1"/);
  assert.match(html, /<th data-c="3" data-sort="" data-nosort="1"/);
  // The indicator glyphs come from the stylesheet.
  assert.match(GRID_CSS, /data-sort="asc"\] \.si::after \{ content: "\\25B2"; \}/);
  assert.match(GRID_CSS, /data-sort="desc"\] \.si::after \{ content: "\\25BC"; \}/);
  // One line by default, wrapping only for an auto-fitted row, capped at 400 px.
  assert.match(GRID_CSS, /white-space: nowrap; overflow: hidden; text-overflow: ellipsis;/);
  assert.match(GRID_CSS, /tr\[data-fit="1"\] > td \{ white-space: pre-wrap;[^}]*max-height: 400px; overflow: auto;/);
});

test('client script: valid JavaScript, inlines the tested comparator, never builds HTML', () => {
  const script = gridScript();
  assert.doesNotThrow(() => new Function(script));
  assert.match(script, /function compareGridValues\(a, b, numeric\)/);
  assert.match(script, /function gridSortOrder\(values, numeric, dir\)/);
  assert.match(script, /function initGrids\(vscode\)/);
  for (const banned of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'exports.', 'eval(', 'fetch(']) {
    assert.ok(!script.includes(banned), banned);
  }
  // Copy posts indexes only; the values never travel back.
  assert.match(script, /type: 'copy', row: Number\(button.getAttribute\('data-r'\)\), col: Number\(button.getAttribute\('data-c'\)\)/);
  assert.match(script, /measureText/);
});

test('Data View page: nonce CSP, TOP box and Reload, no innerHTML, no network, escaped names and errors', () => {
  const html = renderDataView({
    objectName: EVIL, connection: EVIL, top: 200,
    result: { columns: cols(['Id', 'int']), rows: [[1]], truncated: true }, sort: { col: 0, dir: 'asc' },
  }, NONCE);
  assert.ok(html.includes(`content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${NONCE}';"`));
  assert.equal((html.match(/<script/g) ?? []).length, 1);
  assert.ok(html.includes(`<script nonce="${NONCE}">`));
  assert.ok(!html.includes('innerHTML'));
  assert.ok(!/https?:\/\//.test(html));
  assert.ok(!html.includes('<script>alert(1)'));
  assert.match(html, /<input id="top" type="number" min="1" max="10000" step="1" value="200"/);
  assert.match(html, /<button type="button" class="act" id="reload"[^>]*>Reload<\/button>/);
  assert.match(html, /first 1 rows \(truncated\) · 1 column · read-only/);
  assert.match(html, /id="gd" data-mode="server"/);
  assert.match(html, /data-sort="asc"/);
  assert.match(html, /type: 'reload', top: n/);
  assert.match(html, /TOP must be a whole number from 1 to 10000\./);
  assert.ok(html.includes('/^\\d+$/.test(v)'), 'the TOP check regex survives the template literal');
  // The page script is valid JavaScript (acquireVsCodeApi is only called when it runs).
  const script = html.slice(html.indexOf(`<script nonce="${NONCE}">`) + `<script nonce="${NONCE}">`.length, html.lastIndexOf('</script>'));
  assert.doesNotThrow(() => new Function(script));

  const failed = renderDataView({ objectName: 'dbo.T', connection: 'dev', top: 50, error: `No open connections. ${EVIL}` }, NONCE);
  assert.match(failed, /<p class="error">No open connections\. &lt;script&gt;/);
  assert.match(failed, /value="50"/);
  assert.doesNotMatch(failed, /class="dgrid"/);

  const empty = renderDataView({ objectName: 'dbo.T', connection: 'dev', top: 200, result: { columns: cols(['Id', 'int']), rows: [], truncated: false } }, NONCE);
  assert.match(empty, /0 rows · 1 column/);
  assert.match(empty, /<span class="hl">Id<\/span>/, 'zero rows still show the column headers');
  assert.match(empty, /No rows\./);
});

// --- gridModel ---------------------------------------------------------------------------------------------------

test('comparator: NULLs first, numbers by value (exact for decimal text), strings by localeCompare', () => {
  assert.equal(compareGridValues(null, null, false), 0);
  assert.equal(compareGridValues(null, 'a', false), -1);
  assert.equal(compareGridValues('a', null, true), 1);
  assert.ok(compareGridValues('9', '10', true) < 0);
  assert.ok(compareGridValues('9', '10', false) > 0, 'text compare without the numeric flag');
  assert.ok(compareGridValues('-5', '3', true) < 0);
  assert.ok(compareGridValues('-5', '-3', true) < 0);
  assert.equal(compareGridValues('1.50', '1.5', true), 0);
  assert.equal(compareGridValues('-0', '0.00', true), 0);
  assert.equal(compareGridValues('007', '7', true), 0);
  // Beyond double precision: exact.
  assert.ok(compareGridValues('9223372036854775806', '9223372036854775807', true) < 0);
  assert.ok(compareGridValues('12345678901234567890.0000000001', '12345678901234567890', true) > 0);
  assert.ok(compareGridValues('1e+21', '5', true) > 0, 'float text falls back to Number');
  assert.ok(compareGridValues('abc', 'abd', true) < 0, 'non-numbers in a numeric column fall back to text');
  assert.ok(compareGridValues('a', 'B', false) < 0);
});

test('sort order: stable, NULLs first ascending and last descending', () => {
  const values = ['b', null, 'a', 'b', null, 'a'];
  assert.deepEqual(gridSortOrder(values, false, 'asc'), [1, 4, 2, 5, 0, 3]);
  assert.deepEqual(gridSortOrder(values, false, 'desc'), [0, 3, 2, 5, 1, 4]);
  assert.deepEqual(gridSortOrder(['10', '9', '100', '9'], true, 'asc'), [1, 3, 0, 2]);
  assert.deepEqual(gridSortOrder([], true, 'asc'), []);
});

test('sortable and numeric type rules', () => {
  for (const t of ['int', 'nvarchar', 'varchar', 'datetime2', 'uniqueidentifier', 'varbinary', 'bit', 'decimal', 'DATE']) assert.ok(isSortableType(t), t);
  for (const t of ['text', 'ntext', 'image', 'xml', 'geography', 'geometry', 'hierarchyid', 'sql_variant', 'NTEXT', 'MyDb.dbo.MyUdt', 'db.sys.geography', '']) {
    assert.ok(!isSortableType(t), t);
  }
  for (const t of ['bigint', 'int', 'smallint', 'tinyint', 'decimal', 'numeric', 'money', 'smallmoney', 'float', 'real', 'INT']) assert.ok(isNumericType(t), t);
  for (const t of ['varbinary', 'nvarchar', 'bit', 'date']) assert.ok(!isNumericType(t), t);
});

test('Data View SQL: bracket quoting of ], ORDER BY only for sortable columns, TOP bounds', () => {
  const t = { schema: 'sa]les', name: 'Or]ders' };
  assert.equal(dataViewSql(t, 200), 'SELECT TOP (201) * FROM [sa]]les].[Or]]ders]');
  assert.equal(dataViewSql(t, 5, { column: 'a]b', type: 'int', dir: 'asc' }), 'SELECT TOP (6) * FROM [sa]]les].[Or]]ders] ORDER BY [a]]b] ASC');
  assert.equal(dataViewSql(t, 5, { column: 'x', type: 'nvarchar', dir: 'desc' }), 'SELECT TOP (6) * FROM [sa]]les].[Or]]ders] ORDER BY [x] DESC');
  assert.equal(dataViewSql(t, 5, { column: 'doc', type: 'xml', dir: 'asc' }), 'SELECT TOP (6) * FROM [sa]]les].[Or]]ders]');
  assert.equal(dataViewSql(t, 5, { column: 'x', type: 'int', dir: 'sideways' }), 'SELECT TOP (6) * FROM [sa]]les].[Or]]ders]');
  assert.equal(dataViewSql({ name: 'T' }, MAX_TOP), 'SELECT TOP (10001) * FROM [T]');
  for (const bad of [0, -1, 10001, 1.5, NaN, Infinity, '5']) assert.throws(() => dataViewSql(t, bad), RangeError, String(bad));
});

test('TOP parsing and the setting clamp', () => {
  assert.equal(parseTop(1), 1);
  assert.equal(parseTop(10000), 10000);
  assert.equal(parseTop(' 42 '), 42);
  for (const bad of [0, 10001, 2.5, -3, '1e3', '', 'abc', null, undefined, NaN]) assert.equal(parseTop(bad), undefined, String(bad));
  assert.equal(DEFAULT_TOP, 200);
  assert.equal(clampTop(undefined), 200);
  assert.equal(clampTop('500'), 200);
  assert.equal(clampTop(0), 1);
  assert.equal(clampTop(99999), 10000);
  assert.equal(clampTop(12.9), 12);
});

test('copy text: NULL is empty, binary is its 0x text, other values their full display text', () => {
  assert.equal(copyText(null), '');
  assert.equal(copyText(undefined), '');
  assert.equal(copyText('0x00FF'), '0x00FF');
  assert.equal(copyText(42), '42');
  assert.equal(copyText(true), 'true');
  assert.equal(copyText('12.50'), '12.50');
  assert.equal(copyText({ a: 1 }), '{"a":1}');
  const long = 'z'.repeat(70000);
  assert.equal(copyText(long), long, 'not truncated');
  assert.equal(cellAt([[1, 2], [3]], 1, 1), undefined);
  assert.equal(cellAt([[1, 2], [3]], 0, 1), 2);
});

test('Data View message validation: type allow-list, integer indexes in range', () => {
  const dims = { rows: 3, cols: 2 };
  assert.deepEqual(parseDataViewMessage({ type: 'copy', row: 2, col: 1 }, dims), { type: 'copy', row: 2, col: 1 });
  assert.deepEqual(parseDataViewMessage({ type: 'sort', col: 0, dir: 'asc' }, dims), { type: 'sort', col: 0, dir: 'asc' });
  assert.deepEqual(parseDataViewMessage({ type: 'sort', col: 1, dir: 'none' }, dims), { type: 'sort', col: 1, dir: 'none' });
  assert.deepEqual(parseDataViewMessage({ type: 'reload', top: 500 }, dims), { type: 'reload', top: 500 });
  const bad = [
    null, 'copy', [], { type: 'copy', row: 3, col: 0 }, { type: 'copy', row: -1, col: 0 }, { type: 'copy', row: 0.5, col: 0 },
    { type: 'copy', row: '0', col: 0 }, { type: 'copy', row: 0, col: 2 }, { type: 'sort', col: 0, dir: 'up' }, { type: 'sort', col: 9, dir: 'asc' },
    { type: 'reload', top: 0 }, { type: 'reload', top: 10001 }, { type: 'reload', top: '200' }, { type: 'reload', top: 1.5 },
    { type: 'eval', code: 'x' }, { type: 'cancel' }, { type: 'reveal', line: 1 },
  ];
  for (const m of bad) assert.equal(parseDataViewMessage(m, dims), undefined, JSON.stringify(m));
  assert.equal(parseDataViewMessage({ type: 'copy', row: 0, col: 0 }, { rows: 0, cols: 0 }), undefined);
});

test('Results message validation: copy needs a set, row and column in range; reveal and cancel pass', () => {
  const sets = [{ rows: 2, cols: 1 }, { rows: 0, cols: 3 }];
  assert.deepEqual(parseResultsMessage({ type: 'copy', set: 0, row: 1, col: 0 }, sets), { type: 'copy', set: 0, row: 1, col: 0 });
  assert.deepEqual(parseResultsMessage({ type: 'reveal', line: 7 }, sets), { type: 'reveal', line: 7 });
  assert.deepEqual(parseResultsMessage({ type: 'cancel' }, []), { type: 'cancel' });
  const bad = [
    { type: 'copy', set: 1, row: 0, col: 0 }, { type: 'copy', set: 2, row: 0, col: 0 }, { type: 'copy', row: 0, col: 0 },
    { type: 'copy', set: 0, row: 2, col: 0 }, { type: 'copy', set: 0, row: 0, col: 1 }, { type: 'reveal', line: '3' },
    { type: 'reveal', line: 1.5 }, { type: 'sort', col: 0, dir: 'asc' }, { type: 'reload', top: 5 }, undefined,
  ];
  for (const m of bad) assert.equal(parseResultsMessage(m, sets), undefined, JSON.stringify(m));
});
