import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RunRegistry } from '../out/query/runRegistry.js';

test('one run per document; finish ends it', () => {
  const r = new RunRegistry();
  const t = r.start('untitled:Untitled-1');
  assert.ok(t);
  assert.equal(r.has('untitled:Untitled-1'), true);
  assert.equal(r.start('untitled:Untitled-1'), undefined, 'a second run is refused while the first runs');
  assert.ok(r.start('untitled:Untitled-2'), 'other documents run independently');
  r.finish(t);
  assert.equal(r.has('untitled:Untitled-1'), false);
  assert.ok(r.start('untitled:Untitled-1'));
});

test('cancel aborts the signal and keeps the run current until it settles', () => {
  const r = new RunRegistry();
  const t = r.start('k');
  r.cancel('k');
  assert.equal(t.controller.signal.aborted, true);
  assert.equal(r.isCurrent(t), true);
  r.finish(t);
  assert.equal(r.isCurrent(t), false);
  r.cancel('k'); // nothing running: no-op
});

test('closing a document aborts its run and a new document with the same uri inherits nothing', () => {
  const r = new RunRegistry();
  const old = r.start('untitled:Untitled-1');
  assert.equal(r.close('untitled:Untitled-1'), true);
  assert.equal(old.controller.signal.aborted, true);
  assert.equal(r.isCurrent(old), false, 'the old run must not update state any more');
  assert.equal(r.has('untitled:Untitled-1'), false, 'no "already running" guard for the reused uri');

  const fresh = r.start('untitled:Untitled-1');
  assert.ok(fresh);
  // The old run settling later must not clear the new run's guard.
  r.finish(old);
  assert.equal(r.isCurrent(fresh), true);
  // Cancel reaches only the new run's signal.
  r.cancel('untitled:Untitled-1');
  assert.equal(fresh.controller.signal.aborted, true);
  assert.equal(r.close('untitled:Untitled-9'), false);
});

test('dispose aborts every run', () => {
  const r = new RunRegistry();
  const a = r.start('a');
  const b = r.start('b');
  r.dispose();
  assert.equal(a.controller.signal.aborted && b.controller.signal.aborted, true);
  assert.equal(r.has('a') || r.has('b'), false);
});

test('keys lists the documents with a current run', () => {
  const r = new RunRegistry();
  assert.deepEqual(r.keys(), []);
  const a = r.start('untitled:a');
  r.start('file:///b.sql');
  assert.deepEqual(r.keys().sort(), ['file:///b.sql', 'untitled:a']);
  r.finish(a);
  r.close('file:///b.sql');
  assert.deepEqual(r.keys(), []);
});
