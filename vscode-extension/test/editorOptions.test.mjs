import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { OPTION_SETTINGS, parseOptionsMessage, settingsUpdates, valuesFromSettings } from '../out/options/editorOptionsModel.js';
import { renderEditorOptions } from '../out/options/editorOptionsHtml.js';
import { enhancedOptions } from '../out/query/sqlEditorFeatures.js';

const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
const c = pkg.contributes;
const defaults = Object.fromEntries(Object.entries(OPTION_SETTINGS).map(([, key]) => [key, c.configuration.properties[`msSqlMcp.${key}`]?.default]));
const valid = (over = {}) => ({
  enhanced: true, tableAliases: false, joinNameMatch: 'keyed', keywordCase: 'upper', maxItemsPerRow: 6, formatFiles: true,
  shortcuts: ['', '', '', 'select top (1000) * from {selection}', '', "select * from globalVars where varName = 'dbVersion'", '', '', '', ''],
  ...over,
});

test('every option is a declared setting', () => {
  for (const key of Object.values(OPTION_SETTINGS)) assert.ok(c.configuration.properties[`msSqlMcp.${key}`], key);
  assert.equal(c.configuration.properties['msSqlMcp.completion.tableAliases'].default, true);
  assert.deepEqual(c.configuration.properties['msSqlMcp.completion.joinNameMatch'].enum, ['off', 'keyed', 'any']);
});

test('the form starts from the effective settings, with defaults for anything odd', () => {
  const v = valuesFromSettings(key => defaults[key]);
  assert.deepEqual(v, {
    enhanced: true, tableAliases: true, joinNameMatch: 'any', keywordCase: 'lower', maxItemsPerRow: 4, formatFiles: false,
    shortcuts: ['', '', '', 'select top (1000) * from {selection}', '', '', '', '', '', ''],
  });
  const odd = valuesFromSettings(() => 'nonsense');
  assert.equal(odd.joinNameMatch, 'any');
  assert.equal(odd.keywordCase, 'lower');
  assert.equal(odd.maxItemsPerRow, 4);
});

test('enhancedOptions defaults to aliases on and any name match', () => {
  assert.deepEqual(enhancedOptions(undefined, undefined), { tableAliases: true, joinNameMatch: 'any' });
  assert.deepEqual(enhancedOptions(false, 'off'), { tableAliases: false, joinNameMatch: 'off' });
});

test('save messages are validated', () => {
  assert.deepEqual(parseOptionsMessage({ type: 'save', scope: 'user', values: valid() }), { type: 'save', scope: 'user', values: valid() });
  assert.deepEqual(parseOptionsMessage({ type: 'cancel' }), { type: 'cancel' });
  for (const bad of [
    { type: 'save', scope: 'machine', values: valid() },
    { type: 'save', scope: 'user', values: valid({ maxItemsPerRow: 0 }) },
    { type: 'save', scope: 'user', values: valid({ maxItemsPerRow: 2.5 }) },
    { type: 'save', scope: 'user', values: valid({ joinNameMatch: 'all' }) },
    { type: 'save', scope: 'user', values: valid({ shortcuts: ['x'] }) },
    { type: 'save', scope: 'user', values: valid({ shortcuts: Array(10).fill('x'.repeat(4001)) }) },
    { type: 'save', scope: 'user', values: valid({ enhanced: 'yes' }) },
    { type: 'other' }, null, 'save',
  ]) assert.equal(parseOptionsMessage(bad), undefined, JSON.stringify(bad)?.slice(0, 80));
});

test('saving maps every field to its setting; a cleared default shortcut is written blank', () => {
  const updates = Object.fromEntries(settingsUpdates(valid({ shortcuts: Array(10).fill('') }), defaults.queryShortcuts));
  assert.deepEqual(updates, {
    'completion.enhanced': true, 'completion.tableAliases': false, 'completion.joinNameMatch': 'keyed',
    'format.keywordCase': 'upper', 'format.maxItemsPerRow': 6, 'format.formatFiles': true, queryShortcuts: { 3: '' },
  });
});

test('the form escapes shortcut SQL and offers workspace scope only with a folder open', () => {
  const html = renderEditorOptions(valid({ shortcuts: ['<script>"x"</script>', ...Array(9).fill('')] }), { nonce: 'abc', hasWorkspace: false });
  assert.ok(!html.includes('<script>"x"'));
  assert.ok(html.includes('&lt;script&gt;&quot;x&quot;'));
  assert.ok(html.includes("script-src 'nonce-abc'"));
  assert.ok(!html.includes('value="workspace"'));
  assert.ok(renderEditorOptions(valid(), { nonce: 'abc', hasWorkspace: true }).includes('value="workspace"'));
  for (let slot = 0; slot < 10; slot++) assert.ok(html.includes(`id="sc${slot}"`));
});

test('the options form is reachable from the explorer and the SQL editor tab menus', () => {
  assert.ok(c.commands.some(x => x.command === 'msSqlMcp.editorOptions'));
  for (const menu of ['view/title', 'editor/title', 'editor/title/context']) {
    assert.ok(c.menus[menu].some(m => m.command === 'msSqlMcp.editorOptions'), menu);
  }
});
