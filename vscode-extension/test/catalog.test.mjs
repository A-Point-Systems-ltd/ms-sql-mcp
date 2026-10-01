import { test } from 'node:test';
import assert from 'node:assert/strict';
import { CATEGORIES, parseObjectList, parseTableChildren, parseViewIndexes } from '../out/explorer/catalog.js';

const cat = id => CATEGORIES.find(c => c.id === id);

test('list parser accepts "schema.name" strings (tables) and objects (views, security)', () => {
  assert.deepEqual(parseObjectList(['dbo.A', 'sales.B'], cat('tables')), [{ schema: 'dbo', name: 'A' }, { schema: 'sales', name: 'B' }]);
  assert.deepEqual(parseObjectList([{ schema: 'dbo', name: 'vX', id: 1 }], cat('views')), [{ schema: 'dbo', name: 'vX' }]);
  assert.deepEqual(parseObjectList([{ name: 'app', type: 'S', isDisabled: true }], cat('logins')), [{ name: 'app', detail: 'S · disabled' }]);
  assert.deepEqual(parseObjectList([{ schema: 'dbo', name: 'T1', kind: 'Alias', baseType: 'int' }], cat('types')), [{ schema: 'dbo', name: 'T1', detail: 'Alias' }]);
  assert.deepEqual(parseObjectList(undefined, cat('tables')), []);
  assert.deepEqual(parseObjectList(['dot.in.name'], cat('tables')), [{ schema: 'dot', name: 'in.name' }]);
});

test('table children include PK/UQ constraints as indexes, fks and triggers', () => {
  const d = {
    constraints: [{ name: 'PK_T', type: 'PRIMARY_KEY_CONSTRAINT', keys: 'Id' }],
    indexes: [{ name: 'IX_T_A', type: 'NONCLUSTERED', keys: 'A' }],
    foreignKeys: [{ name: 'FK_T_U', schema: 'dbo', table_name: 'T' }],
    triggers: [{ name: 'trT', is_disabled: false }],
  };
  const c = parseTableChildren(d, 'dbo', 'T');
  assert.deepEqual(c.indexes.map(i => i.name), ['PK_T', 'IX_T_A']);
  assert.ok(c.indexes.every(i => i.scriptType === 'Index' && i.parent === 'dbo.T'));
  assert.deepEqual(c.foreignKeys[0], { scriptType: 'ForeignKey', schema: 'dbo', name: 'FK_T_U' });
  assert.deepEqual(c.triggers[0], { scriptType: 'TableTrigger', schema: 'dbo', name: 'trT' });
  assert.deepEqual(parseTableChildren(undefined, 'dbo', 'T'), { indexes: [], foreignKeys: [], triggers: [] });
});

test('view indexes', () => {
  assert.deepEqual(parseViewIndexes({ indexes: [{ name: 'IXV' }] }, 'dbo', 'v').map(i => [i.name, i.parent]), [['IXV', 'dbo.v']]);
  assert.deepEqual(parseViewIndexes({}, 'dbo', 'v'), []);
});

test('rows without a usable name are skipped', () => {
  assert.deepEqual(parseObjectList([{ schema: 'dbo' }, { name: 5 }, null, '', { name: 'ok' }], cat('views')), [{ name: 'ok' }]);
  const c = parseTableChildren({ constraints: [{ type: 'x' }], indexes: [{ name: 'I' }, {}], foreignKeys: [{ name: null }], triggers: [{}] }, 'dbo', 'T');
  assert.deepEqual(c.indexes.map(i => i.name), ['I']);
  assert.equal(c.foreignKeys.length, 0);
  assert.equal(c.triggers.length, 0);
  assert.deepEqual(parseViewIndexes({ indexes: [{}, { name: 'V' }] }, 'dbo', 'v').map(i => i.name), ['V']);
});
