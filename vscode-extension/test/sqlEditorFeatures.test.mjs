import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  ENHANCED_CONTEXT_KEY, ENHANCED_SETTING, FORMAT_TOOL, SQL_SNIPPETS, enhancedEnabled, expandWildcard, formatRequest,
  formatSettings, objectNameAt, objectRefOf, parseFormatEdits, parseObjectInfo, parseScope, pickerItems, quoteName,
  snippetHint, snippetPreview, tvfQuery, wildcardAt,
} from '../out/query/sqlEditorFeatures.js';
import { completionKindName } from '../out/query/intellisense.js';
import { traceableArguments } from '../out/client/parse.js';

const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));

test('the enhancements are on unless the setting is explicitly false', () => {
  assert.equal(enhancedEnabled(undefined), true);
  assert.equal(enhancedEnabled(true), true);
  assert.equal(enhancedEnabled(false), false);
  assert.equal(enhancedEnabled('false'), true);
});

test('ssf expands to select top(100) * from, with a Tab hint', () => {
  const ssf = SQL_SNIPPETS.find(s => s.prefix === 'ssf');
  assert.ok(ssf);
  assert.equal(snippetPreview(ssf.body), 'select top(100) * from ');
  assert.equal(snippetHint(ssf), '⇥ Tab → select top(100) * from');
});

test('snippet prefixes are unique and new modules carry --with encryption', () => {
  const prefixes = SQL_SNIPPETS.map(s => s.prefix);
  assert.equal(new Set(prefixes).size, prefixes.length);
  for (const p of ['cp', 'cf', 'ctf']) assert.match(SQL_SNIPPETS.find(s => s.prefix === p).body, /\n--with encryption\n/);
});

test('snippet previews drop placeholders but keep their default text', () => {
  assert.equal(snippetPreview('update ${1:table}\nset ${2:column} = $3\nwhere $0'), 'update table\nset column = \nwhere ');
});

test('format settings follow the editor and clamp the options', () => {
  assert.deepEqual(formatSettings({}, { tabSize: 2, insertSpaces: true }), { indentSize: 2, useTabs: false, keywordCase: 'lower', maxItemsPerRow: 4 });
  assert.deepEqual(formatSettings({ keywordCase: 'upper', maxItemsPerRow: 99 }, { tabSize: 'auto', insertSpaces: false }),
    { indentSize: 4, useTabs: true, keywordCase: 'upper', maxItemsPerRow: 50 });
  assert.equal(formatSettings({ keywordCase: 'weird' }, { tabSize: 4, insertSpaces: true }).keywordCase, 'lower');
});

test('the format request is 1-based and sends a range only for a selection', () => {
  const settings = { indentSize: 4, useTabs: false, keywordCase: 'lower', maxItemsPerRow: 4 };
  assert.deepEqual(formatRequest('x', settings), { text: 'x', ...settings });
  assert.deepEqual(formatRequest('x', settings, { start: { line: 0, character: 2 }, end: { line: 3, character: 0 } }),
    { text: 'x', ...settings, startLine: 1, startColumn: 3, endLine: 4, endColumn: 1 });
});

test('format edits are validated as a whole', () => {
  const ok = { success: true, data: { edits: [{ startLine: 1, startColumn: 1, endLine: 1, endColumn: 7, newText: 'select' }] } };
  assert.deepEqual(parseFormatEdits(ok), ok.data.edits);
  assert.deepEqual(parseFormatEdits({ data: { edits: [] } }), []);
  assert.equal(parseFormatEdits({ data: { edits: [{ startLine: 0, startColumn: 1, endLine: 1, endColumn: 1, newText: '' }] } }), undefined);
  assert.equal(parseFormatEdits({ data: {} }), undefined);
});

test('format_sql document text is never traced', () => {
  const traced = traceableArguments(FORMAT_TOOL, { text: 'select secret from t', indentSize: 4 });
  assert.equal(traced.text, undefined);
  assert.equal(traced.textLength, 20);
});

test('the object under the cursor: qualified, bracketed, selected, or none', () => {
  const line = 'select * from dbo.Units u join [sales].[Order Lines] ol on 1 = 1';
  assert.equal(objectNameAt(line, 20), 'dbo.Units');
  assert.equal(objectNameAt(line, 14), 'dbo.Units');
  assert.equal(objectNameAt(line, 40), '[sales].[Order Lines]');
  assert.equal(objectNameAt('exec @x', 6), undefined);
  assert.equal(objectNameAt('', 0), undefined);
  assert.equal(objectNameAt('x', 0, ' dbo.fnUnits(1, 2); '), 'dbo.fnUnits');
  assert.equal(objectNameAt('x', 0, 'two\nlines'), undefined);
  assert.equal(objectNameAt('select * from dbo . T', 20), 'dbo.T');
});

test('object info parses, and maps to an explorer ref only when it has a script type', () => {
  const info = parseObjectInfo({ data: { found: true, schema: 'dbo', name: 'fnUnits', type: 'IF', scriptType: 'TableFunction',
    parameters: [{ name: '@bid', type: 'int', isOutput: false }, { name: '@top', type: 'int', default: '10', isOutput: false }, { bogus: 1 }] } });
  assert.equal(info.parameters.length, 2);
  assert.equal(info.parameters[1].default, '10');
  assert.deepEqual(objectRefOf('dev', info), { connection: 'dev', scriptType: 'TableFunction', schema: 'dbo', name: 'fnUnits' });
  assert.equal(objectRefOf('dev', { ...info, scriptType: undefined }), undefined);
  assert.equal(objectRefOf('dev', { found: false, parameters: [] }), undefined);
  assert.equal(parseObjectInfo({ data: {} }), undefined);
});

test('the TVF query declares parameters without defaults and passes default for the rest', () => {
  const sql = tvfQuery('dbo', 'fnUnits', [{ name: '@bid', type: 'int', isOutput: false }, { name: '@top', type: 'int', default: '10', isOutput: false }], 200);
  assert.equal(sql, 'declare @bid int = null\n\nselect top (200) *\nfrom dbo.fnUnits(@bid, default /* @top = 10 */)\n');
  assert.equal(tvfQuery('my schema', 'f', [], 5), 'select top (5) *\nfrom [my schema].f()\n');
});

test('names are bracketed only when needed', () => {
  assert.equal(quoteName('Units'), 'Units');
  assert.equal(quoteName('Order Lines'), '[Order Lines]');
  assert.equal(quoteName('a]b'), '[a]]b]');
});

const scopePayload = { data: { cacheState: 'warm', tables: [
  { alias: 'u', schema: 'dbo', name: 'Units', kind: 'U', columns: [{ name: 'UID', type: 'int', nullable: false, isKey: true }, { name: 'Name', type: 'nvarchar(50)', nullable: true, isKey: false }] },
  { alias: 'b', schema: 'dbo', name: 'Buildings', kind: 'U', columns: [{ name: 'BID', type: 'int', nullable: false, isKey: true }] },
  { name: '' },
] } };

test('scope parses tables and drops nameless ones', () => {
  const scope = parseScope(scopePayload);
  assert.equal(scope.tables.length, 2);
  assert.equal(scope.loading, false);
  assert.equal(parseScope({ data: { tables: [], cacheState: 'loading' } }).loading, true);
  assert.equal(parseScope({ data: {} }), undefined);
});

test('picker rows are qualified by alias and describe type, key and nullability', () => {
  const items = pickerItems(parseScope(scopePayload).tables);
  assert.deepEqual(items.map(i => i.insert), ['u.UID', 'u.Name', 'b.BID']);
  assert.equal(items[0].description, 'int · key · not null');
  assert.deepEqual(pickerItems([{ name: 'T', columns: [{ name: 'a', type: 'int', nullable: true, isKey: false }] }]).map(i => i.insert), ['a']);
});

test('a * of a SELECT list is found; count(*) and multiplication are not', () => {
  assert.deepEqual(wildcardAt('select * from t', 7), { start: 7, end: 8 });
  assert.deepEqual(wildcardAt('select u.* from t', 9), { start: 7, end: 10, qualifier: 'u' });
  assert.deepEqual(wildcardAt('select a, [x y].* from t', 12), { start: 10, end: 17, qualifier: 'x y' });
  assert.deepEqual(wildcardAt('select top (10) * from t', 16), { start: 16, end: 17 });
  assert.equal(wildcardAt('select count(*) from t', 13), undefined);
  assert.equal(wildcardAt('select a * b from t', 9), undefined);
  assert.deepEqual(wildcardAt('    *', 4), { start: 4, end: 5 });
});

test('* expands to every column, or to one table for alias.*', () => {
  const tables = parseScope(scopePayload).tables;
  assert.equal(expandWildcard(tables), 'u.UID, u.Name, b.BID');
  assert.equal(expandWildcard(tables, 'b'), 'b.BID');
  assert.equal(expandWildcard(tables, 'Buildings'), 'Buildings.BID');
  assert.equal(expandWildcard(tables, 'zz'), undefined);
  assert.equal(expandWildcard([{ name: 'T', columns: [{ name: 'a', type: 'int', nullable: true, isKey: false }] }]), 'a');
  assert.equal(expandWildcard([{ name: '#tmp', columns: [] }]), undefined);
});

test('join and picker completions map to their own kinds', () => {
  assert.equal(completionKindName('join'), 'Reference');
  assert.equal(completionKindName('picker'), 'Event');
});

test('package.json: the switch sits in the editor tab menus and the shortcuts are scoped to SQL editors', () => {
  const c = pkg.contributes;
  assert.equal(c.configuration.properties[`msSqlMcp.${ENHANCED_SETTING}`].default, true);
  for (const menu of ['editor/title', 'editor/title/context']) {
    const ids = c.menus[menu].map(m => m.command);
    assert.ok(ids.includes('msSqlMcp.enableEnhancedCompletions'), menu);
    assert.ok(ids.includes('msSqlMcp.disableEnhancedCompletions'), menu);
  }
  const on = c.menus['editor/title'].find(m => m.command === 'msSqlMcp.disableEnhancedCompletions');
  assert.match(on.when, new RegExp(`(^|&& )${ENHANCED_CONTEXT_KEY}$`));
  const keys = Object.fromEntries(c.keybindings.filter(k => ['ctrl+f2', 'ctrl+f12', 'ctrl+3'].includes(k.key)).map(k => [k.key, k]));
  assert.equal(keys['ctrl+f2'].command, 'msSqlMcp.formatSql');
  assert.equal(keys['ctrl+f12'].command, 'msSqlMcp.goToObjectDefinition');
  assert.equal(keys['ctrl+3'].command, 'msSqlMcp.selectTopRows');
  for (const k of Object.values(keys)) assert.match(k.when, /editorLangId == sql/);
  const contextIds = c.menus['editor/context'].map(m => m.command);
  assert.ok(contextIds.includes('msSqlMcp.formatSql'));
  const declared = new Set(c.commands.map(x => x.command));
  for (const id of ['msSqlMcp.formatSql', 'msSqlMcp.pickColumns', 'msSqlMcp.expandWildcard', 'msSqlMcp.newQueryWithText']) assert.ok(declared.has(id), id);
});
