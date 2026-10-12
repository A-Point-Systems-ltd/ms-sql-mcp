import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  QUERY_SHORTCUTS_SETTING, SHORTCUT_SLOTS, expandShortcut, parseShortcuts, shortcutContextKey, shortcutsToSetting,
} from '../out/query/queryShortcuts.js';

const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
const c = pkg.contributes;

test('only digit slots with non-blank SQL are configured', () => {
  const slots = parseShortcuts({ 3: 'select 1', 5: '   ', 7: 42, x: 'select 2', 10: 'select 3', 0: 'select 0' });
  assert.deepEqual([...slots.entries()], [['0', 'select 0'], ['3', 'select 1']].map(([k, v]) => [Number(k), v]));
  for (const bad of [undefined, null, 'select 1', ['select 1']]) assert.equal(parseShortcuts(bad).size, 0);
});

test('{selection} takes the selected text, else the name under the cursor', () => {
  assert.deepEqual(expandShortcut('select top (1000) * from {selection}', ' dbo.Units ', 'Other'), { sql: 'select top (1000) * from dbo.Units' });
  assert.deepEqual(expandShortcut('select top (1000) * from {selection}', undefined, 'Units'), { sql: 'select top (1000) * from Units' });
  assert.deepEqual(expandShortcut('select top (1000) * from {selection}', '   ', 'Units'), { sql: 'select top (1000) * from Units' });
  assert.deepEqual(expandShortcut('exec sp_help {selection}; select count(*) from {selection}', 'T', undefined), { sql: 'exec sp_help T; select count(*) from T' });
  assert.ok('error' in expandShortcut('select * from {selection}', undefined, undefined));
});

test('a shortcut without {selection} runs as written, whatever is selected', () => {
  const sql = "select * from globalVars where varName = 'dbVersion'";
  assert.deepEqual(expandShortcut(sql, 'dbo.Units', 'Units'), { sql });
  assert.deepEqual(expandShortcut(sql, undefined, undefined), { sql });
});

test('saving writes non-blank slots and blanks a cleared default slot', () => {
  const defaults = { 3: 'select top (1000) * from {selection}' };
  assert.deepEqual(shortcutsToSetting(new Map([[5, 'select 5'], [3, '']]), defaults), { 3: '', 5: 'select 5' });
  assert.deepEqual(shortcutsToSetting(new Map([[3, 'select top (1000) * from {selection}'], [1, '  ']]), defaults), { 3: 'select top (1000) * from {selection}' });
  assert.deepEqual(shortcutsToSetting(new Map(), {}), {});
});

test('Ctrl+3 is query shortcut slot 3, whose default SQL is in the setting (not hard-coded)', () => {
  const setting = c.configuration.properties[`msSqlMcp.${QUERY_SHORTCUTS_SETTING}`];
  assert.equal(setting.type, 'object');
  assert.deepEqual(parseShortcuts(setting.default), new Map([[3, 'select top (1000) * from {selection}']]));
  assert.ok(!c.keybindings.some(k => k.command === 'msSqlMcp.selectTopRows'));
  for (const slot of SHORTCUT_SLOTS) {
    const key = c.keybindings.find(k => k.key === `ctrl+${slot}`);
    assert.equal(key?.command, `msSqlMcp.queryShortcut${slot}`);
    // Only a configured slot takes Ctrl+n from VS Code, and only in SQL editors.
    assert.match(key.when, new RegExp(`${shortcutContextKey(slot).replace(/\./g, '\\.')}$`));
    assert.match(key.when, /editorLangId == sql/);
    assert.ok(c.commands.some(x => x.command === `msSqlMcp.queryShortcut${slot}`));
  }
});
