import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildRunRequest, clampMaxRows, editorLine, parseRunScriptResult } from '../out/query/runScript.js';

test('buildRunRequest runs the whole text from line 0 without a selection', () => {
  assert.deepEqual(buildRunRequest('SELECT 1\nGO\nSELECT 2', undefined), { script: 'SELECT 1\nGO\nSELECT 2', lineOffset: 0 });
});

test('buildRunRequest runs a non-blank selection and offsets by its start line', () => {
  assert.deepEqual(buildRunRequest('a\nb\nSELECT 2\n', { text: 'SELECT 2', startLine: 2 }), { script: 'SELECT 2', lineOffset: 2 });
});

test('buildRunRequest ignores a whitespace-only selection', () => {
  assert.deepEqual(buildRunRequest('SELECT 1', { text: ' \n\t ', startLine: 4 }), { script: 'SELECT 1', lineOffset: 0 });
  assert.deepEqual(buildRunRequest('SELECT 1', { text: '', startLine: 4 }), { script: 'SELECT 1', lineOffset: 0 });
});

test('editorLine maps a 1-based script line to a 0-based editor line', () => {
  assert.equal(editorLine(1, 0), 0);
  assert.equal(editorLine(4, 0), 3);
  assert.equal(editorLine(1, 10), 10);
  assert.equal(editorLine(3, 10), 12);
  assert.equal(editorLine(null, 10), undefined);
  assert.equal(editorLine(0, 10), undefined);
  assert.equal(editorLine(-2, 10), undefined);
  assert.equal(editorLine(1.5, 0), undefined);
});

test('parseRunScriptResult keeps the Task 1 shape', () => {
  const payload = {
    success: true,
    data: {
      resultSets: [{ batch: 1, columns: [{ name: 'Id', type: 'int' }], rows: [[1], [2]], rowCount: 2, truncated: false }],
      messages: [{ kind: 'rows', text: '(2 rows affected)', line: null }, { kind: 'error', text: 'Msg 102', line: 4 }],
      hadErrors: true,
      batches: 2,
      elapsedMs: 41,
    },
  };
  assert.deepEqual(parseRunScriptResult(payload), payload.data);
});

test('parseRunScriptResult normalises missing and odd fields', () => {
  const r = parseRunScriptResult({ success: true, data: {} });
  assert.deepEqual(r, { resultSets: [], messages: [], hadErrors: false, batches: 0, elapsedMs: 0 });

  const odd = parseRunScriptResult({
    Success: true,
    Data: {
      ResultSets: [{ Columns: [{ Name: 'a' }, null], Rows: [[1, 2], 'x'], Truncated: true }, null],
      Messages: [{ kind: 'weird', text: 5, line: '3' }, { kind: 'error', text: 'boom', line: 2 }, null],
    },
  });
  assert.equal(odd.resultSets.length, 1);
  assert.deepEqual(odd.resultSets[0], { batch: 1, columns: [{ name: 'a', type: '' }, { name: '', type: '' }], rows: [[1, 2], []], rowCount: 2, truncated: true });
  assert.deepEqual(odd.messages, [{ kind: 'info', text: '5', line: null }, { kind: 'error', text: 'boom', line: 2 }]);
  // hadErrors falls back to the presence of an error message.
  assert.equal(odd.hadErrors, true);
});

test('parseRunScriptResult throws the error text on success:false and on a non-object', () => {
  assert.throws(() => parseRunScriptResult({ success: false, error: 'Unknown connection' }), /Unknown connection/);
  assert.throws(() => parseRunScriptResult({ success: false, message: 'denied' }), /denied/);
  assert.throws(() => parseRunScriptResult({ success: false }), /run_script failed/);
  assert.throws(() => parseRunScriptResult(undefined), /Unexpected run_script result/);
  assert.throws(() => parseRunScriptResult('text'), /Unexpected run_script result/);
});

test('clampMaxRows defaults to 1000 and clamps to 1..10000', () => {
  assert.equal(clampMaxRows(undefined), 1000);
  assert.equal(clampMaxRows('x'), 1000);
  assert.equal(clampMaxRows(NaN), 1000);
  assert.equal(clampMaxRows(0), 1);
  assert.equal(clampMaxRows(50_000), 10000);
  assert.equal(clampMaxRows(250.7), 250);
});
