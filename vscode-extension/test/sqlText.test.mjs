import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bracket, qualified, previewSql, ddlUri, parseDdlUri } from '../out/explorer/sqlText.js';

test('bracket doubles closing brackets', () => {
  assert.equal(bracket('a]b'), '[a]]b]');
  assert.equal(qualified('dbo', 'Order Lines'), '[dbo].[Order Lines]');
  assert.equal(qualified(undefined, 't'), '[t]');
});

test('preview SQL is TOP-limited and quoted', () => {
  assert.equal(previewSql('sales', 'x]y', 500), 'SELECT TOP (500) * FROM [sales].[x]]y]');
});

test('ddl uri round-trips names with dots, spaces and unicode', () => {
  const ref = { connection: 'prod.eu', scriptType: 'Index', schema: 'sales', name: 'IX.א b', parent: 'sales.Order Lines' };
  const uri = ddlUri(ref);
  assert.match(uri, /^mssql-ddl:/);
  assert.match(uri, /\.sql\?/);
  assert.deepEqual(parseDdlUri(uri), ref);
});

test('ddl uri survives VS Code URI normalisation (encoded and decoded forms)', async () => {
  const { URI } = await import('vscode-uri');
  for (const name of ['a b', 'a+b', 'a&b', 'a=b', '100%', 'a%20b', 'a#b', 'a?b', 'IX.א', 'x.y.z', '[br]', "q'uote"]) {
    const ref = { connection: `c ${name}`, scriptType: 'Index', schema: `s${name}`, name, parent: `p.${name}` };
    const uri = ddlUri(ref);
    assert.deepEqual(parseDdlUri(uri), ref, `raw ${name}`);
    assert.deepEqual(parseDdlUri(URI.parse(uri).toString()), ref, `toString ${name}`);
    assert.deepEqual(parseDdlUri(URI.parse(uri).toString(true)), ref, `toString(true) ${name}`);
    assert.deepEqual(parseDdlUri(URI.parse(uri).query), ref, `query ${name}`);
  }
});
