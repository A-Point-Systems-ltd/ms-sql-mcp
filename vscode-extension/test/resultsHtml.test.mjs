import { test } from 'node:test';
import assert from 'node:assert/strict';
import { renderResults } from '../out/query/resultsHtml.js';

const NONCE = 'abc123nonce';
const CSP = 'vscode-webview://x';
const render = state => renderResults(state, NONCE, CSP);

const result = (over = {}) => ({ resultSets: [], messages: [], hadErrors: false, batches: 1, elapsedMs: 12, ...over });
const set = (over = {}) => ({ batch: 1, columns: [{ name: 'Id', type: 'int' }], rows: [[1]], rowCount: 1, truncated: false, ...over });
const done = (r, lineOffset = 0) => ({ kind: 'done', connection: 'dev', result: r, lineOffset });

/** The id of the tab button marked as selected. */
const selectedTab = html => /<button[^>]*class="tab selected"[^>]*data-tab="(\w+)"/.exec(html)?.[1];

test('every state renders a full document with the CSP and its nonce', () => {
  const states = [
    { kind: 'empty' },
    { kind: 'running', connection: 'dev', startedAt: 1_700_000_000_000 },
    done(result({ resultSets: [set()] })),
    { kind: 'failed', connection: 'dev', error: 'Server process exited.' },
    { kind: 'cancelled', connection: 'dev' },
  ];
  for (const s of states) {
    const html = render(s);
    assert.match(html, /^<!DOCTYPE html>/, s.kind);
    assert.ok(html.includes(`content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${NONCE}';"`), s.kind);
    assert.ok(html.includes(`<script nonce="${NONCE}">`), s.kind);
    assert.ok(!/https?:\/\//.test(html), `${s.kind}: no network resources`);
    assert.ok(!html.includes('innerHTML'), `${s.kind}: the client script never uses innerHTML`);
  }
});

test('state texts: empty, running (timer + Cancel), completed, errors, failed, cancelled', () => {
  assert.match(render({ kind: 'empty' }), /Run a query with F5 or the Run button\./);

  const running = render({ kind: 'running', connection: 'dev', startedAt: 1_700_000_000_000 });
  assert.match(running, /Running/);
  assert.match(running, /data-started="1700000000000"/);
  assert.match(running, /<button[^>]*id="cancel"[^>]*>Cancel<\/button>/);
  assert.match(running, /dev/);

  assert.match(render(done(result({ elapsedMs: 41 }))), /Completed in 41 ms/);
  const withErrors = render(done(result({ hadErrors: true, messages: [{ kind: 'error', text: 'x', line: null }] })));
  assert.match(withErrors, /Completed with errors/);
  assert.doesNotMatch(withErrors, /Completed in/);

  assert.match(render({ kind: 'failed', connection: 'dev', error: 'No open connections.' }), /No open connections\./);
  assert.match(render({ kind: 'cancelled', connection: 'dev' }), /Cancelled/);
});

test('tabs show counts; Results is selected when there are result sets', () => {
  const html = render(done(result({
    resultSets: [set(), set({ batch: 2 })],
    messages: [{ kind: 'rows', text: '(1 row affected)', line: null }],
  })));
  assert.match(html, />Results \(2\)</);
  assert.match(html, />Messages \(1\)</);
  assert.equal(selectedTab(html), 'results');
});

test('Messages is auto-selected on errors only and when there are no result sets', () => {
  const errorsOnly = render(done(result({ hadErrors: true, messages: [{ kind: 'error', text: 'Msg 102', line: 1 }] })));
  assert.equal(selectedTab(errorsOnly), 'messages');
  const noSets = render(done(result({ messages: [{ kind: 'rows', text: '(3 rows affected)', line: null }] })));
  assert.equal(selectedTab(noSets), 'messages');
  // Errors next to a result set keep Results selected.
  const mixed = render(done(result({ hadErrors: true, resultSets: [set()], messages: [{ kind: 'error', text: 'x', line: 1 }] })));
  assert.equal(selectedTab(mixed), 'results');
});

test('values, column names, types, messages and the connection are HTML-escaped', () => {
  const evil = '<script>alert(1)</script>&amp';
  const html = render({
    kind: 'done', connection: evil, lineOffset: 0,
    result: result({
      resultSets: [set({ columns: [{ name: evil, type: '"><b>' }], rows: [[evil]] })],
      messages: [{ kind: 'error', text: evil, line: 2 }],
    }),
  });
  assert.ok(!html.includes('<script>alert(1)</script>'));
  assert.ok(!html.includes('"><b>'));
  assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;&amp;amp'));
  assert.ok(html.includes('&quot;&gt;&lt;b&gt;'));
  // Failed state error text too.
  assert.ok(!render({ kind: 'failed', connection: 'dev', error: evil }).includes('<script>alert'));
});

test('grid: NULL cells, numeric columns right-aligned, name and type as header tooltip', () => {
  const html = render(done(result({
    resultSets: [set({
      columns: [{ name: 'Id', type: 'int' }, { name: 'Name', type: 'nvarchar' }, { name: 'Amount', type: 'decimal' }, { name: '', type: 'int' }],
      rows: [[1, null, '12.50', 7]],
    })],
  })));
  assert.match(html, /<th data-c="0" data-sort="" data-num="1" draggable="true" title="Id \(int\)"><span class="hl">Id<\/span>/);
  assert.match(html, /<th data-c="1" data-sort="" draggable="true" title="Name \(nvarchar\)"><span class="hl">Name<\/span>/);
  assert.match(html, /title="\(No column name\) \(int\)"><span class="hl">\(No column name\)<\/span>/);
  assert.match(html, /<td class="null" data-null="1">NULL<\/td>/);
  assert.match(html, /<td>1<\/td>/);
  // A decimal arrives as a string but is still a numeric column: right-aligned by a per-column rule.
  assert.match(html, /<td>12\.50<\/td>/);
  assert.match(html, /#g0 \.gt tbody tr>:nth-child\(2\)\{text-align:right/);
  assert.match(html, /#g0 \.gt tbody tr>:nth-child\(4\)\{text-align:right/);
  assert.doesNotMatch(html, /#g0 \.gt tbody tr>:nth-child\(3\)\{text-align:right/);
  // Local sort per set, with the caption note and the set index for copy messages.
  assert.match(html, /class="dgrid" id="g0" data-mode="local" data-set="0"/);
  assert.match(html, /data-sortnote="g0" style="display:none"> · sorted locally \(loaded rows only\)/);
});

test('captions: row counts, truncation, single set fills the view, several sets are capped', () => {
  const single = render(done(result({ resultSets: [set({ rowCount: 1 })] })));
  assert.match(single, /Result 1 - 1 row</);
  assert.match(single, /class="grid single"/);

  const many = render(done(result({
    resultSets: [set({ rows: [[1], [2]], rowCount: 5000, truncated: true }), set({ batch: 2, rows: [], rowCount: 0 })],
  })));
  assert.match(many, /Result 1 - 5000 rows \(showing first 2\)/);
  assert.match(many, /Result 2 - 0 rows</);
  assert.equal((many.match(/class="grid multi"/g) ?? []).length, 2);
  assert.match(many, /\.grid\.multi\s*\{[^}]*max-height:\s*45vh/);
});

test('messages: kind classes in order, and a line becomes a reveal link', () => {
  const html = render(done(result({
    messages: [
      { kind: 'info', text: 'first', line: 3 },
      { kind: 'rows', text: 'second', line: null },
      { kind: 'warning', text: 'third', line: null },
      { kind: 'error', text: 'fourth', line: 7 },
    ],
  })));
  const body = html.slice(html.indexOf('<body>'));
  const order = ['first', 'second', 'third', 'fourth'].map(t => body.indexOf(t));
  assert.deepEqual([...order].sort((a, b) => a - b), order);
  assert.match(html, /class="msg info"/);
  assert.match(html, /class="msg rows"/);
  assert.match(html, /class="msg warning"/);
  assert.match(html, /class="msg error"/);
  assert.match(html, /data-line="7"/);
  assert.match(html, /data-line="3"/);
  assert.equal((html.match(/data-line="/g) ?? []).length, 2);
  // The client script posts reveal and cancel messages.
  assert.match(html, /type: 'reveal'/);
  assert.match(html, /type: 'cancel'/);
});

test('a run without result sets says so in the Results pane', () => {
  const html = render(done(result({ messages: [{ kind: 'rows', text: '(1 row affected)', line: null }] })));
  assert.match(html, /<section class="pane hidden" id="pane-results"[^>]*><p class="none">No result sets\.<\/p><\/section>/);
  assert.doesNotMatch(render(done(result({ resultSets: [set()] }))), /No result sets\./);
});

test('exact-number strings (decimal, money, bigint) are right-aligned by column type; 0x binary is not', () => {
  const html = render(done(result({ resultSets: [set({
    columns: [{ name: 'd', type: 'decimal' }, { name: 'm', type: 'money' }, { name: 'b', type: 'bigint' }, { name: 'v', type: 'varbinary' }, { name: 's', type: 'nvarchar' }],
    rows: [['12345678901234.5678', '922337203685477.5807', '9223372036854775807', '0x00FF10', '42']],
  })] })));
  const right = n => html.includes(`#g0 .gt tbody tr>:nth-child(${n}){text-align:right`);
  assert.deepEqual([2, 3, 4, 5, 6].map(right), [true, true, true, false, false]);
  assert.ok(html.includes('<td>12345678901234.5678</td>'));
  assert.ok(html.includes('<td>0x00FF10</td>'));
  // A numeric-looking string in a text column stays left-aligned.
  assert.ok(html.includes('<td>42</td>'));
});

test('a value cut by the server cap is marked and explains the full size in its tooltip', () => {
  const cutText = `abc\u2026 (truncated, 70000 chars)`;
  const cutBinary = `0x4142\u2026 (truncated, 40000 bytes)`;
  const html = render(done(result({ resultSets: [set({
    columns: [{ name: 's', type: 'nvarchar' }, { name: 'b', type: 'varbinary' }],
    rows: [[cutText, cutBinary]],
  })] })));
  assert.ok(html.includes(`<td class="trunc" title="${cutText}\n\nTruncated by the server: the full value has 70000 chars.">${cutText}</td>`));
  assert.ok(html.includes(`<td class="trunc" title="${cutBinary}\n\nTruncated by the server: the full value has 40000 bytes.">${cutBinary}</td>`));
  // Only the exact server suffix at the end counts.
  const plain = render(done(result({ resultSets: [set({ columns: [{ name: 's', type: 'nvarchar' }], rows: [['(truncated, 5 chars) is just text']] })] })));
  assert.ok(!plain.includes('class="trunc"'));
});
