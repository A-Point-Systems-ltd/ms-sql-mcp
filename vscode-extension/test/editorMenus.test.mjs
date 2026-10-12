import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
const c = pkg.contributes;
const items = c.menus['view/item/context'];
const onConnection = m => /msSqlMcp\\.conn\\./.test(m.when);

test('a connection node has New Query and Open SQL File inline, and Refresh only in its context menu', () => {
  const inline = items.filter(m => onConnection(m) && m.group.startsWith('inline')).map(m => m.command);
  assert.deepEqual(inline, ['msSqlMcp.newQuery', 'msSqlMcp.openSqlFile']);
  assert.ok(items.some(m => m.command === 'msSqlMcp.openSqlFile' && onConnection(m) && !m.group.startsWith('inline')));
  const refresh = items.filter(m => m.command === 'msSqlMcp.refresh');
  assert.ok(refresh.some(m => onConnection(m) && !m.group.startsWith('inline')));
  assert.ok(!refresh.some(m => onConnection(m) && m.group.startsWith('inline')));
  // Category nodes keep their inline Refresh.
  assert.ok(refresh.some(m => /category/.test(m.when) && m.group.startsWith('inline')));
});

test('SQL editors offer Connect while unbound and Change Connection while bound, in the title and context menus', () => {
  for (const menu of ['editor/title', 'editor/context', 'editor/title/context']) {
    const change = c.menus[menu].find(m => m.command === 'msSqlMcp.changeConnection');
    const connect = c.menus[menu].find(m => m.command === 'msSqlMcp.connectEditor');
    assert.match(change.when, /editorLangId == sql/, menu);
    assert.match(change.when, /&& msSqlMcp\.editorBound$/, menu);
    assert.match(connect.when, /&& !msSqlMcp\.editorBound$/, menu);
  }
  const declared = new Set(c.commands.map(x => x.command));
  for (const id of ['msSqlMcp.connectEditor', 'msSqlMcp.openSqlFile']) assert.ok(declared.has(id), id);
});
