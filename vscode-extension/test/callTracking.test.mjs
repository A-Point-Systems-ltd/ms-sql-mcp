import { test } from 'node:test';
import assert from 'node:assert/strict';
import { CallCounter, raceAbort } from '../out/client/callTracking.js';
import { McpToolError } from '../out/client/parse.js';

test('whenIdle runs at once when no call is in flight', () => {
  const c = new CallCounter();
  let ran = 0;
  c.whenIdle(() => ran++);
  assert.equal(ran, 1);
  assert.equal(c.inFlight, 0);
});

test('whenIdle is deferred until the last in-flight call ends', () => {
  const c = new CallCounter();
  const endA = c.begin();
  const endB = c.begin();
  assert.equal(c.inFlight, 2);
  const ran = [];
  c.whenIdle(() => ran.push('reset'));
  c.whenIdle(() => ran.push('second'));
  endA();
  assert.deepEqual(ran, []);
  // Ending the same call twice does not count twice.
  endA();
  assert.equal(c.inFlight, 1);
  assert.deepEqual(ran, []);
  endB();
  assert.equal(c.inFlight, 0);
  assert.deepEqual(ran, ['reset', 'second']);
  // Callbacks run once only.
  c.begin()();
  assert.deepEqual(ran, ['reset', 'second']);
});

test('raceAbort resolves with the promise when the signal does not fire', async () => {
  assert.equal(await raceAbort(Promise.resolve(5), new AbortController().signal), 5);
  assert.equal(await raceAbort(Promise.resolve(6), undefined), 6);
  await assert.rejects(raceAbort(Promise.reject(new Error('spawn failed')), new AbortController().signal), /spawn failed/);
});

test('raceAbort rejects at once with a cancelled McpToolError when the signal fires first', async () => {
  const ac = new AbortController();
  let resolveSlow;
  const slow = new Promise(r => { resolveSlow = r; });
  const started = Date.now();
  const raced = raceAbort(slow, ac.signal);
  setTimeout(() => ac.abort(), 20);
  await assert.rejects(raced, err => err instanceof McpToolError && err.cancelled === true && err.message === 'Cancelled.');
  assert.ok(Date.now() - started < 500);
  resolveSlow(1);

  const aborted = new AbortController();
  aborted.abort();
  await assert.rejects(raceAbort(new Promise(() => {}), aborted.signal), err => err.cancelled === true);
});

test('raceAbort does not leave an unhandled rejection when the losing promise fails later', async () => {
  const unhandled = [];
  const onUnhandled = e => unhandled.push(e);
  process.on('unhandledRejection', onUnhandled);
  try {
    const ac = new AbortController();
    let rejectLater;
    const late = new Promise((_, rej) => { rejectLater = rej; });
    const raced = raceAbort(late, ac.signal);
    ac.abort();
    await assert.rejects(raced, err => err.cancelled === true);
    rejectLater(new Error('late failure'));
    await new Promise(r => setTimeout(r, 30));
    assert.deepEqual(unhandled, []);
  } finally {
    process.off('unhandledRejection', onUnhandled);
  }
});
