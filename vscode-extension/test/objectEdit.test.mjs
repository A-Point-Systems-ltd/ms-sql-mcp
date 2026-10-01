import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { EDITABLE_TYPES, isEditable, editFilePath, safeSegment } from '../out/explorer/objectEdit.js';
import { scriptArgs } from '../out/explorer/treeModel.js';

const ref = (over = {}) => ({ connection: 'dev', scriptType: 'View', schema: 'dbo', name: 'vOrders', ...over });
const segs = p => p.split(/[\\/]/);

test('editable types are exactly view, procedure and both function kinds', () => {
  assert.deepEqual([...EDITABLE_TYPES], ['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction']);
  for (const t of EDITABLE_TYPES) assert.equal(isEditable(t), true);
  for (const t of ['Table', 'Index', 'ForeignKey', 'TableTrigger', 'DatabaseTrigger', 'Type', 'Login', '', 'view']) assert.equal(isEditable(t), false, t);
  assert.equal(isEditable(undefined), false);
});

test('editFilePath layout: root/edits/connection/scriptType/schema.name.sql', () => {
  const p = editFilePath(path.join('C:', 'store'), ref());
  assert.deepEqual(segs(p).slice(-4), ['edits', 'dev', 'View', 'dbo.vOrders.sql']);
  assert.equal(p, path.join('C:', 'store', 'edits', 'dev', 'View', 'dbo.vOrders.sql'));
});

test('a ref without a schema uses the bare name', () => {
  assert.equal(segs(editFilePath('r', ref({ schema: undefined }))).pop(), 'vOrders.sql');
});

test('illegal characters and control characters become underscores', () => {
  assert.equal(safeSegment('a<b>c:d"e/f\\g|h?i*j'), 'a_b_c_d_e_f_g_h_i_j');
  assert.equal(safeSegment('a\u0000b\u001fc\u007fd'), 'a_b_c_d');
  const p = editFilePath('r', ref({ connection: 'a/b', name: 'x:y' }));
  assert.deepEqual(segs(p).slice(-3), ['a_b', 'View', 'dbo.x_y.sql']);
});

test('traversal segments cannot escape the folder', () => {
  const p = editFilePath('r', ref({ connection: '..', name: '..' }));
  assert.ok(!segs(p).includes('..'));
  assert.equal(safeSegment('..'), '_');
  assert.equal(safeSegment(''), '_');
});

test('trailing dots and spaces are trimmed', () => {
  assert.equal(safeSegment('name. . '), 'name');
  assert.equal(safeSegment('name...'), 'name');
  assert.equal(safeSegment('   '), '_');
});

test('Windows reserved names are prefixed, case-insensitively, with or without extension', () => {
  for (const n of ['CON', 'con', 'PRN', 'Aux', 'NUL', 'COM1', 'com9', 'LPT1', 'lpt9', 'CON.txt', 'nul.sql', 'dbo.CON']) {
    const s = safeSegment(n);
    if (n === 'dbo.CON') assert.equal(s, 'dbo.CON', 'only the first dotted part counts');
    else assert.equal(s, `_${n}`, n);
  }
  for (const n of ['COM0', 'COM10', 'CONSOLE', 'LPT', 'auxx']) assert.equal(safeSegment(n), n, n);
  // schema.name is joined first, so an object named after a device is not a device name once a schema precedes it.
  assert.equal(segs(editFilePath('r', ref({ schema: 'CON', name: 'x' }))).pop(), '_CON.x.sql');
});

test('segments are capped at 100 characters', () => {
  assert.equal(safeSegment('x'.repeat(250)).length, 100);
  const long = 'y'.repeat(99) + '. ';
  assert.equal(safeSegment(long), 'y'.repeat(99));
  const last = segs(editFilePath('r', ref({ name: 'z'.repeat(300) }))).pop();
  assert.ok(last.endsWith('.sql'));
  assert.ok(last.length <= 104);
});

test('scriptArgs passes form only when given', () => {
  const r = ref({ scriptType: 'StoredProcedure', name: 'p' });
  assert.deepEqual(scriptArgs(r), { objectType: 'StoredProcedure', name: '[dbo].[p]' });
  assert.ok(!('form' in scriptArgs(r)));
  assert.ok(!('form' in scriptArgs(r, '')));
  assert.ok(!('form' in scriptArgs(r, undefined)));
  assert.deepEqual(scriptArgs(r, 'alter'), { objectType: 'StoredProcedure', name: '[dbo].[p]', form: 'alter' });
});
