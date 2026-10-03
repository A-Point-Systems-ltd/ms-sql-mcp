import { test } from 'node:test';
import assert from 'node:assert/strict';
import { defaultFormValues, profileToFormValues, formToProfile, ENCRYPTION_HELP, AUTH_OPTIONS, parseFormMessage, formToProbeProfile, PROBE_NAME } from '../out/connections/connectionFormModel.js';
import { renderConnectionForm } from '../out/connections/connectionFormHtml.js';

const ctx = (over = {}) => ({ existing: undefined, hasSavedPassword: false, existingNames: [], ...over });
const filled = (over = {}) => ({ ...defaultFormValues(), name: 'dev', server: 'DC\\DEV', database: 'Sales', ...over });

test('defaults', () => {
  assert.deepEqual(defaultFormValues(), {
    name: '', auth: 'windows', server: '', database: '', user: '', password: '', rawConnectionString: '',
    encrypt: 'optional', trustServerCertificate: true, readOnly: true, insights: true, open: true, ddlHistory: false, color: '',
  });
  assert.deepEqual(AUTH_OPTIONS.map(a => a.kind), ['windows', 'sql', 'entraInteractive', 'entraDefault', 'raw']);
  assert.deepEqual(Object.keys(ENCRYPTION_HELP).sort(), ['mandatory', 'optional', 'strict']);
});

test('a valid windows form becomes a profile without a password', () => {
  const r = formToProfile(filled(), ctx());
  assert.deepEqual(r.errors, {});
  assert.equal(r.password, undefined);
  assert.deepEqual(r.profile, {
    name: 'dev', server: 'DC\\DEV', database: 'Sales', auth: 'windows', user: undefined, readOnly: true, insights: true, open: true,
    encrypt: 'optional', trustServerCertificate: true, rawConnectionString: undefined, ddlHistory: false,
  });
  assert.equal(formToProfile(filled({ color: 'red' }), ctx()).profile.color, 'red');
});

test('missing server, database, user and password give per-field errors', () => {
  const r = formToProfile(defaultFormValues(), ctx());
  assert.ok(r.errors.name && r.errors.server && r.errors.database);
  assert.equal(r.profile, undefined);
  const sql = formToProfile(filled({ auth: 'sql' }), ctx());
  assert.ok(sql.errors.user, 'user required for sql');
  assert.ok(sql.errors.password, 'password required on add');
  assert.ok(formToProfile(filled({ auth: 'entraInteractive' }), ctx()).errors.user);
});

test('invalid and duplicate names', () => {
  assert.ok(formToProfile(filled({ name: 'a b' }), ctx()).errors.name);
  assert.match(formToProfile(filled({ name: 'DEV' }), ctx({ existingNames: ['dev'] })).errors.name, /already exists/);
});

test('${env: is rejected in every text input', () => {
  const env = '${env:SECRET}';
  const r = formToProfile(filled({ auth: 'sql', user: 'u', password: 'x', server: env, database: env }), ctx());
  assert.ok(r.errors.server && r.errors.database);
  assert.ok(formToProfile(filled({ auth: 'sql', user: env, password: 'x' }), ctx()).errors.user);
  assert.ok(formToProfile(filled({ auth: 'sql', user: 'u', password: 'a${env:X}' }), ctx()).errors.password);
  assert.ok(formToProfile(filled({ auth: 'raw', rawConnectionString: 'Server=${env:X}' }), ctx()).errors.rawConnectionString);
});

test('raw: needs a connection string without a password; server and database are ignored', () => {
  assert.ok(formToProfile(filled({ auth: 'raw' }), ctx()).errors.rawConnectionString);
  assert.match(formToProfile(filled({ auth: 'raw', rawConnectionString: 'Server=x;Password=p' }), ctx()).errors.rawConnectionString, /password/i);
  assert.ok(formToProfile(filled({ auth: 'raw', rawConnectionString: 'Server=x;PWD = p' }), ctx()).errors.rawConnectionString);
  const ok = formToProfile({ ...defaultFormValues(), name: 'raw1', auth: 'raw', rawConnectionString: ' Server=x;Database=y ', user: 'ignored' }, ctx());
  assert.deepEqual(ok.errors, {});
  assert.equal(ok.profile.rawConnectionString, 'Server=x;Database=y');
  assert.equal(ok.profile.server, '');
  assert.equal(ok.profile.user, undefined);
});

test('sql: password is returned; edit with an empty password keeps the saved one', () => {
  const add = formToProfile(filled({ auth: 'sql', user: 'app', password: 'p;w' }), ctx());
  assert.deepEqual(add.errors, {});
  assert.equal(add.password, 'p;w');
  assert.equal(add.profile.user, 'app');

  const existing = add.profile;
  const keep = formToProfile(profileToFormValues(existing), ctx({ existing, hasSavedPassword: true, existingNames: ['dev'] }));
  assert.deepEqual(keep.errors, {});
  assert.equal(keep.password, undefined);

  const noSaved = formToProfile(profileToFormValues(existing), ctx({ existing, hasSavedPassword: false, existingNames: ['dev'] }));
  assert.ok(noSaved.errors.password);
});

test('edit: the name is read-only and not checked against itself', () => {
  const existing = formToProfile(filled(), ctx()).profile;
  const r = formToProfile({ ...profileToFormValues(existing), name: 'renamed' }, ctx({ existing, existingNames: ['dev'] }));
  assert.deepEqual(r.errors, {});
  assert.equal(r.profile.name, 'dev');
});

test('a password is dropped when the auth is not sql', () => {
  const r = formToProfile(filled({ password: 'leftover' }), ctx());
  assert.equal(r.password, undefined);
});

test('entraDefault and windows need no user', () => {
  for (const auth of ['entraDefault', 'windows']) {
    const r = formToProfile(filled({ auth, user: 'x' }), ctx());
    assert.deepEqual(r.errors, {});
    assert.equal(r.profile.user, undefined);
  }
});

test('profileToFormValues never carries a password and round-trips the profile', () => {
  const p = { name: 'p', server: 's', database: 'd', auth: 'sql', user: 'u', readOnly: false, insights: false, open: false, encrypt: 'strict', trustServerCertificate: false };
  const v = profileToFormValues(p);
  assert.equal(v.password, '');
  assert.deepEqual(v, { name: 'p', auth: 'sql', server: 's', database: 'd', user: 'u', password: '', rawConnectionString: '', encrypt: 'strict', trustServerCertificate: false, readOnly: false, insights: false, open: false, ddlHistory: false, color: '' });
  assert.equal(profileToFormValues({ ...p, ddlHistory: true }).ddlHistory, true);
});

test('parseFormMessage validates the webview message shape', () => {
  const values = filled();
  assert.equal(parseFormMessage({ type: 'test', values })?.type, 'test');
  assert.equal(parseFormMessage({ type: 'listDatabases', values })?.type, 'listDatabases');
  assert.equal(parseFormMessage({ type: 'cancel' })?.type, 'cancel');
  assert.equal(parseFormMessage({ type: 'rm -rf', values }), undefined);
  assert.equal(parseFormMessage({ type: 'save' }), undefined);
  assert.equal(parseFormMessage({ type: 'save', values: { ...values, auth: 'nope' } }), undefined);
  assert.equal(parseFormMessage({ type: 'save', values: { ...values, encrypt: 'x' } }), undefined);
  assert.equal(parseFormMessage({ type: 'save', values: { ...values, readOnly: 'yes' } }), undefined);
  assert.equal(parseFormMessage({ type: 'save', values: { ...values, server: 5 } }), undefined);
  assert.equal(parseFormMessage({ type: 'save', values: { ...values, ddlHistory: 'yes' } }), undefined);
  const { ddlHistory, ...withoutHistory } = values;
  assert.equal(parseFormMessage({ type: 'save', values: withoutHistory }), undefined);
  assert.equal(parseFormMessage({ type: 'save', values: { ...values, ddlHistory: true } })?.values.ddlHistory, true);
  assert.equal(parseFormMessage(null), undefined);
  assert.equal(parseFormMessage('x'), undefined);
});

test('probe profile: fixed name, database override, open, field errors', () => {
  const ok = formToProbeProfile(filled({ name: '', database: 'Sales', open: false }), { hasPassword: false, database: 'master' });
  assert.deepEqual(ok.errors, {});
  assert.equal(ok.profile.name, PROBE_NAME);
  assert.equal(ok.profile.database, 'master');
  assert.equal(ok.profile.open, true);
  assert.equal(formToProbeProfile(filled({ database: '' }), { hasPassword: false }).errors.database, 'Database is required.');
  assert.ok(formToProbeProfile(filled({ auth: 'sql', user: 'u' }), { hasPassword: false }).errors.password);
  assert.deepEqual(formToProbeProfile(filled({ auth: 'sql', user: 'u' }), { hasPassword: true }).errors, {});
});

const render = (values, over = {}) => renderConnectionForm(values, { mode: 'add', hasSavedPassword: false, nonce: 'NONCE123', cspSource: 'vscode-resource:', ...over });
const tagOf = (html, id) => html.match(new RegExp(`<[a-z]+[^>]*\\bid="${id}"[^>]*>`))?.[0];
const wrapperClass = (html, field) => html.match(new RegExp(`<div class="([^"]*)"[^>]*data-field="${field}"`))?.[1] ?? '';

test('html: strict CSP carries the nonce and the script is nonced', () => {
  const html = render(defaultFormValues());
  assert.match(html, /Content-Security-Policy[^>]*default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-NONCE123'/);
  assert.match(html, /<script nonce="NONCE123">/);
  assert.doesNotMatch(html, /<script(?![^>]*nonce=)/);
});

test('html: the password input never has a value; the saved hint is only on edit', () => {
  const pw = tagOf(render({ ...defaultFormValues(), auth: 'sql', password: 'secret' }), 'password');
  assert.match(pw, /type="password"/);
  assert.doesNotMatch(pw, /\bvalue=/);
  assert.doesNotMatch(render({ ...defaultFormValues(), password: 'secret' }), /secret/);
  assert.match(tagOf(render(filled({ auth: 'sql' }), { mode: 'edit', hasSavedPassword: true }), 'password'), /placeholder="Saved - leave empty to keep"/);
  assert.doesNotMatch(tagOf(render(filled({ auth: 'sql' })), 'password'), /Saved/);
});

test('html: values are escaped', () => {
  const html = render({ ...defaultFormValues(), name: '"><script>alert(1)</script>', server: '<img onerror=x>', rawConnectionString: '</textarea><script>x</script>' });
  assert.doesNotMatch(html, /<script>alert/);
  assert.doesNotMatch(html, /<img onerror/);
  assert.doesNotMatch(html, /<\/textarea><script>x/);
  assert.match(html, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
});

test('html: fields that do not apply to the chosen auth are hidden', () => {
  const win = render(defaultFormValues());
  assert.match(wrapperClass(win, 'rawConnectionString'), /\bhidden\b/);
  assert.doesNotMatch(wrapperClass(win, 'server'), /\bhidden\b/);
  assert.doesNotMatch(wrapperClass(win, 'encrypt'), /\bhidden\b/);
  assert.match(wrapperClass(win, 'user'), /\bhidden\b/);
  assert.match(wrapperClass(win, 'password'), /\bhidden\b/);
  const raw = render({ ...defaultFormValues(), auth: 'raw' });
  assert.doesNotMatch(wrapperClass(raw, 'rawConnectionString'), /\bhidden\b/);
  assert.match(wrapperClass(raw, 'server'), /\bhidden\b/);
  assert.match(wrapperClass(raw, 'encrypt'), /\bhidden\b/);
  const sql = render({ ...defaultFormValues(), auth: 'sql' });
  assert.doesNotMatch(wrapperClass(sql, 'user'), /\bhidden\b/);
  assert.doesNotMatch(wrapperClass(sql, 'password'), /\bhidden\b/);
});

test('html: edit mode makes the name read-only; add mode does not', () => {
  assert.match(tagOf(render(filled(), { mode: 'edit' }), 'name'), /\breadonly\b/);
  assert.doesNotMatch(tagOf(render(filled()), 'name'), /\breadonly\b/);
});

test('html: checkboxes and selects reflect the values', () => {
  const html = render({ ...defaultFormValues(), readOnly: false, trustServerCertificate: false, encrypt: 'strict', auth: 'entraDefault' });
  assert.doesNotMatch(tagOf(html, 'readOnly'), /\bchecked\b/);
  assert.doesNotMatch(tagOf(html, 'trustServerCertificate'), /\bchecked\b/);
  assert.match(tagOf(render(defaultFormValues()), 'insights'), /\bchecked\b/);
  assert.match(html, /<option value="strict" selected>/);
  assert.match(html, /<option value="entraDefault" selected>/);
});

test('ddlHistory: carried from the form to the profile, off by default', () => {
  assert.equal(formToProfile(filled({ ddlHistory: true }), ctx()).profile.ddlHistory, true);
  assert.equal(formToProfile(filled(), ctx()).profile.ddlHistory, false);
  assert.equal(formToProfile(filled({ auth: 'raw', rawConnectionString: 'Server=x', ddlHistory: true }), ctx()).profile.ddlHistory, true);
});

test('html: the DDL history checkbox is off by default, shown for every auth, with its help text', () => {
  const html = render(defaultFormValues());
  assert.match(tagOf(html, 'ddlHistory'), /type="checkbox"/);
  assert.doesNotMatch(tagOf(html, 'ddlHistory'), /\bchecked\b/);
  assert.match(html, /DDL history \(audit trigger\)/);
  // The help text is HTML-escaped: the apostrophe appears only as &#39;.
  assert.ok(html.includes('Records every schema change in dbo.DDL_AuditLog via the DDL_Audit database trigger, so you can diff an object&#39;s history. If they are missing, they are created on read-write connections (you are asked first).'));
  assert.ok(!html.includes("diff an object's history"));
  assert.match(tagOf(render({ ...defaultFormValues(), ddlHistory: true }), 'ddlHistory'), /\bchecked\b/);
  const raw = render({ ...defaultFormValues(), auth: 'raw' });
  assert.doesNotMatch(wrapperClass(raw, 'flags'), /\bhidden\b/);
  assert.match(raw, /id="ddlHistory"/);
  assert.match(html, /ddlHistory: el\('ddlHistory'\)\.checked/);
});
