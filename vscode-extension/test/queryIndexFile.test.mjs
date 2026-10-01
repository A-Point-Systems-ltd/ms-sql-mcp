import test from 'node:test';
import assert from 'node:assert/strict';
import { QueryIndexFile } from '../out/query/queryIndexFile.js';

const FILE = 'C:/store/sqldocs/query-index.json';
const errno = code => Object.assign(new Error(`${code}: something`), { code });
const entry = (title, over = {}) => ({ title, connection: 'dev', updatedAt: 1, ...over });

/** In-memory fs: `files` path → text; `fail` path → error thrown by readFile / rename; `ops` records writes and renames. */
function memFs(initial = {}) {
  const files = new Map(Object.entries(initial));
  const fail = { read: undefined, rename: undefined, write: undefined };
  const ops = [];
  return {
    files, fail, ops,
    async readFile(p) {
      if (fail.read) throw fail.read;
      if (!files.has(p)) throw errno('ENOENT');
      return files.get(p);
    },
    async writeFile(p, data) {
      if (fail.write) throw fail.write;
      ops.push(['write', p]);
      files.set(p, data);
    },
    async rename(from, to) {
      if (fail.rename) throw fail.rename;
      ops.push(['rename', from, to]);
      files.set(to, files.get(from));
      files.delete(from);
    },
    async mkdir() {},
  };
}

function indexFile(fs) {
  const warnings = [];
  return { file: new QueryIndexFile(FILE, fs, m => warnings.push(m), '42'), warnings };
}

test('a missing file reads as an empty index; the first update creates it atomically (tmp, then rename)', async () => {
  const fs = memFs();
  const { file, warnings } = indexFile(fs);
  assert.deepEqual(await file.read(), {});
  assert.equal(await file.update(i => ({ ...i, aaaa0001: entry('A') })), true);
  assert.deepEqual(fs.ops, [['write', `${FILE}.42.tmp`], ['rename', `${FILE}.42.tmp`, FILE]]);
  assert.ok(!fs.files.has(`${FILE}.42.tmp`));
  assert.deepEqual(JSON.parse(fs.files.get(FILE)), { aaaa0001: entry('A') });
  assert.deepEqual(warnings, []);
});

test('updates merge: each one re-reads the file (another window may have written it) and they are serialized', async () => {
  const fs = memFs({ [FILE]: JSON.stringify({ aaaa0001: entry('A') }) });
  const { file } = indexFile(fs);
  await file.update(i => ({ ...i, aaaa0002: entry('B') }));
  // Another window writes in between.
  const other = JSON.parse(fs.files.get(FILE));
  fs.files.set(FILE, JSON.stringify({ ...other, aaaa0003: entry('C') }));
  // Two concurrent updates from this window: neither is lost.
  await Promise.all([
    file.update(i => ({ ...i, aaaa0004: entry('D') })),
    file.update(i => ({ ...i, aaaa0005: entry('E') })),
  ]);
  assert.deepEqual(Object.keys(await file.read()).sort(), ['aaaa0001', 'aaaa0002', 'aaaa0003', 'aaaa0004', 'aaaa0005']);
});

test('a read error other than ENOENT throws, and update aborts without writing (warn, no content)', async () => {
  const fs = memFs({ [FILE]: JSON.stringify({ aaaa0001: entry('SELECT secret FROM clients') }) });
  const { file, warnings } = indexFile(fs);
  fs.fail.read = errno('EBUSY');
  await assert.rejects(() => file.read());
  assert.equal(await file.update(() => ({})), false);
  assert.deepEqual(fs.ops, [], 'nothing written');
  assert.ok(fs.files.get(FILE).includes('aaaa0001'), 'file unchanged');
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /EBUSY/);
  assert.ok(!warnings[0].includes('SELECT'));
});

test('a corrupt (non-empty, unparsable) file aborts the update and is left as it is', async () => {
  for (const corrupt of ['{"aaaa0001": {"title": "SELECT secret', '[1,2]', 'null']) {
    const fs = memFs({ [FILE]: corrupt });
    const { file, warnings } = indexFile(fs);
    await assert.rejects(() => file.read(), corrupt);
    assert.equal(await file.update(i => ({ ...i, aaaa0002: entry('B') })), false, corrupt);
    assert.equal(fs.files.get(FILE), corrupt, 'never overwritten');
    assert.deepEqual(fs.ops, []);
    assert.equal(warnings.length, 1);
    assert.ok(!warnings[0].includes('SELECT'), 'no content in the log');
  }
});

test('an update that returns its input writes nothing', async () => {
  const fs = memFs({ [FILE]: JSON.stringify({ aaaa0001: entry('A') }) });
  const { file } = indexFile(fs);
  assert.equal(await file.update(i => i), true);
  assert.deepEqual(fs.ops, []);
});

test('an empty file is an empty index (not corrupt)', async () => {
  const fs = memFs({ [FILE]: '' });
  const { file } = indexFile(fs);
  assert.deepEqual(await file.read(), {});
  assert.equal(await file.update(i => ({ ...i, aaaa0001: entry('A') })), true);
});

test('a failed write or rename leaves the old file in place; a later update still runs', async () => {
  const fs = memFs({ [FILE]: JSON.stringify({ aaaa0001: entry('A') }) });
  const { file, warnings } = indexFile(fs);
  fs.fail.rename = errno('EPERM');
  assert.equal(await file.update(i => ({ ...i, aaaa0002: entry('B') })), false);
  assert.deepEqual(Object.keys(JSON.parse(fs.files.get(FILE))), ['aaaa0001']);
  assert.match(warnings[0], /EPERM/);
  fs.fail.rename = undefined;
  assert.equal(await file.update(i => ({ ...i, aaaa0003: entry('C') })), true, 'the chain survives a failure');
  assert.deepEqual(Object.keys(await file.read()).sort(), ['aaaa0001', 'aaaa0003']);
});
