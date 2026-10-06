// Connections view (MCP Apps): list, add, edit, test and remove managed connections.
// Server data is only ever written with textContent / value, never innerHTML.
import { App, applyDocumentTheme, applyHostStyleVariables } from '@modelcontextprotocol/ext-apps/app-with-deps';
import {
  AUTH_OPTIONS, ENCRYPTION_HELP, ENCRYPTION_OPTIONS, TRUST_HELP,
  changeNote, defaultInput, describeTarget, isShown, parseToolResult, toServerInput, viewToInput,
  type AuthKind, type EncryptKind, type FieldKey, type FormInput, type ListResult, type ManagedView, type ProbeResult, type SaveResult,
} from './model';

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;

const app = new App({ name: 'apoint-ms-sql-connections', version: '1.0.0' }, {}, { autoResize: true });

let current: ListResult = { managed: [], others: [] };
let editing: ManagedView | undefined;
let busy = false;

// ---- host theme ----
function applyHost(ctx: ReturnType<App['getHostContext']>) {
  if (ctx?.theme) applyDocumentTheme(ctx.theme);
  if (ctx?.styles?.variables) applyHostStyleVariables(ctx.styles.variables);
}
app.onhostcontextchanged = applyHost;

// ---- tool calls ----
async function call<T>(name: string, args: Record<string, unknown>): Promise<T> {
  const result = await app.callServerTool({ name, arguments: args });
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
  for (const id of ['save', 'test', 'listDbs', 'cancel', 'delete']) $<HTMLButtonElement>(id).disabled = b;
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
      const list = $('dblist');
      list.replaceChildren(...r.databases.map(d => new Option(d, d)));
      setStatus(`${r.databases.length} databases - pick one in the Database field.`, 'ok');
    }
  } catch (err) {
    setStatus(message(err), 'err');
  } finally {
    setBusy(false);
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
