import assert from 'node:assert/strict';
import test from 'node:test';
import { changeNote, defaultInput, describeTarget, isShown, parseToolResult, toServerInput, viewToInput, type ManagedView } from '../src/model';

const view: ManagedView = {
  name: 'prod', auth: 'sql', server: 'srv', database: 'db', user: 'sa', hasPassword: true, encrypt: 'mandatory',
  trustServerCertificate: false, readOnly: false, insights: true, rawConnectionString: null, isOpen: true, error: null,
};

test('defaults match the VS Code form', () => {
  const d = defaultInput();
  assert.equal(d.auth, 'windows');
  assert.equal(d.readOnly, true);
  assert.equal(d.encrypt, 'optional');
  assert.equal(d.trustServerCertificate, true);
});

test('field visibility per auth', () => {
  assert.equal(isShown('password', 'sql'), true);
  assert.equal(isShown('password', 'windows'), false);
  assert.equal(isShown('user', 'entraInteractive'), true);
  assert.equal(isShown('server', 'raw'), false);
  assert.equal(isShown('rawConnectionString', 'raw'), true);
});

test('editing never pre-fills the password', () => {
  assert.equal(viewToInput(view).password, '');
  assert.equal(viewToInput(view).user, 'sa');
});

test('hidden fields are sent empty so a stale password never leaves the form', () => {
  const input = toServerInput({ ...viewToInput(view), auth: 'windows', password: 'typed-before-switch', user: ' sa ', name: ' prod ' });
  assert.equal(input.password, '');
  assert.equal(input.user, '');
  assert.equal(input.name, 'prod');
  assert.equal(toServerInput({ ...viewToInput(view), password: ' keep spaces ' }).password, ' keep spaces ');
});

test('describeTarget', () => {
  assert.equal(describeTarget(view), 'srv / db');
  assert.equal(describeTarget({ ...view, auth: 'raw' }), 'raw connection string');
});

test('change notes never contain the password', () => {
  const note = changeNote('added', { ...viewToInput(view), password: 'p@ss' });
  assert.match(note, /'prod' \(srv\/db, read-write\)/);
  assert.doesNotMatch(note, /p@ss/);
  assert.equal(changeNote('removed', { name: 'x', readOnly: true }), "The user removed the connection 'x'.");
});

test('parseToolResult reads structured or text content and surfaces tool errors', () => {
  assert.deepEqual(parseToolResult({ structuredContent: { a: 1 } }), { a: 1 });
  assert.deepEqual(parseToolResult({ content: [{ type: 'text', text: '{"a":2}' }] }), { a: 2 });
  assert.throws(() => parseToolResult({ isError: true, content: [{ type: 'text', text: '{"success":false,"error":"nope"}' }] }), /nope/);
  assert.throws(() => parseToolResult({ content: [{ type: 'text', text: 'plain failure' }] }), /plain failure/);
});
