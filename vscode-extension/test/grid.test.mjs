import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  DEFAULT_TOP, MAX_TOP, SORT_COLUMN_GONE, TOOLTIP_MAX, cellAt, clampTop, compareGridValues, copiedMessage, copyText,
  dataViewSql, gridSortOrder, isNumericType, isSortableType, parseDataViewMessage, parseResultsMessage, parseTop,
  reconcileSort, tooltipText,
} from '../out/grid/gridModel.js';
import { GRID_CSS, INLINED_FUNCTIONS, gridScript, renderDataView, renderGrid } from '../out/grid/gridHtml.js';

const NONCE = 'n0nce42';
const EVIL = '<script>alert(1)</script>"\'&';
const cols = (...specs) => specs.map(([name, type]) => ({ name, type }));
const grid = spec => renderGrid({ gen: 1, ...spec });

// --- gridHtml ----------------------------------------------------------------------------------------------------

test('grid HTML escapes cells, headers and tooltips', () => {
  const cut = `${EVIL}… (truncated, 70000 chars)`;
  const html = grid({ id: 'g0', columns: cols([EVIL, '"><b>'], ['t', 'nvarchar']), rows: [[EVIL, cut]], sortMode: 'local' });
  assert.ok(!html.includes('<script>alert(1)</script>'));
  assert.ok(!html.includes('"><b>'));
  assert.ok(html.includes('<span class="hl">&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;</span>'));
  assert.ok(html.includes('title="&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp; (&quot;&gt;&lt;b&gt;)"'));
  assert.ok(html.includes('<td>&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;</td>'));
  // The only server-rendered cell tooltip (a server-truncated value) is escaped too.
  assert.ok(html.includes('<td class="trunc" title="&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#39;&amp;… (truncated, 70000 chars)\n\nTruncated by the server: the full value has 70000 chars.">'));
});

test('cell tooltips are lazy: no title on ordinary cells, the script sets it on hover from textContent', () => {
  const html = grid({ id: 'g0', columns: cols(['a', 'int'], ['b', 'nvarchar']), rows: [[1, 'x'], [2, null]], sortMode: 'local' });
  assert.equal((html.match(/<td[^>]* title=/g) ?? []).length, 0);
  assert.match(gridScript(), /td\.setAttribute\('title', tooltipText\(td\.textContent\)\)/);
});

test('grid HTML: render gen, row-number gutter with row handles, resize handles, one hover copy button, width variables', () => {
  const html = renderGrid({ id: 'g1', columns: cols(['a', 'int'], ['b', 'nvarchar']), rows: [[1, 'x'], [2, null]], sortMode: 'local', set: 1, gen: 7 });
  assert.equal((html.match(/<th class="rn">\d+<span class="rh"><\/span><\/th>/g) ?? []).length, 2);
  assert.equal((html.match(/<span class="rz"><\/span>/g) ?? []).length, 2);
  assert.equal((html.match(/class="ghover gcopy"/g) ?? []).length, 1);
  assert.equal((html.match(/class="ghover gview"/g) ?? []).length, 1);
  assert.match(html, /<button type="button" class="ghover gcopy" title="Copy to clipboard"[^>]*style="display:none"><svg /);
  assert.match(html, /class="dgrid" id="g1" data-mode="local" data-set="1" data-gen="7" data-freeze="1" data-wrap="0" data-stripe="1"/);
  assert.match(html, /style="--rn:\d+px;--c0:120px;--c1:120px"/);
  assert.match(html, /<tr data-r="1"><th class="rn">2<span class="rh"><\/span><\/th><td>2<\/td><td class="null" data-null="1">NULL<\/td><\/tr>/);
  assert.throws(() => grid({ id: 'x"><', columns: [], rows: [], sortMode: 'local' }));
});

test('tooltips are cut to 2000 characters', () => {
  const t = tooltipText('x'.repeat(5000));
  assert.equal(t.length, TOOLTIP_MAX);
  assert.ok(t.endsWith('…'));
  assert.equal(tooltipText('short'), 'short');
  assert.equal(tooltipText('y'.repeat(2000)), 'y'.repeat(2000));
  const long = 'z'.repeat(5000);
  assert.ok(grid({ id: 'g0', columns: cols(['t', 'nvarchar']), rows: [[long]], sortMode: 'local' }).includes(`<td>${long}</td>`), 'the cell keeps the full text');
});

test('server sort mode: indicator on the sorted column, unsortable columns flagged in the tooltip', () => {
  const html = grid({
    id: 'gd', columns: cols(['Id', 'int'], ['Doc', 'xml'], ['Shape', 'geography'], ['Node', 'MyDb.sys.hierarchyid']),
    rows: [], sortMode: 'server', sort: { col: 0, dir: 'desc' },
  });
  assert.match(html, /<th data-c="0" data-sort="desc" data-num="1" draggable="true" title="Id \(int\)">/);
  assert.match(html, /<th data-c="1" data-sort="" data-nosort="1" draggable="true" title="Doc \(xml\) - cannot be sorted \(xml\)">/);
  assert.match(html, /<th data-c="2" data-sort="" data-nosort="1"/);
  assert.match(html, /<th data-c="3" data-sort="" data-nosort="1"/);
  // The indicator glyphs come from the stylesheet.
  assert.match(GRID_CSS, /data-sort="asc"\] \.si::after \{ content: "\\25B2"; \}/);
  assert.match(GRID_CSS, /data-sort="desc"\] \.si::after \{ content: "\\25BC"; \}/);
  // One line by default, wrapping only for an auto-fitted row, capped at 400 px.
  assert.match(GRID_CSS, /white-space: nowrap; overflow: hidden; text-overflow: ellipsis;/);
  assert.match(GRID_CSS, /tr\[data-fit="1"\] > td, \.dgrid\[data-wrap="1"\] \.gt tbody tr:not\(\[data-fit="0"\]\) > td \{ white-space: pre-wrap;[^}]*max-height: 400px; overflow: auto;/);
});

test('client script: valid JavaScript, never builds HTML, posts indexes and gen only', () => {
  const script = gridScript();
  assert.doesNotThrow(() => new Function(script));
  assert.match(script, /function initGrids\(vscode\)/);
  for (const banned of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'exports.', 'eval(', 'fetch(']) {
    assert.ok(!script.includes(banned), banned);
  }
  assert.match(script, /type: type, gen: gen, row: Number\(btn.getAttribute\('data-r'\)\), col: Number\(btn.getAttribute\('data-c'\)\)/);
  assert.match(script, /hoverPost\(copyBtn, 'copy'\)/);
  assert.match(script, /measureText/);
  // Drags write once per frame; local text sort uses one shared collator.
  assert.match(script, /requestAnimationFrame\(applyDrag\)/);
  assert.match(script, /new Intl\.Collator\(\)/);
  assert.match(script, /gridSortOrder\(values, .*, next, collator\.compare\)/);
  // Auto-fit measures all columns before writing any width.
  assert.match(script, /result\.push\([^]*for \(var j = 0; j < cols\.length; j\+\+\) setWidth\(cols\[j\], result\[j\]\)/);
});

test('build guard: the toString-inlined gridModel functions are present by name and run in isolation', () => {
  const script = gridScript();
  assert.deepEqual([...INLINED_FUNCTIONS], ['compareGridValues', 'gridSortOrder', 'tooltipText', 'cellMatches', 'countMatches', 'filterMatches', 'numericStats']);
  for (const name of INLINED_FUNCTIONS) assert.match(script, new RegExp(`function ${name}\\(`), name);
  // Only the script's own text is in scope here: a reference to a module helper or constant would throw.
  const inlined = new Function(`${script}\nreturn { ${INLINED_FUNCTIONS.join(', ')} };`)();
  assert.deepEqual(inlined.gridSortOrder(['b', null, '10', '9'], false, 'asc'), [1, 2, 3, 0]);
  assert.deepEqual(inlined.gridSortOrder(['10', '9', null], true, 'desc'), [0, 1, 2]);
  assert.equal(inlined.compareGridValues('1.50', '1.5', true), 0);
  assert.equal(inlined.tooltipText('q'.repeat(3000)).length, TOOLTIP_MAX);
  assert.equal(inlined.cellMatches('Hello', 'ELL'), true);
  assert.equal(inlined.countMatches(['ab', 'AB', 'c'], 'b'), 2);
  assert.equal(inlined.filterMatches('Tel Aviv', 'starts', 'tel'), true);
  assert.deepEqual(inlined.numericStats(['0.1', '0.2']), { count: 2, sum: '0.3', min: '0.1', max: '0.2', avg: '0.15', exact: true });
  assert.equal(Object.keys(inlined).length, INLINED_FUNCTIONS.length);
  for (const name of INLINED_FUNCTIONS) assert.equal(typeof inlined[name], 'function', name);
});

test('Data View page: nonce CSP, TOP box and Reload, no innerHTML, no network, escaped names, errors and notes', () => {
  const html = renderDataView({
    objectName: EVIL, connection: EVIL, top: 200, gen: 3, notes: [SORT_COLUMN_GONE, EVIL],
    result: { columns: cols(['Id', 'int']), rows: [[1]], truncated: true }, sort: { col: 0, dir: 'asc' },
  }, NONCE);
  assert.ok(html.includes(`content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${NONCE}';"`));
  assert.equal((html.match(/<script/g) ?? []).length, 1);
  assert.ok(html.includes(`<script nonce="${NONCE}">`));
  assert.ok(!html.includes('innerHTML'));
  assert.ok(!/https?:\/\//.test(html));
  assert.ok(!html.includes('<script>alert(1)'));
  assert.match(html, /<input id="top" type="number" min="1" max="10000" step="1" value="200"/);
  assert.match(html, /<button type="button" class="tb" id="reload"[^>]*><span>Reload<\/span><\/button>/);
  assert.match(html, /first 1 rows \(truncated\) · 1 column · read-only/);
  assert.match(html, /id="gd" data-mode="server" data-gen="3"/);
  assert.match(html, /data-sort="asc"/);
  assert.match(html, /<div class="note">Sort column no longer exists<\/div>/);
  assert.match(html, /<div class="note">&lt;script&gt;/);
  assert.ok(html.includes("type: 'reload', gen: Number(document.body.getAttribute('data-gen')), top: n"));
  assert.match(html, /<body data-gen="3">/);
  assert.match(html, /TOP must be a whole number from 1 to 10000\./);
  assert.ok(html.includes('/^\\d+$/.test(v)'), 'the TOP check regex survives the template literal');
  const script = html.slice(html.indexOf(`<script nonce="${NONCE}">`) + `<script nonce="${NONCE}">`.length, html.lastIndexOf('</script>'));
  assert.doesNotThrow(() => new Function(script));

  const failed = renderDataView({ objectName: 'dbo.T', connection: 'dev', top: 50, gen: 1, error: `No open connections. ${EVIL}` }, NONCE);
  assert.match(failed, /<p class="error">No open connections\. &lt;script&gt;/);
  assert.match(failed, /value="50"/);
  assert.doesNotMatch(failed, /class="dgrid"/);

  const empty = renderDataView({ objectName: 'dbo.T', connection: 'dev', top: 200, gen: 1, result: { columns: cols(['Id', 'int']), rows: [], truncated: false } }, NONCE);
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
  assert.ok(compareGridValues('9223372036854775806', '9223372036854775807', true) < 0);
  assert.ok(compareGridValues('12345678901234567890.0000000001', '12345678901234567890', true) > 0);
  assert.ok(compareGridValues('1e+21', '5', true) > 0, 'float text falls back to Number');
  assert.ok(compareGridValues('abc', 'abd', true) < 0, 'non-numbers in a numeric column fall back to text');
  assert.ok(compareGridValues('a', 'B', false) < 0);
});

test('comparator: an optional compare function (a shared Intl.Collator) replaces localeCompare for text only', () => {
  const calls = [];
  const spy = (x, y) => { calls.push([x, y]); return x < y ? -1 : x > y ? 1 : 0; };
  assert.ok(compareGridValues('a', 'B', false, spy) > 0, 'the given compare decides (code-point order here)');
  assert.deepEqual(calls, [['a', 'B']]);
  assert.ok(compareGridValues('9', '10', true, spy) < 0);
  assert.equal(compareGridValues(null, 'x', false, spy), -1);
  assert.equal(calls.length, 1, 'numbers and NULLs never reach it');
  const collator = new Intl.Collator();
  assert.deepEqual(gridSortOrder(['b', 'A', 'a', null], false, 'asc', collator.compare), [3, 2, 1, 0]);
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

test('stored sort vs a new result: kept with the current type, or cleared with a note', () => {
  const sort = { column: 'Name', type: 'nvarchar', dir: 'desc' };
  assert.deepEqual(reconcileSort(undefined, cols(['Name', 'nvarchar'])), { col: -1 });
  assert.deepEqual(reconcileSort(sort, cols(['Id', 'int'], ['Name', 'varchar'])), { sort: { column: 'Name', type: 'varchar', dir: 'desc' }, col: 1 });
  assert.deepEqual(reconcileSort(sort, cols(['Id', 'int'], ['FullName', 'nvarchar'])), { col: -1, note: SORT_COLUMN_GONE });
  assert.deepEqual(reconcileSort(sort, cols(['Name', 'ntext'])), { col: -1, note: SORT_COLUMN_GONE }, 'now unsortable');
  assert.equal(SORT_COLUMN_GONE, 'Sort column no longer exists');
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

test('copy status: says when the server truncated the value', () => {
  assert.equal(copiedMessage('abc'), 'Copied');
  assert.equal(copiedMessage(''), 'Copied');
  assert.equal(copiedMessage('abc… (truncated, 70000 chars)'), 'Copied (value truncated by the server)');
  assert.equal(copiedMessage('0x41… (truncated, 40000 bytes)'), 'Copied (value truncated by the server)');
  assert.equal(copiedMessage('(truncated, 5 chars) is just text'), 'Copied');
});

test('Data View message validation: type allow-list, integer indexes in range, copy gen must be current', () => {
  const dims = { rows: 3, cols: 2 };
  assert.deepEqual(parseDataViewMessage({ type: 'copy', row: 2, col: 1, gen: 4 }, dims, 4), { type: 'copy', row: 2, col: 1 });
  assert.deepEqual(parseDataViewMessage({ type: 'sort', gen: 4, col: 0, dir: 'asc' }, dims, 4), { type: 'sort', col: 0, dir: 'asc' });
  assert.deepEqual(parseDataViewMessage({ type: 'sort', gen: 4, col: 1, dir: 'none' }, dims, 4), { type: 'sort', col: 1, dir: 'none' });
  assert.deepEqual(parseDataViewMessage({ type: 'reload', gen: 4, top: 500 }, dims, 4), { type: 'reload', top: 500 });
  const bad = [
    null, 'copy', [], { type: 'copy', row: 3, col: 0, gen: 4 }, { type: 'copy', row: -1, col: 0, gen: 4 }, { type: 'copy', row: 0.5, col: 0, gen: 4 },
    { type: 'copy', row: '0', col: 0, gen: 4 }, { type: 'copy', row: 0, col: 2, gen: 4 },
    { type: 'copy', row: 0, col: 0 }, { type: 'copy', row: 0, col: 0, gen: 3 }, { type: 'copy', row: 0, col: 0, gen: '4' }, { type: 'copy', row: 0, col: 0, gen: 4.5 },
    { type: 'sort', gen: 4, col: 0, dir: 'up' }, { type: 'sort', col: 0, dir: 'asc' }, { type: 'sort', gen: 3, col: 0, dir: 'asc' }, { type: 'sort', col: 9, dir: 'asc' },
    { type: 'reload', top: 200 }, { type: 'reload', gen: 3, top: 200 }, { type: 'reload', gen: 4, top: 0 }, { type: 'reload', top: 10001 }, { type: 'reload', top: '200' }, { type: 'reload', top: 1.5 },
    { type: 'eval', code: 'x' }, { type: 'cancel' }, { type: 'reveal', line: 1 },
  ];
  for (const m of bad) assert.equal(parseDataViewMessage(m, dims, 4), undefined, JSON.stringify(m));
  assert.equal(parseDataViewMessage({ type: 'copy', row: 0, col: 0, gen: 1 }, { rows: 0, cols: 0 }, 1), undefined);
});

test('Results message validation: copy needs a set, row and column in range and the current gen; reveal and cancel pass', () => {
  const sets = [{ rows: 2, cols: 1 }, { rows: 0, cols: 3 }];
  assert.deepEqual(parseResultsMessage({ type: 'copy', set: 0, row: 1, col: 0, gen: 9 }, sets, 9), { type: 'copy', row: 1, col: 0, set: 0 });
  assert.deepEqual(parseResultsMessage({ type: 'reveal', line: 7 }, sets, 9), { type: 'reveal', line: 7 });
  assert.deepEqual(parseResultsMessage({ type: 'cancel' }, [], 9), { type: 'cancel' });
  const bad = [
    { type: 'copy', set: 1, row: 0, col: 0, gen: 9 }, { type: 'copy', set: 2, row: 0, col: 0, gen: 9 }, { type: 'copy', row: 0, col: 0, gen: 9 },
    { type: 'copy', set: 0, row: 2, col: 0, gen: 9 }, { type: 'copy', set: 0, row: 0, col: 1, gen: 9 },
    { type: 'copy', set: 0, row: 0, col: 0 }, { type: 'copy', set: 0, row: 0, col: 0, gen: 8 }, { type: 'copy', set: 0, row: 0, col: 0, gen: '9' },
    { type: 'reveal', line: '3' }, { type: 'reveal', line: 1.5 }, { type: 'sort', col: 0, dir: 'asc' }, { type: 'reload', top: 5 }, undefined,
  ];
  for (const m of bad) assert.equal(parseResultsMessage(m, sets, 9), undefined, JSON.stringify(m));
});
