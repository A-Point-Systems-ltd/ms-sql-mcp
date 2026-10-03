import { test } from 'node:test';
import assert from 'node:assert/strict';
import { asScriptTarget, describeTarget, parseRawTarget, targetMismatch, targetOf, wrongTargetPrompt } from '../out/query/targetGuard.js';

const profile = (over = {}) => ({ name: 'dev', server: 'DEVSRV', database: 'db1', auth: 'windows', readOnly: false, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true, ...over });
const raw = cs => profile({ auth: 'raw', server: '', database: '', rawConnectionString: cs });
const object = (over = {}) => ({ connection: 'dev', kind: 'object', target: { server: 'DEVSRV', database: 'db1' }, ...over });

test('targetOf takes server/database from a profile, trimmed', () => {
  assert.deepEqual(targetOf(profile({ server: ' DEVSRV ', database: ' db1 ' })), { server: 'DEVSRV', database: 'db1' });
});

test('parseRawTarget reads Data Source / Server and Initial Catalog / Database, quoted or not, any key case', () => {
  assert.deepEqual(parseRawTarget('Data Source=srv1;Initial Catalog=Sales;Integrated Security=True'), { server: 'srv1', database: 'Sales' });
  assert.deepEqual(parseRawTarget('server = tcp:srv2,1433 ; DATABASE=Hr'), { server: 'tcp:srv2,1433', database: 'Hr' });
  assert.deepEqual(parseRawTarget('Address="a;b";Database=\'it\'\'s\''), { server: 'a;b', database: "it's" });
  assert.deepEqual(parseRawTarget('Data Source=srv3'), { server: 'srv3', database: '' });
});

test('parseRawTarget gives { raw: true } when no server can be found', () => {
  assert.deepEqual(parseRawTarget(''), { raw: true });
  assert.deepEqual(parseRawTarget('Integrated Security=True;Initial Catalog=x'), { raw: true });
  assert.deepEqual(parseRawTarget('garbage without pairs'), { raw: true });
  assert.deepEqual(targetOf(raw('Data Source=r1;Initial Catalog=d1')), { server: 'r1', database: 'd1' });
});

test('targetMismatch: same server/database (case-insensitive, trimmed) is no mismatch', () => {
  assert.equal(targetMismatch({ server: 'devsrv', database: 'DB1' }, profile()), false);
  assert.equal(targetMismatch({ server: ' DEVSRV', database: 'db1 ' }, profile()), false);
});

test('targetMismatch: another server or database is a mismatch', () => {
  assert.equal(targetMismatch({ server: 'DEVSRV', database: 'db1' }, profile({ server: 'PRODSRV' })), true);
  assert.equal(targetMismatch({ server: 'DEVSRV', database: 'db1' }, profile({ database: 'db2' })), true);
});

test('targetMismatch: a missing stored target, or raw against known, is a mismatch; raw against raw is not', () => {
  assert.equal(targetMismatch(undefined, profile()), true);
  assert.equal(targetMismatch({ raw: true }, profile()), true);
  assert.equal(targetMismatch({ server: 'DEVSRV', database: 'db1' }, raw('no server here')), true);
  assert.equal(targetMismatch({ raw: true }, raw('no server here')), false);
  assert.equal(targetMismatch({ server: 'r1', database: 'd1' }, raw('Data Source=R1;Initial Catalog=D1')), false);
});

test('wrongTargetPrompt: free queries and matching object documents run without asking', () => {
  assert.equal(wrongTargetPrompt({ connection: 'dev', kind: 'query' }, profile({ server: 'PRODSRV' })), undefined);
  assert.equal(wrongTargetPrompt(object(), profile()), undefined);
});

test('wrongTargetPrompt: an edited profile pointing elsewhere asks with both targets and the connection name', () => {
  assert.equal(
    wrongTargetPrompt(object(), profile({ server: 'PRODSRV', database: 'live' })),
    "This script was generated from DEVSRV\\db1 but will run on PRODSRV\\live (connection 'dev'). Run anyway?");
});

test('wrongTargetPrompt: a document rebound with Change Connection asks even when the targets look equal', () => {
  assert.equal(
    wrongTargetPrompt(object({ connection: 'other', rebound: true }), profile({ name: 'other' })),
    "This script was generated from DEVSRV\\db1 but will run on DEVSRV\\db1 (connection 'other'). Run anyway?");
});

test('wrongTargetPrompt: an unknown origin is described as such', () => {
  assert.equal(
    wrongTargetPrompt(object({ target: undefined }), profile()),
    "This script was generated from an unknown server but will run on DEVSRV\\db1 (connection 'dev'). Run anyway?");
  assert.equal(describeTarget({ raw: true }), 'an unknown server');
});

test('asScriptTarget keeps well-formed targets only', () => {
  assert.deepEqual(asScriptTarget({ server: 's', database: 'd' }), { server: 's', database: 'd' });
  assert.deepEqual(asScriptTarget({ raw: true }), { raw: true });
  assert.equal(asScriptTarget({ server: 's' }), undefined);
  assert.equal(asScriptTarget('s/d'), undefined);
  assert.equal(asScriptTarget(null), undefined);
});
