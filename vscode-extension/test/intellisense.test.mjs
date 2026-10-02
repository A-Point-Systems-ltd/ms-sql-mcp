import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  COMPLETION_TRIGGER_CHARACTERS, LANGUAGE_SERVICE_TIMEOUT_MS, SIGNATURE_RETRIGGER_CHARACTERS, SIGNATURE_TRIGGER_CHARACTERS,
  TYPING_DEBOUNCE_MS, TYPING_WINDOW_MS, WARM_INTERVAL_MS, WarmTracker, buildPositionRequest, completionKindName,
  intellisenseDocs, intellisenseEnabled, isExplicitCompletion, loadingMessage, parseCompletionResult, parseHoverResult,
  parseSignatureHelpResult, refreshedMessage, replaceStart, requestDelayMs, warmKey,
} from '../out/query/intellisense.js';
import { traceableArguments, traceablePayload } from '../out/client/parse.js';

const profile = (over = {}) => ({ name: 'dev', server: 'DC\\DEV', database: 'db1', auth: 'windows', readOnly: false, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true, ...over });

test('constants: trigger characters, timeout, debounce and warm interval', () => {
  assert.deepEqual([...COMPLETION_TRIGGER_CHARACTERS], ['.', ' ', '(', ',', '@', '[']);
  assert.deepEqual([...SIGNATURE_TRIGGER_CHARACTERS], ['(', ',']);
  assert.deepEqual([...SIGNATURE_RETRIGGER_CHARACTERS], [',']);
  assert.equal(LANGUAGE_SERVICE_TIMEOUT_MS, 5000);
  assert.equal(TYPING_DEBOUNCE_MS, 150);
  assert.equal(WARM_INTERVAL_MS, 5 * 60 * 1000);
});

test('completionKindName maps every server kind to a CompletionItemKind name', () => {
  const expected = {
    table: 'Class', view: 'Interface', column: 'Field', procedure: 'Method', function: 'Function', keyword: 'Keyword',
    schema: 'Module', parameter: 'Property', variable: 'Variable', database: 'Folder', type: 'TypeParameter',
    snippet: 'Snippet', other: 'Text',
  };
  for (const [kind, name] of Object.entries(expected)) assert.equal(completionKindName(kind), name, kind);
  assert.equal(completionKindName('TABLE'), 'Class');
  assert.equal(completionKindName('something-new'), 'Text');
  assert.equal(completionKindName(undefined), 'Text');
});

test('buildPositionRequest sends the whole text with 1-based line and column', () => {
  const text = 'SELECT 1\nSELECT t. FROM dbo.T t';
  assert.deepEqual(buildPositionRequest('completion', text, { line: 1, character: 9 }),
    { action: 'completion', text, line: 2, column: 10 });
  assert.deepEqual(buildPositionRequest('hover', text, { line: 0, character: 0 }), { action: 'hover', text, line: 1, column: 1 });
  assert.deepEqual(buildPositionRequest('signatureHelp', '', { line: 0, character: 0 }), { action: 'signatureHelp', text: '', line: 1, column: 1 });
});

test('buildPositionRequest takes no selection: the text is always the full document', () => {
  // The provider passes document.getText(); there is no selection parameter to narrow it.
  assert.equal(buildPositionRequest.length, 3);
});

test('explicit Ctrl+Space is not debounced; typing-triggered requests wait 150 ms', () => {
  assert.equal(isExplicitCompletion('invoke', undefined), true);
  assert.equal(isExplicitCompletion('invoke', TYPING_WINDOW_MS + 1), true);
  // Quick suggestions while typing also arrive as "invoke", right after an edit.
  assert.equal(isExplicitCompletion('invoke', 10), false);
  assert.equal(isExplicitCompletion('triggerCharacter', undefined), false);
  assert.equal(isExplicitCompletion('incomplete', 5000), false);
  assert.equal(requestDelayMs(true), 0);
  assert.equal(requestDelayMs(false), 150);
});

test('WarmTracker: once per connection target per 5 minutes, case-insensitive', () => {
  const warm = new WarmTracker();
  const key = warmKey(profile());
  assert.equal(warm.shouldWarm(key, 1000), true);
  assert.equal(warm.shouldWarm(key, 1000 + 60_000), false);
  assert.equal(warm.shouldWarm(warmKey(profile({ name: 'DEV' })), 2000), false);
  assert.equal(warm.shouldWarm(key, 1000 + WARM_INTERVAL_MS - 1), false);
  assert.equal(warm.shouldWarm(key, 1000 + WARM_INTERVAL_MS), true);
  // Another database (profile edited) or connection is another cache.
  assert.equal(warm.shouldWarm(warmKey(profile({ database: 'db2' })), 2000), true);
  assert.equal(warm.shouldWarm(warmKey(profile({ name: 'other' })), 2000), true);
  warm.mark(warmKey(profile({ name: 'marked' })), 3000);
  assert.equal(warm.shouldWarm(warmKey(profile({ name: 'marked' })), 4000), false);
  // A runner restart empties the server caches.
  warm.clear();
  assert.equal(warm.shouldWarm(key, 1000 + WARM_INTERVAL_MS + 1), true);
});

test('parseCompletionResult keeps valid items, drops malformed ones, passes isIncomplete and cacheState', () => {
  const payload = {
    success: true,
    data: {
      items: [
        { label: 'a', kind: 'column', detail: 'int', insertText: 'a', sortText: '1a' },
        { label: '@x', kind: 'parameter', detail: 'int', insertText: '@x = ', sortText: '0@x' },
        { label: 'SELECT', kind: 'keyword' },
        { label: '', kind: 'column', insertText: 'x' },
        { label: 42, kind: 'column' },
        { kind: 'table', insertText: 'T' },
        null,
        'T',
        { label: 'b', kind: 7, detail: 5, insertText: null, sortText: ['x'] },
      ],
      isIncomplete: true,
      cacheState: 'loading',
    },
  };
  assert.deepEqual(parseCompletionResult(payload), {
    items: [
      { label: 'a', kind: 'column', detail: 'int', insertText: 'a', sortText: '1a' },
      { label: '@x', kind: 'parameter', detail: 'int', insertText: '@x = ', sortText: '0@x' },
      { label: 'SELECT', kind: 'keyword', detail: undefined, insertText: 'SELECT', sortText: undefined },
      { label: 'b', kind: 'other', detail: undefined, insertText: 'b', sortText: undefined },
    ],
    isIncomplete: true,
    cacheState: 'loading',
  });
});

test('parseCompletionResult: defaults and malformed envelopes', () => {
  assert.deepEqual(parseCompletionResult({ success: true, data: { items: [] } }), { items: [], isIncomplete: false, cacheState: 'warm' });
  assert.deepEqual(parseCompletionResult({ Data: { Items: [{ Label: 'T', Kind: 'table', InsertText: 'T', SortText: '2T' }], IsIncomplete: false, CacheState: 'warm' } }),
    { items: [{ label: 'T', kind: 'table', detail: undefined, insertText: 'T', sortText: '2T' }], isIncomplete: false, cacheState: 'warm' });
  assert.equal(parseCompletionResult(undefined), undefined);
  assert.equal(parseCompletionResult({ success: true, data: null }), undefined);
  assert.equal(parseCompletionResult({ success: true, data: { items: 'nope' } }), undefined);
  assert.equal(parseCompletionResult({ success: true, data: { items: [], cacheState: 'weird' } }).cacheState, 'warm');
});

test('parseHoverResult keeps contents and a valid 1-based range only', () => {
  assert.deepEqual(parseHoverResult({ data: { contents: 'column a(int, null)', range: { startLine: 1, startColumn: 8, endLine: 1, endColumn: 9 } } }),
    { contents: 'column a(int, null)', range: { startLine: 1, startColumn: 8, endLine: 1, endColumn: 9 } });
  assert.deepEqual(parseHoverResult({ data: { contents: 'x', range: { startLine: 0, startColumn: 1, endLine: 1, endColumn: 2 } } }), { contents: 'x', range: undefined });
  assert.deepEqual(parseHoverResult({ data: { contents: 'x', range: null } }), { contents: 'x', range: undefined });
  assert.equal(parseHoverResult({ data: null }), undefined);
  assert.equal(parseHoverResult({ data: { contents: '' } }), undefined);
  assert.equal(parseHoverResult({ data: { contents: 3 } }), undefined);
});

test('parseSignatureHelpResult validates signatures and parameters', () => {
  const payload = {
    data: {
      signatures: [
        { label: 'DATEADD(interval, increment, expression)', documentation: 'adds', parameters: [{ label: 'interval' }, { label: 'increment', documentation: 'n' }, { nope: 1 }] },
        { label: 7, parameters: [] },
        { label: 'p @x int', parameters: 'bad' },
      ],
      activeSignature: 0,
      activeParameter: 1,
    },
  };
  assert.deepEqual(parseSignatureHelpResult(payload), {
    signatures: [
      { label: 'DATEADD(interval, increment, expression)', documentation: 'adds', parameters: [{ label: 'interval', documentation: undefined }, { label: 'increment', documentation: 'n' }] },
      { label: 'p @x int', documentation: undefined, parameters: [] },
    ],
    activeSignature: 0,
    activeParameter: 1,
  });
  assert.equal(parseSignatureHelpResult({ data: null }), undefined);
  assert.equal(parseSignatureHelpResult({ data: { signatures: [] } }), undefined);
  const odd = parseSignatureHelpResult({ data: { signatures: [{ label: 'f()', parameters: [] }], activeSignature: 9, activeParameter: 'x' } });
  assert.equal(odd.activeSignature, 0);
  assert.equal(odd.activeParameter, -1);
});

test('replaceStart covers the word being typed, with a leading @, @@, # or [', () => {
  assert.equal(replaceStart('SELECT * FROM Ord', 17), 14);
  assert.equal(replaceStart('EXEC dbo.p @', 12), 11);
  assert.equal(replaceStart('SELECT @@ver', 12), 7);
  assert.equal(replaceStart('SELECT * FROM [My Ta', 20), 14);
  assert.equal(replaceStart('SELECT * FROM [', 15), 14);
  assert.equal(replaceStart('SELECT t.', 9), 9);
  assert.equal(replaceStart('SELECT ', 7), 7);
  assert.equal(replaceStart('SELECT * FROM #tmp', 18), 14);
  assert.equal(replaceStart('', 0), 0);
  assert.equal(replaceStart('SELECT [a].', 11), 11);
  assert.equal(replaceStart('SELECT [a], b', 13), 12);
});

test('intellisenseDocs lists bound documents whose connection exists and is open', () => {
  const entries = [
    ['mssql-sql:/query/1', { connection: 'dev', kind: 'query' }],
    ['file:///o.sql', { connection: 'ro', kind: 'object' }],
    ['untitled:closed', { connection: 'closed', kind: 'query' }],
    ['untitled:gone', { connection: 'gone', kind: 'query' }],
  ];
  const profiles = [profile(), profile({ name: 'ro', readOnly: true }), profile({ name: 'closed', open: false })];
  assert.deepEqual(intellisenseDocs(entries, profiles), ['mssql-sql:/query/1', 'file:///o.sql']);
});

test('intellisenseEnabled defaults to true and is false only for false', () => {
  assert.equal(intellisenseEnabled(undefined), true);
  assert.equal(intellisenseEnabled(true), true);
  assert.equal(intellisenseEnabled(false), false);
  assert.equal(intellisenseEnabled('false'), true);
});

test('messages name the database (loading) and server/database (refresh)', () => {
  assert.equal(loadingMessage(profile()), 'APoint-ms-sql: loading IntelliSense for db1…');
  assert.equal(refreshedMessage(profile()), 'IntelliSense cache refreshed for DC\\DEV/db1');
  const raw = profile({ name: 'rawconn', auth: 'raw', server: '', database: '' });
  assert.equal(loadingMessage(raw), 'APoint-ms-sql: loading IntelliSense for rawconn…');
  assert.equal(refreshedMessage(raw), 'IntelliSense cache refreshed for rawconn');
});

test('trace logging never records the document text of language_service calls', () => {
  const args = { action: 'completion', text: 'SELECT secret FROM dbo.T', line: 1, column: 8, connection: 'dev' };
  assert.deepEqual(traceableArguments('language_service', args), { action: 'completion', textLength: 24, line: 1, column: 8, connection: 'dev' });
  assert.deepEqual(traceableArguments('list_tables', { connection: 'dev' }), { connection: 'dev' });
  const completion = { success: true, data: { items: [{ label: 'secret_col' }, { label: 'b' }], isIncomplete: false, cacheState: 'warm' } };
  assert.deepEqual(traceablePayload('language_service', completion), { items: 2, isIncomplete: false, cacheState: 'warm' });
  assert.deepEqual(traceablePayload('language_service', { success: true, data: { contents: 'column secret_col(int)' } }), { data: 'present' });
  assert.deepEqual(traceablePayload('language_service', { success: true, data: null }), { data: null });
  assert.deepEqual(traceablePayload('language_service', { success: true, data: { cacheState: 'loading' } }), { cacheState: 'loading' });
});

test('package.json: IntelliSense command, menus, keybinding and setting', () => {
  const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  const cmd = pkg.contributes.commands.find(c => c.command === 'msSqlMcp.refreshIntelliSense');
  assert.ok(cmd);
  assert.equal(cmd.title, 'Refresh IntelliSense Cache');
  assert.equal(cmd.icon, '$(refresh)');
  assert.equal(cmd.category, 'APoint-ms-sql');

  const title = pkg.contributes.menus['editor/title'].filter(m => m.command === 'msSqlMcp.refreshIntelliSense');
  assert.equal(title.length, 1);
  assert.match(title[0].when, /msSqlMcp\.editorConnected/);
  assert.match(title[0].when, /resource in msSqlMcp\.intellisenseDocs/);
  assert.match(title[0].group, /^navigation@\d+$/);
  // Next to Run, Cancel and History.
  const order = m => Number(m.group.split('@')[1]);
  const history = pkg.contributes.menus['editor/title'].find(m => m.command === 'msSqlMcp.showHistory');
  assert.ok(order(title[0]) > order(history));

  const context = pkg.contributes.menus['editor/context'].filter(m => m.command === 'msSqlMcp.refreshIntelliSense');
  assert.equal(context.length, 1);
  assert.equal(context[0].when, 'msSqlMcp.editorConnected');
  const palette = pkg.contributes.menus.commandPalette.find(m => m.command === 'msSqlMcp.refreshIntelliSense');
  assert.equal(palette.when, 'msSqlMcp.editorConnected');

  const keys = pkg.contributes.keybindings.filter(k => k.command === 'msSqlMcp.refreshIntelliSense');
  assert.deepEqual(keys, [{ command: 'msSqlMcp.refreshIntelliSense', key: 'ctrl+shift+r', when: 'editorTextFocus && msSqlMcp.editorConnected' }]);

  const setting = pkg.contributes.configuration.properties['msSqlMcp.intellisense.enabled'];
  assert.equal(setting.type, 'boolean');
  assert.equal(setting.default, true);
});
