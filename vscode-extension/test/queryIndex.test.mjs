import test from 'node:test';
import assert from 'node:assert/strict';
import {
  QUERY_INDEX_FILE, QueryIndexFormatError, RETENTION_MS, closedEntry, closedQueryAction, isBlank, mergeRecovered, parseQueryIndex,
  queryPrunePlan, recentQueries, recoveredEntries, relativeTime, reopenObjectName, serializeQueryIndex, withEntry, withOpenCleared,
  withoutIds, writtenEntry,
} from '../out/query/queryIndex.js';

const DAY = 24 * 60 * 60 * 1000;
const NOW = Date.UTC(2026, 9, 1, 12, 0, 0);
const entry = (over = {}) => ({ title: 'Query 1 - S - D', connection: 'dev', updatedAt: NOW, ...over });
const text = t => ({ kind: 'text', text: t });
const MISSING = { kind: 'missing' };
const UNKNOWN = { kind: 'unknown' };

test('index file name and the 30-day retention', () => {
  assert.equal(QUERY_INDEX_FILE, 'query-index.json');
  assert.equal(RETENTION_MS, 30 * DAY);
});

test('parseQueryIndex keeps well-formed entries with valid ids (closedAt optional)', () => {
  const good = { aaaa0001: entry(), aaaa0002: entry({ title: 'Query 2 - S - D', connection: '', closedAt: NOW - DAY }) };
  assert.deepEqual(parseQueryIndex(JSON.stringify(good)), good);
  assert.deepEqual(parseQueryIndex(JSON.stringify({
    ...good,
    '../x': entry(),
    AAAA0003: entry(),
    aaaa0004: { title: 5, connection: 'dev', updatedAt: NOW },
    aaaa0005: { title: 't', connection: 'dev', updatedAt: 'yesterday' },
    aaaa0006: null,
  })), good);
  // A malformed closedAt is dropped, the entry stays (it then counts as open: never pruned).
  assert.deepEqual(parseQueryIndex(JSON.stringify({ aaaa0007: entry({ closedAt: 'x' }) })), { aaaa0007: entry() });
  assert.deepEqual(parseQueryIndex(serializeQueryIndex(good)), good, 'round trip');
});

test('parseQueryIndex: no text is an empty index; non-empty text that is not an index throws', () => {
  for (const empty of [undefined, '', '  \r\n']) assert.deepEqual(parseQueryIndex(empty), {}, JSON.stringify(empty));
  for (const bad of ['not json', '{"a":', '[]', 'null', '42', '"x"']) {
    assert.throws(() => parseQueryIndex(bad), QueryIndexFormatError, bad);
  }
  // The error message never carries the file content.
  try { parseQueryIndex('{"secret": "SELECT * FROM clients"'); } catch (err) { assert.ok(!String(err.message).includes('SELECT')); }
});

test('withEntry / withoutIds return new indexes', () => {
  const a = withEntry({}, 'aaaa0001', entry());
  const b = withEntry(a, 'aaaa0002', entry({ title: 'Query 2 - S - D' }));
  assert.deepEqual(Object.keys(a), ['aaaa0001']);
  assert.deepEqual(Object.keys(b).sort(), ['aaaa0001', 'aaaa0002']);
  assert.deepEqual(Object.keys(withoutIds(b, ['aaaa0001', 'zzzz'])), ['aaaa0002']);
  assert.deepEqual(Object.keys(b).sort(), ['aaaa0001', 'aaaa0002'], 'input unchanged');
});

test('writtenEntry clears closedAt; closedEntry sets it', () => {
  const closed = closedEntry('T', 'dev', NOW);
  assert.deepEqual(closed, { title: 'T', connection: 'dev', updatedAt: NOW, closedAt: NOW });
  const written = writtenEntry(closed, 'T2', undefined, NOW + 1);
  assert.deepEqual(written, { title: 'T2', connection: 'dev', updatedAt: NOW + 1 }, 'connection kept from the old entry');
  assert.ok(!('closedAt' in written));
  assert.deepEqual(writtenEntry(undefined, 'T', undefined, NOW), { title: 'T', connection: '', updatedAt: NOW });
  assert.deepEqual(closedEntry('T', undefined, NOW, entry({ connection: 'prod' })), { title: 'T', connection: 'prod', updatedAt: NOW, closedAt: NOW });
});

test('recentQueries: newest first', () => {
  const index = { aaaa0001: entry({ updatedAt: NOW - 3 * DAY }), aaaa0002: entry({ updatedAt: NOW }), aaaa0003: entry({ updatedAt: NOW - DAY }) };
  assert.deepEqual(recentQueries(index).map(e => e.id), ['aaaa0002', 'aaaa0003', 'aaaa0001']);
  assert.equal(recentQueries(index)[0].connection, 'dev');
});

test('relativeTime', () => {
  assert.equal(relativeTime(NOW, NOW), 'just now');
  assert.equal(relativeTime(NOW + 5000, NOW), 'just now', 'clock skew');
  assert.equal(relativeTime(NOW - 59 * 1000, NOW), 'just now');
  assert.equal(relativeTime(NOW - 60 * 1000, NOW), '1 minute ago');
  assert.equal(relativeTime(NOW - 5 * 60 * 1000, NOW), '5 minutes ago');
  assert.equal(relativeTime(NOW - 60 * 60 * 1000, NOW), '1 hour ago');
  assert.equal(relativeTime(NOW - 23 * 60 * 60 * 1000, NOW), '23 hours ago');
  assert.equal(relativeTime(NOW - DAY, NOW), '1 day ago');
  assert.equal(relativeTime(NOW - 29 * DAY, NOW), '29 days ago');
});

test('isBlank: empty or whitespace-only', () => {
  for (const t of ['', ' ', '\r\n\t ', ' ']) assert.equal(isBlank(t), true, JSON.stringify(t));
  for (const t of ['x', '  -- comment', '\nSELECT 1']) assert.equal(isBlank(t), false, JSON.stringify(t));
});

test('closing a query document: blank is deleted, text kept, missing forgotten, an unreadable file left alone', () => {
  assert.equal(closedQueryAction(text('')), 'delete');
  assert.equal(closedQueryAction(text(' \r\n')), 'delete');
  assert.equal(closedQueryAction(MISSING), 'forget', 'backing file already gone');
  assert.equal(closedQueryAction(UNKNOWN), 'leave', 'a read error is not "missing"');
  assert.equal(closedQueryAction(text('SELECT 1')), 'keep');
  assert.equal(closedQueryAction(text('-- notes')), 'keep');
});

test('queryPrunePlan: only closed entries older than 30 days are removed; entries without closedAt never are', () => {
  const index = {
    aaaa0001: entry({ updatedAt: NOW - 40 * DAY, closedAt: NOW - 31 * DAY }), // closed long ago, not open: removed
    aaaa0002: entry({ updatedAt: NOW - 40 * DAY, closedAt: NOW - 31 * DAY }), // closed long ago but open here: kept
    aaaa0003: entry({ updatedAt: NOW - 40 * DAY, closedAt: NOW - 29 * DAY }), // closed recently: kept
    aaaa0008: entry({ updatedAt: NOW - 400 * DAY }), // no closedAt (open in some window, or never closed): never pruned
    aaaa0004: entry({ updatedAt: NOW - 40 * DAY, closedAt: NOW - 31 * DAY }), // owned and missing: forgotten, not removed
  };
  const plan = queryPrunePlan({
    index,
    owned: ['aaaa0002', 'aaaa0005', 'aaaa0006', 'aaaa0007', 'aaaa0004', 'aaaa0009', '../bad'],
    open: new Set(['aaaa0002', 'aaaa0007']),
    blank: new Set(['aaaa0005', 'aaaa0007']), // 0007 is blank but open (a new empty query window): kept
    missing: new Set(['aaaa0004', 'aaaa0006']), // 0009 could not be read (unknown): in neither set, left alone
    now: NOW,
  });
  assert.deepEqual(plan.remove.sort(), ['aaaa0001', 'aaaa0005']);
  assert.deepEqual(plan.forget.sort(), ['../bad', 'aaaa0004', 'aaaa0006']);
  const empty = queryPrunePlan({ index: {}, owned: [], open: new Set(), blank: new Set(), missing: new Set(), now: NOW });
  assert.deepEqual(empty, { remove: [], forget: [] });
});

test('recoveredEntries: a non-blank backing file without an index entry is re-added', () => {
  const index = { aaaa0001: entry() };
  const files = [
    { id: 'aaaa0001', mtimeMs: NOW - DAY, read: text('SELECT 1') }, // already indexed
    { id: 'aaaa0002', mtimeMs: NOW - 2 * DAY, read: text('SELECT 2') }, // not open: recovered as closed at its mtime
    { id: 'aaaa0003', mtimeMs: NOW - 3 * DAY, read: text('SELECT 3') }, // open here: its tab title, not closed
    { id: 'aaaa0004', mtimeMs: NOW, read: text('  ') }, // blank: not recovered
    { id: 'aaaa0005', mtimeMs: NOW, read: UNKNOWN }, // unreadable: not touched
    { id: 'aaaa0006', mtimeMs: NOW, read: MISSING },
  ];
  const recovered = recoveredEntries(index, files, new Map([['aaaa0003', 'Query 3 - S - D']]), NOW);
  assert.deepEqual(recovered, {
    // closedAt = now: a recovery always starts a fresh 30-day clock (updatedAt stays the file's mtime, for the list).
    aaaa0002: { title: 'Recovered query aaaa0002', connection: '', updatedAt: NOW - 2 * DAY, closedAt: NOW },
    aaaa0003: { title: 'Query 3 - S - D', connection: '', updatedAt: NOW - 3 * DAY },
  });
  // Merged into the index as it is at write time: an entry another window added meanwhile wins.
  const current = { aaaa0001: entry(), aaaa0002: entry({ title: 'From another window' }) };
  const merged = mergeRecovered(current, recovered);
  assert.equal(merged.aaaa0002.title, 'From another window');
  assert.equal(merged.aaaa0003.title, 'Query 3 - S - D');
  assert.deepEqual(mergeRecovered(current, {}), current);
});

test('an old orphan file is recovered and is not removed by the same run\'s prune', () => {
  const recovered = recoveredEntries({}, [{ id: 'aaaa0001', mtimeMs: NOW - 400 * DAY, read: text('SELECT 1') }], new Map(), NOW);
  const index = mergeRecovered({}, recovered);
  assert.equal(index.aaaa0001.closedAt, NOW);
  const plan = queryPrunePlan({ index, owned: [], open: new Set(), blank: new Set(), missing: new Set(), now: NOW });
  assert.deepEqual(plan.remove, []);
  // ... and it is removed only 30 days after the recovery.
  const later = queryPrunePlan({ index, owned: [], open: new Set(), blank: new Set(), missing: new Set(), now: NOW + RETENTION_MS + 1 });
  assert.deepEqual(later.remove, ['aaaa0001']);
});

test('withOpenCleared: ids open in this window lose closedAt before the prune plans', () => {
  const index = {
    aaaa0001: entry({ closedAt: NOW - 40 * DAY }),
    aaaa0002: entry({ closedAt: NOW - 40 * DAY }),
    aaaa0003: entry(),
  };
  const cleared = withOpenCleared(index, new Set(['aaaa0001', 'aaaa0003', 'aaaa0009']));
  assert.ok(!('closedAt' in cleared.aaaa0001));
  assert.equal(cleared.aaaa0002.closedAt, NOW - 40 * DAY);
  assert.deepEqual(cleared.aaaa0003, index.aaaa0003);
  assert.equal(withOpenCleared(index, new Set(['aaaa0003'])), index, 'nothing to clear: the same object (no write)');
  const plan = queryPrunePlan({ index: cleared, owned: [], open: new Set(), blank: new Set(), missing: new Set(), now: NOW });
  assert.deepEqual(plan.remove, ['aaaa0002']);
});

test('reopen title: keeps its Query number unless an open tab has it', () => {
  let n = 7;
  const next = () => n++;
  assert.equal(reopenObjectName('Query 3 - S - D', new Set([1, 2]), next), 'Query 3');
  assert.equal(reopenObjectName('Query 3 - S - D', new Set([3]), next), 'Query 7');
  assert.equal(reopenObjectName('Recovered query aaaa0002', new Set(), next), 'Query 8');
});
