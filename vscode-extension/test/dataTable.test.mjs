import { test } from 'node:test';
import assert from 'node:assert/strict';
import { rowsToTable, parseReadData, rowCountLabel } from '../out/dataTable.js';

test('row objects become columns + data preserving first-row column order', () => {
  assert.deepEqual(rowsToTable([{ b: 1, a: null }, { b: 2, a: 'x' }]), { columns: ['b', 'a'], data: [[1, null], [2, 'x']] });
  assert.deepEqual(rowsToTable([]), { columns: [], data: [] });
});

test('keys missing from a row become null; keys only in later rows are appended', () => {
  assert.deepEqual(rowsToTable([{ a: 1 }, { b: 2 }]), { columns: ['a', 'b'], data: [[1, null], [null, 2]] });
});

test('read_data payload: data rows plus top-level truncated/maxRows (any casing)', () => {
  assert.deepEqual(parseReadData({ success: true, data: [{ a: 1 }], truncated: true, maxRows: 1 }), { rows: [{ a: 1 }], truncated: true, maxRows: 1 });
  assert.deepEqual(parseReadData({ Success: true, Data: [{ a: 1 }], Truncated: true, MaxRows: 1 }), { rows: [{ a: 1 }], truncated: true, maxRows: 1 });
  assert.deepEqual(parseReadData({ success: true, data: [] }), { rows: [], truncated: false, maxRows: undefined });
  assert.deepEqual(parseReadData({ success: true, data: [null, 5, { a: 2 }] }).rows, [{ a: 2 }]);
  assert.deepEqual(parseReadData(undefined), { rows: [], truncated: false, maxRows: undefined });
});

test('row count label', () => {
  assert.equal(rowCountLabel(0, false), '0 rows');
  assert.equal(rowCountLabel(1, false), '1 row');
  assert.equal(rowCountLabel(500, true), 'first 500 rows (truncated)');
});
