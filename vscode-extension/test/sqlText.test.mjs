import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bracket, ddlUri, definitionUnavailable, hasExecutableSql, parseDdlUri, qualified } from '../out/explorer/sqlText.js';

test('bracket doubles closing brackets', () => {
  assert.equal(bracket('a]b'), '[a]]b]');
  assert.equal(qualified('dbo', 'Order Lines'), '[dbo].[Order Lines]');
  assert.equal(qualified(undefined, 't'), '[t]');
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

test('hasExecutableSql ignores whitespace, line and nested block comments, and GO lines', () => {
  assert.equal(hasExecutableSql(''), false);
  assert.equal(hasExecutableSql('  \r\n\t'), false);
  assert.equal(hasExecutableSql('-- WARNING: [dbo].[p] is WITH ENCRYPTION; definition not available.'), false);
  assert.equal(hasExecutableSql('/* a /* nested */ still comment */\n-- x\nGO\n  go 5 -- again\r\nGO'), false);
  assert.equal(hasExecutableSql('/* unterminated'), false);
});

test('hasExecutableSql finds a statement, also after comments and next to them', () => {
  assert.equal(hasExecutableSql('-- WARNING: x\r\nCREATE OR ALTER PROCEDURE dbo.p AS SELECT 1\r\nGO'), true);
  assert.equal(hasExecutableSql('/* c */ SELECT 1'), true);
  assert.equal(hasExecutableSql("'-- not a comment'"), true);
  assert.equal(hasExecutableSql('GOTO label'), true);
  assert.equal(hasExecutableSql('/* c */ GO'), true);
});

test('definitionUnavailable: a comment-only script or an unavailable-definition warning', () => {
  assert.equal(definitionUnavailable('-- WARNING: [dbo].[clr] has no T-SQL definition (CLR or extended object) and is not scripted.', []), true);
  assert.equal(definitionUnavailable('CREATE OR ALTER VIEW v AS SELECT 1', ['[dbo].[v] is WITH ENCRYPTION; definition not available.']), true);
  assert.equal(definitionUnavailable('CREATE OR ALTER VIEW v AS SELECT 1', ['ALTER drops the indexes of an indexed view.']), false);
});
