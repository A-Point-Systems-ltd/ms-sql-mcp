import test from 'node:test';
import assert from 'node:assert/strict';
import { EDITABLE_TYPES, isEditable, objectDocId } from '../out/explorer/objectEdit.js';
import { scriptArgs } from '../out/explorer/treeModel.js';

const ref = (over = {}) => ({ connection: 'dev', scriptType: 'View', schema: 'dbo', name: 'vOrders', ...over });

test('editable types are exactly view, procedure and both function kinds', () => {
  assert.deepEqual([...EDITABLE_TYPES], ['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction']);
  for (const t of EDITABLE_TYPES) assert.equal(isEditable(t), true);
  for (const t of ['Table', 'Index', 'ForeignKey', 'TableTrigger', 'DatabaseTrigger', 'Type', 'Login', '', 'view']) assert.equal(isEditable(t), false, t);
  assert.equal(isEditable(undefined), false);
});

test('scriptArgs passes form only when given', () => {
  const r = ref({ scriptType: 'StoredProcedure', name: 'p' });
  assert.deepEqual(scriptArgs(r), { objectType: 'StoredProcedure', name: '[dbo].[p]' });
  assert.ok(!('form' in scriptArgs(r)));
  assert.ok(!('form' in scriptArgs(r, '')));
  assert.ok(!('form' in scriptArgs(r, undefined)));
  assert.deepEqual(scriptArgs(r, 'alter'), { objectType: 'StoredProcedure', name: '[dbo].[p]', form: 'alter' });
});

test('objectDocId: 16 lowercase hex, same object same id', () => {
  const id = objectDocId(ref());
  assert.match(id, /^[0-9a-f]{16}$/);
  assert.equal(objectDocId(ref()), id);
  // The parent (child nodes) is not part of the identity.
  assert.equal(objectDocId(ref({ parent: 'x' })), id);
});

test('objectDocId is injective over connection + scriptType + schema + name', () => {
  const distinct = (a, b, label) => assert.notEqual(objectDocId(ref(a)), objectDocId(ref(b)), label);
  distinct({ connection: 'a:b' }, { connection: 'a_b' }, 'a:b vs a_b');
  distinct({ connection: 'dev' }, { connection: 'Dev' }, 'connection case');
  distinct({ name: 'Orders' }, { name: 'orders' }, 'name case (the id is lower-case hex, safe on NTFS)');
  distinct({ scriptType: 'View' }, { scriptType: 'StoredProcedure' }, 'script type');
  distinct({ schema: 'a.b', name: 'c' }, { schema: 'a', name: 'b.c' }, 'schema/name split');
  distinct({ schema: 'a', name: 'b\u0000c' }, { schema: 'a\u0000b', name: 'c' }, 'NUL inside a part');
  distinct({ connection: 'a', scriptType: 'bView' }, { connection: 'ab', scriptType: 'View' }, 'connection/type split');
  distinct({ schema: undefined, name: 'x' }, { schema: '', name: 'x' }, 'no schema vs empty schema');
  distinct({ schema: undefined, name: 'x' }, { schema: 'dbo', name: 'x' }, 'with and without schema');
  distinct({ name: 'p'.repeat(100) + 'x'.repeat(50) }, { name: 'p'.repeat(100) + 'y'.repeat(50) }, 'long names sharing a prefix');
});
