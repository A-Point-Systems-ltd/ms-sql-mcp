import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildConnectionString, validateProfile } from '../out/connections/profile.js';

const base = { name: 'dev', server: 'DC\\DEV', database: 'Sales', auth: 'windows', readOnly: false, insights: true, open: true, encrypt: 'optional', trustServerCertificate: true };

test('windows auth uses Integrated Security and no password', () => {
  const cs = buildConnectionString(base, undefined);
  assert.match(cs, /Data Source=DC\\DEV/);
  assert.match(cs, /Initial Catalog=Sales/);
  assert.match(cs, /Integrated Security=True/);
  assert.doesNotMatch(cs, /Password/i);
});

test('sql auth quotes passwords containing ; and "', () => {
  const cs = buildConnectionString({ ...base, auth: 'sql', user: 'app' }, 'a;b"c');
  assert.match(cs, /User ID=app/);
  assert.ok(cs.includes('Password="a;b""c"'), cs);
});

test('entra interactive sets Authentication keyword', () => {
  assert.match(buildConnectionString({ ...base, auth: 'entraInteractive', user: 'me@contoso.com' }, undefined), /Authentication="Active Directory Interactive"/);
});

test('read-only profile adds ApplicationIntent=ReadOnly', () => {
  assert.match(buildConnectionString({ ...base, readOnly: true }, undefined), /Application Intent=ReadOnly/);
});

test('raw auth passes the string through', () => {
  assert.equal(buildConnectionString({ ...base, auth: 'raw', rawConnectionString: 'Server=x;Database=y;Trusted_Connection=True' }, undefined), 'Server=x;Database=y;Trusted_Connection=True');
});

test('validation catches bad names and missing fields', () => {
  assert.deepEqual(validateProfile(base), []);
  assert.ok(validateProfile({ ...base, name: 'has space' }).length > 0);
  assert.ok(validateProfile({ ...base, server: '' }).length > 0);
  assert.ok(validateProfile({ ...base, auth: 'sql', user: '' }).length > 0);
});
