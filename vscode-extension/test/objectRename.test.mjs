import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { copyNameText, dependentsSql, renameSql, renameUnsupported, renameWarning, validateNewName } from '../out/explorer/objectRename.js';

const ref = (over = {}) => ({ connection: 'dev', scriptType: 'Table', schema: 'dbo', name: 'Units', ...over });

test('Copy Name gives schema.name for schema-scoped objects, else the name', () => {
  assert.equal(copyNameText(ref()), 'dbo.Units');
  assert.equal(copyNameText(ref({ scriptType: 'DatabaseRole', schema: undefined, name: 'api_programmers' })), 'api_programmers');
  assert.equal(copyNameText(ref({ scriptType: 'Index', schema: undefined, name: 'IX_a', parent: '[dbo].[Units]' })), 'IX_a');
});

test('rename SQL: sp_rename with a plain new name, or ALTER ... WITH NAME for principals', () => {
  assert.equal(renameSql(ref(), 'Units2'), "EXEC sys.sp_rename @objname = N'[dbo].[Units]', @newname = N'Units2', @objtype = N'OBJECT';");
  assert.equal(renameSql(ref({ name: "O'Brien]x" }), "New 'one'"),
    "EXEC sys.sp_rename @objname = N'[dbo].[O''Brien]]x]', @newname = N'New ''one''', @objtype = N'OBJECT';");
  assert.equal(renameSql(ref({ scriptType: 'Index', schema: undefined, name: 'IX_a', parent: '[dbo].[Units]' }), 'IX_b'),
    "EXEC sys.sp_rename @objname = N'[dbo].[Units].[IX_a]', @newname = N'IX_b', @objtype = N'INDEX';");
  assert.equal(renameSql(ref({ scriptType: 'Type', name: 'Phone' }), 'PhoneNo'),
    "EXEC sys.sp_rename @objname = N'[dbo].[Phone]', @newname = N'PhoneNo', @objtype = N'USERDATATYPE';");
  assert.equal(renameSql(ref({ scriptType: 'DatabaseRole', schema: undefined, name: 'api' }), 'api]2'), 'ALTER ROLE [api] WITH NAME = [api]]2];');
  assert.equal(renameSql(ref({ scriptType: 'Login', schema: undefined, name: 'CORP\\x' }), 'CORP\\y'), 'ALTER LOGIN [CORP\\x] WITH NAME = [CORP\\y];');
  assert.equal(renameSql(ref({ scriptType: 'ServerRole', schema: undefined, name: 'ops' }), 'ops2'), 'ALTER SERVER ROLE [ops] WITH NAME = [ops2];');
});

test('unsupported objects and invalid names are refused', () => {
  assert.match(renameUnsupported(ref({ scriptType: 'DatabaseTrigger' })), /database triggers/);
  assert.equal(renameUnsupported(ref()), undefined);
  assert.throws(() => renameSql(ref({ scriptType: 'DatabaseTrigger' }), 'x'));
  assert.equal(validateNewName(ref(), 'Units'), 'This is the current name.');
  assert.equal(validateNewName(ref(), ''), 'Enter a name.');
  assert.match(validateNewName(ref(), ' x'), /spaces/);
  assert.match(validateNewName(ref(), 'a'.repeat(129)), /128/);
  assert.match(validateNewName(ref(), 'a\nb'), /control/);
  assert.equal(validateNewName(ref(), 'Units2'), undefined);
});

test('dependents are checked for objects and types, and the warning lists them', () => {
  assert.match(dependentsSql(ref()), /referenced_class = 1 AND d\.referenced_id = OBJECT_ID\(N'\[dbo\]\.\[Units\]'\)/);
  assert.match(dependentsSql(ref({ scriptType: 'Type', name: 'Phone' })), /TYPE_ID\(N'\[dbo\]\.\[Phone\]'\)/);
  assert.equal(dependentsSql(ref({ scriptType: 'DatabaseRole', schema: undefined })), undefined);
  assert.equal(dependentsSql(ref({ scriptType: 'Index', parent: '[dbo].[Units]' })), undefined);
  const w = renameWarning(ref({ scriptType: 'View', name: 'vUnits' }), 'vUnits2', ['dbo.p1', 'dbo.v2']);
  assert.match(w, /2 object\(s\) reference it by name and will break: dbo\.p1, dbo\.v2/);
  assert.match(w, /stored definition keeps the old name/);
  assert.doesNotMatch(renameWarning(ref(), 'X', []), /reference it/);
});

test('package.json: Copy Name and Rename on object items, F2 in the tree', () => {
  const c = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).contributes;
  const items = c.menus['view/item/context'].map(m => m.command);
  assert.ok(items.includes('msSqlMcp.copyName') && items.includes('msSqlMcp.renameObject'));
  const f2 = c.keybindings.find(k => k.command === 'msSqlMcp.renameObject');
  assert.equal(f2.key, 'f2');
  assert.equal(f2.when, 'focusedView == msSqlMcp.explorer');
});
