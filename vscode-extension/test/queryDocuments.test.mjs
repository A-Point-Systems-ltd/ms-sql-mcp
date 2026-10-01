import { test } from 'node:test';
import assert from 'node:assert/strict';
import { QUERY_DOCUMENTS_KEY, QueryDocuments } from '../out/query/queryDocuments.js';

/** In-memory stand-in for vscode.Memento. */
function memento(initial = {}) {
  const data = { ...initial };
  return { data, get: (key, def) => (key in data ? data[key] : def), update: async (key, value) => { data[key] = value; } };
}
const uri = s => ({ toString: () => s });

test('set / get / delete persist under msSqlMcp.queryDocuments and fire onDidChange with the key', async () => {
  const m = memento();
  const docs = new QueryDocuments(m);
  const changes = [];
  const sub = docs.onDidChange(k => changes.push(k));
  await docs.set(uri('untitled:Untitled-1'), { connection: 'dev', kind: 'query' });
  assert.equal(QUERY_DOCUMENTS_KEY, 'msSqlMcp.queryDocuments');
  assert.deepEqual(m.data[QUERY_DOCUMENTS_KEY], { 'untitled:Untitled-1': { connection: 'dev', kind: 'query' } });
  assert.deepEqual(docs.get(uri('untitled:Untitled-1')), { connection: 'dev', kind: 'query' });
  assert.deepEqual(docs.get('untitled:Untitled-1'), { connection: 'dev', kind: 'query' });
  assert.equal(docs.get(uri('untitled:Untitled-2')), undefined);

  await docs.delete(uri('untitled:Untitled-1'));
  assert.equal(docs.get(uri('untitled:Untitled-1')), undefined);
  assert.deepEqual(m.data[QUERY_DOCUMENTS_KEY], {});
  // Deleting a missing entry is a no-op.
  await docs.delete(uri('untitled:Untitled-1'));
  assert.deepEqual(changes, ['untitled:Untitled-1', 'untitled:Untitled-1']);

  sub.dispose();
  await docs.set(uri('file:///a.sql'), { connection: 'dev', kind: 'query' });
  assert.equal(changes.length, 2, 'disposed listener is not called');
});

test('entries are loaded from the memento; invalid ones are ignored', () => {
  const m = memento({
    [QUERY_DOCUMENTS_KEY]: {
      'file:///a.sql': { connection: 'dev', kind: 'object', object: { connection: 'dev', scriptType: 'View', schema: 'dbo', name: 'v' } },
      'file:///bad.sql': { connection: 7, kind: 'query' },
      'file:///bad2.sql': { connection: 'dev', kind: 'other' },
    },
  });
  const docs = new QueryDocuments(m);
  assert.equal(docs.get('file:///a.sql').kind, 'object');
  assert.equal(docs.get('file:///bad.sql'), undefined);
  assert.equal(docs.get('file:///bad2.sql'), undefined);
});

test('prune drops the entries the predicate rejects; rename moves an entry', async () => {
  const m = memento();
  const docs = new QueryDocuments(m);
  await docs.set('untitled:Untitled-1', { connection: 'dev', kind: 'query' });
  await docs.set('file:///a.sql', { connection: 'dev', kind: 'query' });
  await docs.set('file:///b.sql', { connection: 'prod', kind: 'query' });
  const changes = [];
  docs.onDidChange(k => changes.push(k));

  await docs.prune(key => key !== 'untitled:Untitled-1');
  assert.deepEqual(Object.keys(m.data[QUERY_DOCUMENTS_KEY]).sort(), ['file:///a.sql', 'file:///b.sql']);
  assert.deepEqual(changes, ['untitled:Untitled-1']);

  await docs.rename('file:///a.sql', 'file:///c.sql');
  assert.equal(docs.get('file:///a.sql'), undefined);
  assert.equal(docs.get('file:///c.sql').connection, 'dev');
  // Renaming an unassociated document changes nothing.
  await docs.rename('file:///zzz.sql', 'file:///y.sql');
  assert.equal(docs.get('file:///y.sql'), undefined);
});

test('mssql-ddl documents are never bound; other schemes are bindable', async () => {
  const { isNeverBound } = await import('../out/query/queryDocuments.js');
  assert.equal(isNeverBound('mssql-ddl'), true);
  assert.equal(isNeverBound('file'), false);
  assert.equal(isNeverBound('untitled'), false);
  assert.equal(isNeverBound('vscode-remote'), false);
});

test('closing a document drops its association unless it is a file', async () => {
  const { dropOnClose } = await import('../out/query/queryDocuments.js');
  assert.equal(dropOnClose('file'), false);
  assert.equal(dropOnClose('untitled'), true);
  assert.equal(dropOnClose('vscode-userdata'), true);
  assert.equal(dropOnClose('mssql-ddl'), true);
  // mssql-sql documents (query windows, object scripts) are dropped when closed clean; a dirty one keeps its binding
  // (the activation prune drops it later when no tab shows it).
  assert.equal(dropOnClose('mssql-sql'), true);
  assert.equal(dropOnClose('mssql-sql', false), true);
  assert.equal(dropOnClose('mssql-sql', true), false);
  assert.equal(dropOnClose('untitled', true), true);
  assert.equal(dropOnClose('file', false), false);
});

test('mssql-sql documents are bindable and survive activation only while a tab shows them', async () => {
  const { isNeverBound, keepOnActivation } = await import('../out/query/queryDocuments.js');
  assert.equal(isNeverBound('mssql-sql'), false);
  assert.equal(keepOnActivation('mssql-sql', true, () => false), true);
  assert.equal(keepOnActivation('mssql-sql', false, () => true), false);
});

test('ownedQueryIdsToPrune: only ids this workspace created and no tab shows', async () => {
  const { orphanQueryIds } = await import('../out/query/queryDocuments.js');
  assert.deepEqual(orphanQueryIds(['aaaa0001', 'aaaa0002', 'aaaa0003'], new Set(['aaaa0002'])), ['aaaa0001', 'aaaa0003']);
  assert.deepEqual(orphanQueryIds([], new Set(['aaaa0002'])), []);
  // Malformed ids from workspace state are dropped (never used as a path).
  assert.deepEqual(orphanQueryIds(['../x', 'AAAA0001', 'aaaa0004'], new Set()), ['aaaa0004']);
});

test('activation pruning: files must exist, other documents must be open, mssql-ddl never stays', async () => {
  const { keepOnActivation } = await import('../out/query/queryDocuments.js');
  assert.equal(keepOnActivation('file', false, () => true), true);
  assert.equal(keepOnActivation('file', true, () => false), false);
  assert.equal(keepOnActivation('untitled', true, () => false), true);
  assert.equal(keepOnActivation('untitled', false, () => true), false);
  assert.equal(keepOnActivation('vscode-remote', true, () => false), true);
  assert.equal(keepOnActivation('vscode-remote', false, () => true), false);
  assert.equal(keepOnActivation('mssql-ddl', true, () => true), false);
});

test('object associations keep a well-formed target and rebound flag; malformed ones are dropped on load', async () => {
  const good = { connection: 'dev', kind: 'object', object: { connection: 'dev', scriptType: 'View', name: 'v' }, target: { server: 'S', database: 'D' }, rebound: true };
  const m = memento({ [QUERY_DOCUMENTS_KEY]: {
    'file:///a.sql': good,
    'file:///b.sql': { ...good, target: { server: 'S' }, rebound: 'yes' },
    'file:///c.sql': { ...good, target: { raw: true }, rebound: false },
  } });
  const docs = new QueryDocuments(m);
  assert.deepEqual(docs.get('file:///a.sql'), good);
  assert.deepEqual(docs.get('file:///b.sql'), { connection: 'dev', kind: 'object', object: good.object });
  assert.deepEqual(docs.get('file:///c.sql'), { connection: 'dev', kind: 'object', object: good.object, target: { raw: true } });
  await docs.set('untitled:x', good);
  assert.deepEqual(m.data[QUERY_DOCUMENTS_KEY]['untitled:x'], good);
});
