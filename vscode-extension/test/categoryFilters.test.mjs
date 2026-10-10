import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { CategoryFilters } from '../out/tree/categoryFilters.js';

test('filters are per connection and per group', () => {
  const f = new CategoryFilters();
  assert.equal(f.set('dev', 'tables', ' Order '), true);
  assert.equal(f.set('DEV', 'tables', 'Order'), false, 'same term (connection names are case-insensitive)');
  f.set('dev', 'views', 'v_');
  f.set('prod', 'tables', 'Cust');
  assert.equal(f.get('Dev', 'tables'), 'Order');
  assert.equal(f.get('dev', 'views'), 'v_');
  assert.equal(f.get('prod', 'tables'), 'Cust');
  assert.equal(f.get('prod', 'views'), '');
  assert.equal(f.count, 3);
  assert.equal(f.set('dev', 'views', '  '), true, 'blank clears');
  assert.equal(f.get('dev', 'views'), '');
  assert.equal(f.count, 2);
});

test('filters round-trip through their stored state and ignore junk', () => {
  const f = new CategoryFilters();
  f.set('dev', 'tables', 'Order');
  f.set('prod', 'procedures', 'api');
  const again = new CategoryFilters(JSON.parse(JSON.stringify(f.toState())));
  assert.equal(again.get('dev', 'tables'), 'Order');
  assert.equal(again.get('prod', 'procedures'), 'api');
  assert.equal(new CategoryFilters({ dev: { tables: 5, views: 'x' }, bad: null }).count, 1);
  assert.equal(new CategoryFilters('nonsense').count, 0);
  assert.equal(again.clearAll(), true);
  assert.equal(again.count, 0);
  assert.equal(again.clearAll(), false);
});

test('package.json: filter and clear on each group, Clear All Filters in the title', () => {
  const c = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).contributes;
  const item = c.menus['view/item/context'];
  assert.ok(item.some(m => m.command === 'msSqlMcp.filter' && m.group.startsWith('inline') && /category\\.list/.test(m.when)));
  assert.ok(item.some(m => m.command === 'msSqlMcp.clearFilter' && m.group.startsWith('inline') && /category\.list\.filtered/.test(m.when)));
  const title = c.menus['view/title'].map(m => m.command);
  assert.ok(title.includes('msSqlMcp.clearAllFilters'));
  assert.ok(!title.includes('msSqlMcp.filter'));
  assert.ok(c.keybindings.some(k => k.command === 'msSqlMcp.filter' && k.key === 'ctrl+f'));
});

test('filters of removed connections are dropped', () => {
  const f = new CategoryFilters();
  f.set('dev', 'tables', 'Order');
  f.set('Gone', 'views', 'v_');
  assert.equal(f.retain(['DEV', 'prod']), true);
  assert.equal(f.get('dev', 'tables'), 'Order');
  assert.equal(f.get('gone', 'views'), '');
  assert.equal(f.count, 1);
  assert.equal(f.retain(['dev']), false);
});

test('tree keys do not fire while typing in an input', () => {
  const c = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).contributes;
  for (const id of ['msSqlMcp.renameObject', 'msSqlMcp.copyName', 'msSqlMcp.filter']) {
    const k = c.keybindings.find(x => x.command === id && /focusedView == msSqlMcp\.explorer/.test(x.when));
    assert.match(k.when, /!inputFocus/, id);
  }
});
