import test from 'node:test';
import assert from 'node:assert/strict';
import {
  QUERY_INDEX_FILE, RETENTION_MS, closedQueryAction, isBlank, objectPrunePlan, parseQueryIndex, queryPrunePlan, recentQueries,
  relativeTime, reopenObjectName, serializeQueryIndex, withEntry, withoutIds,
} from '../out/query/queryIndex.js';

const DAY = 24 * 60 * 60 * 1000;
const NOW = Date.UTC(2026, 9, 1, 12, 0, 0);
const entry = (over = {}) => ({ title: 'Query 1 - S - D', connection: 'dev', updatedAt: NOW, ...over });

test('index file name and the 30-day retention', () => {
  assert.equal(QUERY_INDEX_FILE, 'query-index.json');
  assert.equal(RETENTION_MS, 30 * DAY);
});

test('parseQueryIndex keeps well-formed entries with valid ids; anything else is an empty index', () => {
  const good = { aaaa0001: entry(), aaaa0002: entry({ title: 'Query 2 - S - D', connection: '' }) };
  assert.deepEqual(parseQueryIndex(JSON.stringify(good)), good);
  assert.deepEqual(parseQueryIndex(JSON.stringify({
    ...good,
    '../x': entry(),
    AAAA0003: entry(),
    aaaa0004: { title: 5, connection: 'dev', updatedAt: NOW },
    aaaa0005: { title: 't', connection: 'dev', updatedAt: 'yesterday' },
    aaaa0006: null,
  })), good);
  for (const bad of [undefined, '', 'not json', '[]', 'null', '42', '"x"']) assert.deepEqual(parseQueryIndex(bad), {}, String(bad));
  assert.deepEqual(parseQueryIndex(serializeQueryIndex(good)), good, 'round trip');
});

test('withEntry / withoutIds return new indexes', () => {
  const a = withEntry({}, 'aaaa0001', entry());
  const b = withEntry(a, 'aaaa0002', entry({ title: 'Query 2 - S - D' }));
  assert.deepEqual(Object.keys(a), ['aaaa0001']);
  assert.deepEqual(Object.keys(b).sort(), ['aaaa0001', 'aaaa0002']);
  assert.deepEqual(Object.keys(withoutIds(b, ['aaaa0001', 'zzzz'])), ['aaaa0002']);
  assert.deepEqual(Object.keys(b).sort(), ['aaaa0001', 'aaaa0002'], 'input unchanged');
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

test('closing a query document: blank content is deleted, anything else is kept (never loses text)', () => {
  assert.equal(closedQueryAction(''), 'delete');
  assert.equal(closedQueryAction(' \r\n'), 'delete');
  assert.equal(closedQueryAction(undefined), 'forget', 'backing file already gone');
  assert.equal(closedQueryAction('SELECT 1'), 'keep');
  assert.equal(closedQueryAction('-- notes'), 'keep');
});

test('queryPrunePlan: old kept docs and blank owned orphans go; open, recent and session ids stay', () => {
  const index = {
    aaaa0001: entry({ updatedAt: NOW - 31 * DAY }), // old, not open: removed
    aaaa0002: entry({ updatedAt: NOW - 31 * DAY }), // old but open: kept
    aaaa0003: entry({ updatedAt: NOW - 29 * DAY }), // recent: kept
    aaaa0004: entry({ updatedAt: NOW - 31 * DAY }), // old, missing: forgotten
  };
  const plan = queryPrunePlan({
    index,
    owned: ['aaaa0002', 'aaaa0005', 'aaaa0006', 'aaaa0007', 'aaaa0004', '../bad'],
    open: new Set(['aaaa0002', 'aaaa0007']),
    blank: new Set(['aaaa0005', 'aaaa0007']), // 0007 is blank but open (a new empty query window): kept
    missing: new Set(['aaaa0004', 'aaaa0006']),
    now: NOW,
  });
  assert.deepEqual(plan.remove.sort(), ['aaaa0001', 'aaaa0005']);
  assert.deepEqual(plan.forget.sort(), ['../bad', 'aaaa0004', 'aaaa0006']);
  const empty = queryPrunePlan({ index: {}, owned: [], open: new Set(), blank: new Set(), missing: new Set(), now: NOW });
  assert.deepEqual(empty, { remove: [], forget: [] });
});

test('objectPrunePlan: clean, unbound object backing files older than 30 days', () => {
  const files = [
    { id: '0000000000000001', mtimeMs: NOW - 31 * DAY },
    { id: '0000000000000002', mtimeMs: NOW - 31 * DAY }, // open
    { id: '0000000000000003', mtimeMs: NOW - 31 * DAY }, // bound
    { id: '0000000000000004', mtimeMs: NOW - 29 * DAY }, // recent
    { id: 'NOT-AN-ID', mtimeMs: NOW - 31 * DAY },
  ];
  assert.deepEqual(objectPrunePlan(files, new Set(['0000000000000002']), new Set(['0000000000000003']), NOW), ['0000000000000001']);
});

test('reopen title: keeps its Query number unless an open tab has it', () => {
  let n = 7;
  const next = () => n++;
  assert.equal(reopenObjectName('Query 3 - S - D', new Set([1, 2]), next), 'Query 3');
  assert.equal(reopenObjectName('Query 3 - S - D', new Set([3]), next), 'Query 7');
  assert.equal(reopenObjectName('Something else', new Set(), next), 'Query 8');
});
