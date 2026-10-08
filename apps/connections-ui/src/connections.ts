// Connections view (MCP Apps): list, add, edit, test and remove managed connections.
// Server data is only ever written with textContent / value, never innerHTML.
import { App, applyDocumentTheme, applyHostStyleVariables } from '@modelcontextprotocol/ext-apps/app-with-deps';
import {
  AUTH_OPTIONS, ENCRYPTION_HELP, ENCRYPTION_OPTIONS, TRUST_HELP,
  canBringOnline, changeNote, databaseLabel, defaultInput, describeDatabaseList, describeTarget, isOnline, isShown, parseToolResult,
  toServerInput, viewToInput,
  type AuthKind, type EncryptKind, type FieldKey, type FormInput, type ListResult, type ManagedView, type ProbeResult, type SaveResult,
} from './model';

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;

const app = new App({ name: 'apoint-ms-sql-connections', version: '1.0.0' }, {}, { autoResize: true });

let current: ListResult = { managed: [], others: [] };
let editing: ManagedView | undefined;
let busy = false;
/** Lower-cased database name -> state_desc, from List databases / Test / Bring online. */
let dbStates = new Map<string, string>();
/** A Save of a new connection stopped because its database is OFFLINE: Bring online then saves, or Save anyway. */
let pendingSave = false;
/** The server allows 300 s for SET ONLINE (crash recovery); the view waits a little longer. */
const BRING_ONLINE_TIMEOUT_MS = 330_000;
/** The optional OFFLINE check before saving a new connection must not hold Save for long (e.g. off VPN). */
const PRECHECK_TIMEOUT_MS = 10_000;

// ---- host theme ----
function applyHost(ctx: ReturnType<App['getHostContext']>) {
  if (ctx?.theme) applyDocumentTheme(ctx.theme);
  if (ctx?.styles?.variables) applyHostStyleVariables(ctx.styles.variables);
}
app.onhostcontextchanged = applyHost;

// ---- tool calls ----
/** `timeout` (ms) overrides the SDK's 60 s request default; on expiry the request is cancelled on the server too. */
async function call<T>(name: string, args: Record<string, unknown>, opts: { timeout?: number } = {}): Promise<T> {
  const result = await app.callServerTool({ name, arguments: args }, opts.timeout ? { timeout: opts.timeout } : undefined);
  return parseToolResult<T>(result);
}

async function tellModel(text: string) {
  try {
    await app.updateModelContext({ content: [{ type: 'text', text }] });
  } catch {
    // Optional host feature: the change is applied either way.
  }
}

// ---- list view ----
async function refresh() {
  try {
    current = await call<ListResult>('connections_ui_list', {});
  } catch (err) {
    current = { managed: [], others: [], fileError: message(err) };
  }
  renderList();
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function authLabel(kind: string): string {
  return AUTH_OPTIONS.find(a => a.kind === kind)?.label ?? kind;
}

function renderList() {
  const banner = $('fileError');
  banner.textContent = current.fileError ?? '';
  banner.classList.toggle('hidden', !current.fileError);

  const list = $('managed');
  list.replaceChildren();
  if (current.managed.length === 0) {
    list.append(el('div', 'empty', 'No saved connections yet. Click "Add connection".'));
  }
  for (const c of current.managed) {
    const row = el('div', 'row');
    const main = el('div', 'main');
    const name = el('div', 'name', c.name);
    name.append(el('span', c.readOnly ? 'badge' : 'badge rw', c.readOnly ? 'read-only' : 'read-write'));
    if (!c.isOpen && !c.error) name.append(el('span', 'badge', 'closed'));
    main.append(name, el('div', 'target', `${describeTarget(c)} · ${authLabel(c.auth)}`));
    if (c.error) main.append(el('div', 'err', c.error));
    const edit = el('button', '', 'Edit');
    edit.type = 'button';
    edit.addEventListener('click', () => openForm(c));
    row.append(main, edit);
    list.append(row);
  }

  const others = $('others');
  others.replaceChildren();
  $('othersWrap').classList.toggle('hidden', current.others.length === 0);
  for (const o of current.others) {
    const row = el('div', 'row other');
    const main = el('div', 'main');
    const name = el('div', 'name', o.name);
    name.append(el('span', 'badge', o.readOnly ? 'read-only' : 'read-write'));
    main.append(name, el('div', 'target', `${o.dataSource} / ${o.database ?? '?'} · ${o.source === 'Configured' ? 'VS Code extension / config file' : o.source}`));
    row.append(main);
    others.append(row);
  }
}

// ---- form view ----
const fieldKeys: FieldKey[] = ['server', 'database', 'user', 'password', 'rawConnectionString', 'encrypt', 'trustServerCertificate'];

function fillSelects() {
  const auth = $<HTMLSelectElement>('f-auth');
  for (const a of AUTH_OPTIONS) auth.append(new Option(a.label, a.kind));
  const enc = $<HTMLSelectElement>('f-encrypt');
  for (const o of ENCRYPTION_OPTIONS) enc.append(new Option(o.label, o.value));
  $('trustHint').textContent = TRUST_HELP;
}

function readForm(): FormInput {
  return {
    name: $<HTMLInputElement>('f-name').value,
    auth: $<HTMLSelectElement>('f-auth').value as AuthKind,
    server: $<HTMLInputElement>('f-server').value,
    database: $<HTMLInputElement>('f-database').value,
    user: $<HTMLInputElement>('f-user').value,
    password: $<HTMLInputElement>('f-password').value,
    rawConnectionString: $<HTMLTextAreaElement>('f-raw').value,
    encrypt: $<HTMLSelectElement>('f-encrypt').value as EncryptKind,
    trustServerCertificate: $<HTMLInputElement>('f-trust').checked,
    readOnly: $<HTMLInputElement>('f-readOnly').checked,
    insights: $<HTMLInputElement>('f-insights').checked,
  };
}

function writeForm(f: FormInput) {
  $<HTMLInputElement>('f-name').value = f.name;
  $<HTMLSelectElement>('f-auth').value = f.auth;
  $<HTMLInputElement>('f-server').value = f.server;
  $<HTMLInputElement>('f-database').value = f.database;
  $<HTMLInputElement>('f-user').value = f.user;
  $<HTMLInputElement>('f-password').value = '';
  $<HTMLTextAreaElement>('f-raw').value = f.rawConnectionString;
  $<HTMLSelectElement>('f-encrypt').value = f.encrypt;
  $<HTMLInputElement>('f-trust').checked = f.trustServerCertificate;
  $<HTMLInputElement>('f-readOnly').checked = f.readOnly;
  $<HTMLInputElement>('f-insights').checked = f.insights;
}

function syncVisibility() {
  const auth = $<HTMLSelectElement>('f-auth').value as AuthKind;
  for (const key of fieldKeys) {
    for (const node of document.querySelectorAll<HTMLElement>(`[data-field="${key}"]`)) {
      node.classList.toggle('hidden', !isShown(key, auth));
    }
  }
  $('encHint').textContent = ENCRYPTION_HELP[$<HTMLSelectElement>('f-encrypt').value as EncryptKind] ?? '';
}

function showErrors(errors: Record<string, string>) {
  for (const node of document.querySelectorAll<HTMLElement>('[data-err]')) {
    node.textContent = errors[node.dataset.err!] ?? '';
  }
}

function setStatus(text: string, kind: 'ok' | 'err' | '' = '') {
  const s = $('status');
  s.textContent = text;
  s.className = kind;
}

function setBusy(b: boolean, text = 'Working...') {
  busy = b;
  for (const id of ['save', 'test', 'listDbs', 'cancel', 'delete', 'bringOnline', 'saveAnyway']) $<HTMLButtonElement>(id).disabled = b;
  if (b) setStatus(text);
}

function openForm(c?: ManagedView) {
  editing = c;
  writeForm(c ? viewToInput(c) : defaultInput());
  const name = $<HTMLInputElement>('f-name');
  name.readOnly = !!c;
  $('formTitle').textContent = c ? `Edit ${c.name}` : 'Add connection';
  const pw = $<HTMLInputElement>('f-password');
  pw.placeholder = c?.hasPassword ? 'Saved - leave empty to keep' : '';
  const del = $('delete');
  del.classList.toggle('hidden', !c);
  del.textContent = 'Delete';
  delete del.dataset.confirm;
  $('dblist').replaceChildren();
  dbStates = new Map();
  pendingSave = false;
  applyDbState();
  showErrors({});
  setStatus(c?.error ?? '', c?.error ? 'err' : '');
  syncVisibility();
  $('listView').classList.add('hidden');
  $('formView').classList.remove('hidden');
  (c ? $('f-server') : name).focus();
}

function closeForm() {
  editing = undefined;
  $<HTMLInputElement>('f-password').value = '';
  $('formView').classList.add('hidden');
  $('listView').classList.remove('hidden');
}

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

async function save(ev: Event) {
  ev.preventDefault();
  if (busy) return;
  if (!editing && !pendingSave && (await databaseIsOffline())) {
    // Optional step: the banner offers "Bring online & save" or "Save anyway".
    pendingSave = true;
    applyDbState();
    setStatus(`Database ${formDatabase()} is OFFLINE. Bring it online, or save the connection anyway.`, 'err');
    return;
  }
  await doSave();
}

async function doSave() {
  if (busy) return;
  pendingSave = false;
  applyDbState();
  const input = toServerInput(readForm());
  const isNew = !editing;
  setBusy(true, 'Saving...');
  try {
    const r = await call<SaveResult>('connections_ui_save', { connection: input, isNew });
    showErrors(r.errors ?? {});
    if (!r.success) {
      setStatus(r.message ?? 'Fix the highlighted fields.', 'err');
      return;
    }
    await tellModel(changeNote(isNew ? 'added' : 'updated', input));
    closeForm();
    await refresh();
  } catch (err) {
    setStatus(message(err), 'err');
  } finally {
    setBusy(false);
  }
}

async function probe(tool: 'connections_ui_test' | 'connections_ui_list_databases') {
  if (busy) return;
  const input = toServerInput(readForm());
  setBusy(true, tool === 'connections_ui_test' ? 'Connecting...' : 'Listing databases...');
  try {
    const r = await call<ProbeResult>(tool, { connection: input, isNew: !editing });
    setStatus(r.message, r.success ? 'ok' : 'err');
    if (r.success && r.databases) {
      // Datalist options cannot be greyed out; the label carries the state ("Sales (offline)").
      $('dblist').replaceChildren(...r.databases.map(d => {
        const o = new Option(d.name, d.name);
        if (!isOnline(d.state)) o.label = databaseLabel(d);
        return o;
      }));
      dbStates = new Map(r.databases.map(d => [d.name.toLowerCase(), d.state]));
      setStatus(describeDatabaseList(r.databases), 'ok');
    }
    if (r.databaseState) dbStates.set(formDatabase().toLowerCase(), r.databaseState);
    applyDbState();
  } catch (err) {
    setStatus(message(err), 'err');
  } finally {
    setBusy(false);
  }
}

// ---- offline database ----
function formDatabase(): string {
  return $<HTMLInputElement>('f-database').value.trim();
}

/** Shows the state note under Database when the typed database is known not to be ONLINE. */
function applyDbState() {
  const name = formDatabase();
  const state = name ? dbStates.get(name.toLowerCase()) : undefined;
  const show = !!state && !isOnline(state);
  if (!show) pendingSave = false;
  $('dbState').classList.toggle('hidden', !show);
  const bring = $<HTMLButtonElement>('bringOnline');
  bring.classList.toggle('hidden', !show || !canBringOnline(state));
  delete bring.dataset.confirm;
  bring.textContent = pendingSave ? 'Bring online & save' : 'Bring online';
  $('saveAnyway').classList.toggle('hidden', !pendingSave);
  $('dbStateText').textContent = !show ? ''
    : canBringOnline(state)
      ? `Database ${name} is OFFLINE. Bringing it online runs ALTER DATABASE ... SET ONLINE on the server; if it was taken offline on purpose, applications can start using it again.`
      : `Database ${name} is ${state}. Only an OFFLINE database can be brought online here.`;
}

/** Before saving a new connection: is its database known (or now found) to be OFFLINE? Unknown counts as no. */
async function databaseIsOffline(): Promise<boolean> {
  const name = formDatabase();
  const auth = readForm().auth;
  // Entra interactive would open a sign-in window just to save: skip the optional check.
  if (!name || !isShown('database', auth) || auth === 'entraInteractive') return false;
  if (!dbStates.has(name.toLowerCase())) {
    setBusy(true, 'Checking the database...');
    try {
      const r = await call<ProbeResult>('connections_ui_list_databases', { connection: toServerInput(readForm()), isNew: true },
        { timeout: PRECHECK_TIMEOUT_MS });
      for (const d of r.databases ?? []) dbStates.set(d.name.toLowerCase(), d.state);
    } catch {
      // The check is optional: if master is unreachable, Save reports the real problem.
    } finally {
      setBusy(false);
    }
  }
  return canBringOnline(dbStates.get(name.toLowerCase()));
}

async function bringOnline() {
  if (busy) return;
  const bring = $<HTMLButtonElement>('bringOnline');
  const name = formDatabase();
  if (!bring.dataset.confirm) {
    // Two-step confirm in the view: sandboxed iframes may block window.confirm.
    bring.dataset.confirm = '1';
    const server = readForm().server.trim();
    bring.textContent = `Confirm: bring ${name} online${server ? ` on ${server}` : ''}`;
    return;
  }
  const thenSave = pendingSave;
  setBusy(true, `Bringing ${name} online...`);
  let ok = false;
  try {
    const r = await call<ProbeResult>('connections_ui_bring_online', { connection: toServerInput(readForm()), isNew: !editing },
      { timeout: BRING_ONLINE_TIMEOUT_MS });
    setStatus(r.message, r.success ? 'ok' : 'err');
    if (r.databaseState) dbStates.set(name.toLowerCase(), r.databaseState);
    ok = r.success;
  } catch (err) {
    // A timeout cancels the request, so the database may be in any state now: read it back and show it.
    const state = await currentState(name);
    setStatus(`${message(err)}${state ? ` The database is now ${state}.` : ' Its state could not be read; use List databases.'}`, 'err');
    if (state) dbStates.set(name.toLowerCase(), state);
  } finally {
    setBusy(false);
  }
  if (ok) await tellModel(`The user brought database ${name} online from the connection manager.`);
  applyDbState();
  if (ok && thenSave) await doSave();
}

/** The database's state from a fresh List databases, or undefined when it cannot be read. */
async function currentState(name: string): Promise<string | undefined> {
  try {
    const r = await call<ProbeResult>('connections_ui_list_databases', { connection: toServerInput(readForm()), isNew: !editing },
      { timeout: PRECHECK_TIMEOUT_MS });
    return r.databases?.find(d => d.name.toLowerCase() === name.toLowerCase())?.state;
  } catch {
    return undefined;
  }
}

async function remove() {
  if (busy || !editing) return;
  const del = $('delete');
  if (!del.dataset.confirm) {
    del.dataset.confirm = '1';
    del.textContent = `Really delete ${editing.name}?`;
    return;
  }
  const name = editing.name;
  setBusy(true, 'Deleting...');
  try {
    const r = await call<SaveResult>('connections_ui_remove', { name });
    if (!r.success) {
      setStatus(r.message ?? 'Delete failed.', 'err');
      return;
    }
    await tellModel(changeNote('removed', { name, readOnly: true }));
    closeForm();
    await refresh();
  } catch (err) {
    setStatus(message(err), 'err');
  } finally {
    setBusy(false);
  }
}

// ---- wiring ----
fillSelects();
$('add').addEventListener('click', () => openForm());
$('cancel').addEventListener('click', closeForm);
$('form').addEventListener('submit', save);
$('test').addEventListener('click', () => void probe('connections_ui_test'));
$('listDbs').addEventListener('click', () => void probe('connections_ui_list_databases'));
$('delete').addEventListener('click', () => void remove());
$('bringOnline').addEventListener('click', () => void bringOnline());
$('saveAnyway').addEventListener('click', () => void doSave());
$('f-database').addEventListener('input', () => { pendingSave = false; applyDbState(); });
// States belong to one server and login: forget them when either changes.
for (const id of ['f-auth', 'f-server', 'f-user', 'f-raw']) {
  for (const ev of ['input', 'change']) $(id).addEventListener(ev, () => { dbStates = new Map(); pendingSave = false; applyDbState(); });
}
$('f-auth').addEventListener('change', syncVisibility);
$('f-encrypt').addEventListener('change', syncVisibility);

// The opening tool's result is a summary for the model; the view loads the full list itself.
app.ontoolresult = () => void refresh();

// The bundle is an IIFE, which has no top-level await.
void (async () => {
  try {
    await app.connect();
    applyHost(app.getHostContext());
    await refresh();
  } catch (err) {
    current = { managed: [], others: [], fileError: `Cannot reach the server: ${message(err)}` };
    renderList();
  }
})();
