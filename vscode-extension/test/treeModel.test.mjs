import { test } from 'node:test';
import assert from 'node:assert/strict';
import { CATEGORIES } from '../out/explorer/catalog.js';
import {
  CLOSED_MESSAGE, categoryKey, childFolderNodes, childNodes, childrenKey, connectionDescription, connectionPrefix, dataViewRequest, ddlText, describeNode,
  errorNode, nodeId, objectNodes, parseChildren, rootCategories, scriptArgs, subCategories,
} from '../out/explorer/treeModel.js';

const cat = id => CATEGORIES.find(c => c.id === id);
const profile = (over = {}) => ({ name: 'dev', server: 'DC\\DEV', database: 'db1', auth: 'windows', readOnly: true, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true, ...over });

test('connection item: description, contextValue, collapsible by open state', () => {
  assert.equal(connectionDescription(profile()), 'DC\\DEV/db1 · read-only');
  assert.equal(connectionDescription(profile({ open: false, readOnly: false })), 'DC\\DEV/db1 · closed');
  assert.equal(connectionDescription(profile({ auth: 'raw', rawConnectionString: 'x', server: '', database: '' })), 'connection string · read-only');
  const open = describeNode({ kind: 'connection', profile: profile() });
  assert.equal(open.label, 'dev');
  assert.equal(open.contextValue, 'msSqlMcp.conn.open');
  assert.equal(open.collapsible, true);
  assert.equal(open.command, undefined);
  const closed = describeNode({ kind: 'connection', profile: profile({ open: false }) });
  assert.equal(closed.contextValue, 'msSqlMcp.conn.closed');
  // Collapsible, so expanding it shows CLOSED_MESSAGE.
  assert.equal(closed.collapsible, true);
  assert.equal(CLOSED_MESSAGE, 'Closed - right-click › Open to browse');
});

test('root categories exclude security sub-categories; security has 4', () => {
  assert.deepEqual(rootCategories('dev').map(n => n.def.id), ['tables', 'views', 'procedures', 'tvfs', 'scalars', 'dbTriggers', 'types', 'security']);
  assert.deepEqual(subCategories('dev', cat('security')).map(n => n.def.label), ['Logins', 'Server Roles', 'Database Users', 'Database Roles']);
  const item = describeNode(rootCategories('dev')[0]);
  assert.equal(item.contextValue, 'msSqlMcp.category');
  assert.equal(item.collapsible, true);
  assert.equal(item.label, 'Tables');
  assert.equal(item.description, undefined);
  assert.equal(describeNode(rootCategories('dev')[0], { shown: 2, total: 10 }).description, '2 of 10');
});

test('object nodes: tables/views collapse without command, others open DDL on click', () => {
  const { nodes, total } = objectNodes('dev', cat('tables'), [{ schema: 'dbo', name: 'Orders' }, { schema: 'dbo', name: 'Lines' }], '');
  assert.equal(total, 2);
  assert.deepEqual(nodes[0].ref, { connection: 'dev', scriptType: 'Table', schema: 'dbo', name: 'Orders' });
  const t = describeNode(nodes[0]);
  assert.equal(t.label, 'dbo.Orders');
  assert.equal(t.contextValue, 'msSqlMcp.obj.table');
  assert.equal(t.collapsible, true);
  assert.equal(t.command, undefined);
  const v = describeNode(objectNodes('dev', cat('views'), [{ schema: 'dbo', name: 'vX' }], '').nodes[0]);
  assert.equal(v.contextValue, 'msSqlMcp.obj.view');
  const p = describeNode(objectNodes('dev', cat('procedures'), [{ schema: 'dbo', name: 'sp' }], '').nodes[0]);
  assert.equal(p.contextValue, 'msSqlMcp.obj.StoredProcedure');
  assert.equal(p.collapsible, false);
  assert.equal(p.command, 'msSqlMcp.showDdl');
  const l = describeNode(objectNodes('dev', cat('logins'), [{ name: 'app', detail: 'S · disabled' }], '').nodes[0]);
  assert.equal(l.label, 'app');
  assert.equal(l.description, 'S · disabled');
});

test('filter applies to object labels (schema.name, case-insensitive)', () => {
  const rows = [{ schema: 'dbo', name: 'Orders' }, { schema: 'sales', name: 'Lines' }, { schema: 'dbo', name: 'OrderLines' }];
  const r = objectNodes('dev', cat('tables'), rows, 'ORDER');
  assert.equal(r.total, 3);
  assert.deepEqual(r.nodes.map(n => n.ref.name), ['Orders', 'OrderLines']);
  assert.deepEqual(objectNodes('dev', cat('tables'), rows, 'sales.').nodes.map(n => n.ref.name), ['Lines']);
});

test('child folders per category; child nodes open DDL', () => {
  const [table] = objectNodes('dev', cat('tables'), [{ schema: 'dbo', name: 'T' }], '').nodes;
  const folders = childFolderNodes(table);
  assert.deepEqual(folders.map(f => describeNode(f).label), ['Indexes', 'Foreign Keys', 'Triggers']);
  assert.ok(folders.every(f => describeNode(f).contextValue === 'msSqlMcp.folder' && describeNode(f).collapsible));
  const [view] = objectNodes('dev', cat('views'), [{ schema: 'dbo', name: 'v' }], '').nodes;
  assert.deepEqual(childFolderNodes(view).map(f => f.folder), ['indexes']);

  const kids = parseChildren(cat('tables'), { constraints: [{ name: 'PK_T' }], foreignKeys: [{ name: 'FK_T', schema: 'dbo' }], triggers: [{ name: 'trT' }] }, 'dbo', 'T');
  const idx = childNodes('dev', 'indexes', kids);
  assert.deepEqual(idx[0].ref, { connection: 'dev', scriptType: 'Index', name: 'PK_T', parent: '[dbo].[T]' });
  const item = describeNode(idx[0]);
  assert.equal(item.label, 'PK_T');
  assert.equal(item.contextValue, 'msSqlMcp.obj.child');
  assert.equal(item.collapsible, false);
  assert.equal(item.command, 'msSqlMcp.showDdl');
  assert.deepEqual(childNodes('dev', 'foreignKeys', kids)[0].ref, { connection: 'dev', scriptType: 'ForeignKey', schema: 'dbo', name: 'FK_T' });
  assert.deepEqual(childNodes('dev', 'triggers', kids)[0].ref, { connection: 'dev', scriptType: 'TableTrigger', schema: 'dbo', name: 'trT' });
  const vk = parseChildren(cat('views'), { indexes: [{ name: 'IXV' }] }, 'dbo', 'v');
  assert.deepEqual(childNodes('dev', 'indexes', vk)[0].ref.parent, '[dbo].[v]');
  assert.deepEqual(childNodes('dev', 'triggers', vk), []);
});

test('script_object arguments: schema-scoped names bracketed, Index passes parent, principals plain', () => {
  assert.deepEqual(scriptArgs({ connection: 'dev', scriptType: 'Table', schema: 'dbo', name: 'a.b' }), { objectType: 'Table', name: '[dbo].[a.b]' });
  assert.deepEqual(scriptArgs({ connection: 'dev', scriptType: 'ForeignKey', schema: 'dbo', name: 'FK' }), { objectType: 'ForeignKey', name: '[dbo].[FK]' });
  assert.deepEqual(scriptArgs({ connection: 'dev', scriptType: 'TableTrigger', schema: 'dbo', name: 'tr' }), { objectType: 'TableTrigger', name: '[dbo].[tr]' });
  assert.deepEqual(scriptArgs({ connection: 'dev', scriptType: 'Index', name: 'IX', parent: '[dbo].[T]' }), { objectType: 'Index', name: 'IX', parent: '[dbo].[T]' });
  assert.deepEqual(scriptArgs({ connection: 'dev', scriptType: 'Login', name: 'DOMAIN\\u' }), { objectType: 'Login', name: 'DOMAIN\\u' });
  assert.deepEqual(scriptArgs({ connection: 'dev', scriptType: 'DatabaseTrigger', name: 'ddl' }), { objectType: 'DatabaseTrigger', name: 'ddl' });
});

test('ddl text: plain ddl, or header + warning comments when there are warnings', () => {
  assert.equal(ddlText({ form: 'CreateOrAlter', ddl: 'CREATE X', warnings: [] }, 'dev'), 'CREATE X');
  assert.equal(ddlText({ form: 'Create', ddl: 'CREATE X' }, 'dev'), 'CREATE X');
  assert.equal(ddlText({ form: 'Alter', ddl: 'ALTER X', warnings: ['partitioned', 'two\nlines'] }, 'dev'),
    '-- Generated by MSSQL-MCP (Alter) from dev\n-- WARNING: partitioned\n-- WARNING: two\n--   lines\n\nALTER X');
});

test('errors become message nodes; transient restart errors carry a retry hint', () => {
  const e = errorNode(new Error("The object is not visible to this login.\nmore"));
  assert.deepEqual([e.kind, e.isError, e.text], ['message', true, 'The object is not visible to this login.']);
  assert.equal(e.tooltip, 'The object is not visible to this login.\nmore');
  const r = errorNode(new Error('Explorer restarted while starting.'));
  assert.match(r.text, /Refresh to retry/);
  assert.match(errorNode(new Error('Client disposed.')).text, /Refresh to retry/);
  const m = describeNode(e);
  assert.equal(m.contextValue, 'msSqlMcp.message');
  assert.equal(m.collapsible, false);
  assert.equal(m.icon, 'error');
  assert.equal(describeNode({ kind: 'message', text: '(none)', isError: false }).icon, 'info');
});

test('cache keys and stable ids', () => {
  assert.equal(categoryKey('dev', 'tables'), 'dev|tables');
  assert.equal(childrenKey('dev', 'dbo', 'T'), 'dev|dbo.T|children');
  const [a] = objectNodes('dev', cat('tables'), [{ schema: 'dbo', name: 'T' }], '').nodes;
  const [b] = objectNodes('dev', cat('tables'), [{ schema: 'dbo', name: 'T' }], '').nodes;
  assert.equal(nodeId(a), nodeId(b));
  assert.notEqual(nodeId(a), nodeId(objectNodes('other', cat('tables'), [{ schema: 'dbo', name: 'T' }], '').nodes[0]));
  assert.equal(nodeId({ kind: 'message', text: 'x', isError: false }), undefined);
});

test('key and id components are escaped: "|" and "." inside names cannot collide', () => {
  assert.notEqual(childrenKey('dev', 'a.b', 'c'), childrenKey('dev', 'a', 'b.c'));
  assert.notEqual(childrenKey('dev', 'a|b', 'c'), childrenKey('dev', 'a', 'b|c'));
  assert.ok(childrenKey('dev', 'x|y', 'z').startsWith(connectionPrefix('dev')));
  assert.equal(childrenKey('dev', 'x|y', 'z').split('|').length, 3);
  const id = (schema, name) => nodeId(objectNodes('dev', cat('tables'), [{ schema, name }], '').nodes[0]);
  assert.notEqual(id('a|b', 'c'), id('a', 'b|c'));
  assert.equal(id('a|b', 'c').split('|').length, id('dbo', 'T').split('|').length);
});

test('data view request asks for one extra row so the server can report truncation', () => {
  assert.deepEqual(dataViewRequest({ connection: 'dev', scriptType: 'Table', schema: 'dbo', name: 'T' }, 500),
    { sql: 'SELECT TOP (501) * FROM [dbo].[T]', maxRows: 500 });
  assert.deepEqual(dataViewRequest({ connection: 'dev', scriptType: 'View', schema: 's', name: 'v]' }, 1),
    { sql: 'SELECT TOP (2) * FROM [s].[v]]]', maxRows: 1 });
});
