// HTML of the add / edit connection form. No 'vscode' import (unit-testable).

import { escapeHtml } from '../webviewUtil';
import { AUTH_OPTIONS, COLOR_HELP, COLOR_OPTIONS, DDL_HISTORY_HELP, DDL_HISTORY_LABEL, ENCRYPTION_HELP, ENCRYPTION_OPTIONS, FormValues, TRUST_HELP } from './connectionFormModel';

export interface RenderOptions {
  mode: 'add' | 'edit';
  hasSavedPassword: boolean;
  nonce: string;
  /** Unused: the page loads no external resources, so the CSP names none (see the webview constraints). */
  cspSource?: string;
}

/** Auth kinds for which a field is shown; a field not listed here is always shown. */
const SERVER_AUTH = 'windows sql entraInteractive entraDefault';
const SHOWN_FOR = {
  server: SERVER_AUTH,
  database: SERVER_AUTH,
  user: 'sql entraInteractive',
  password: 'sql',
  rawConnectionString: 'raw',
  encrypt: SERVER_AUTH,
} as const;

export function renderConnectionForm(v: FormValues, opts: RenderOptions): string {
  const e = escapeHtml;
  const edit = opts.mode === 'edit';

  /** Wrapper for a field that depends on the authentication type. */
  const field = (key: keyof typeof SHOWN_FOR, label: string, body: string, hint = ''): string => {
    const shown = SHOWN_FOR[key];
    const hidden = shown.split(' ').includes(v.auth) ? '' : ' hidden';
    return `<div class="field${hidden}" data-field="${key}" data-auth="${shown}">
      <label for="${key}">${label}</label>
      ${body}
      ${hint ? `<div class="hint">${hint}</div>` : ''}
      <div class="err" data-err="${key}"></div>
    </div>`;
  };
  const plain = (key: string, label: string, body: string, hint = ''): string => `<div class="field" data-field="${key}">
      <label for="${key}">${label}</label>
      ${body}
      ${hint ? `<div class="hint">${hint}</div>` : ''}
      <div class="err" data-err="${key}"></div>
    </div>`;
  const check = (key: 'readOnly' | 'insights' | 'open' | 'trustServerCertificate' | 'ddlHistory', label: string, hint: string): string =>
    `<label class="check"><input type="checkbox" id="${key}"${v[key] ? ' checked' : ''}> <span>${label}</span></label>
      <div class="hint indent">${hint}</div>`;

  const authOptions = AUTH_OPTIONS.map(a => `<option value="${a.kind}"${a.kind === v.auth ? ' selected' : ''}>${e(a.label)}</option>`).join('');
  const encOptions = ENCRYPTION_OPTIONS.map(o => `<option value="${o.value}"${o.value === v.encrypt ? ' selected' : ''}>${e(o.label)}</option>`).join('');
  const encHelp = ENCRYPTION_OPTIONS
    .map(o => `<div class="hint${o.value === v.encrypt ? '' : ' hidden'}" data-enc="${o.value}">${e(ENCRYPTION_HELP[o.value])}</div>`)
    .join('');

  const colorOptions = COLOR_OPTIONS.map(o => `<option value="${o.value}"${o.value === v.color ? ' selected' : ''}>${e(o.label)}</option>`).join('');

  const title = edit ? `Edit connection '${v.name}'` : 'Add connection';
  const passwordPlaceholder = edit && opts.hasSavedPassword ? 'Saved - leave empty to keep' : '';

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${opts.nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${e(title)}</title>
<style>
  body{margin:0;padding:16px 24px;font-family:var(--vscode-font-family);font-size:var(--vscode-font-size);
       color:var(--vscode-foreground);background:var(--vscode-editor-background)}
  h1{font-size:1.4em;font-weight:600;margin:0 0 16px}
  form{max-width:640px}
  .field{margin-bottom:14px}
  .hidden{display:none}
  label{display:block;margin-bottom:4px}
  .check{display:flex;align-items:center;gap:6px;margin:0}
  .check input{margin:0}
  input[type=text],input[type=password],select,textarea{box-sizing:border-box;width:100%;padding:5px 8px;
       color:var(--vscode-input-foreground);background:var(--vscode-input-background);
       border:1px solid var(--vscode-input-border,var(--vscode-panel-border));border-radius:2px;
       font-family:inherit;font-size:inherit;outline:none}
  select{color:var(--vscode-dropdown-foreground);background:var(--vscode-dropdown-background);
         border-color:var(--vscode-dropdown-border,var(--vscode-panel-border))}
  textarea{min-height:90px;resize:vertical;font-family:var(--vscode-editor-font-family),monospace}
  input:focus,select:focus,textarea:focus{border-color:var(--vscode-focusBorder)}
  input[readonly]{opacity:.7}
  .row{display:flex;gap:8px;align-items:center}
  .row input,.row select{flex:1}
  .swatch{flex:0 0 auto;width:14px;height:14px;border-radius:50%;border:1px solid var(--vscode-panel-border)}
  .hint{margin-top:4px;color:var(--vscode-descriptionForeground);font-size:.9em}
  .indent{margin:2px 0 12px 22px}
  .err{margin-top:4px;color:var(--vscode-errorForeground);font-size:.9em}
  .err:empty{display:none}
  button{padding:5px 14px;border:1px solid var(--vscode-button-border,transparent);border-radius:2px;cursor:pointer;
         font-family:inherit;font-size:inherit;color:var(--vscode-button-secondaryForeground);
         background:var(--vscode-button-secondaryBackground)}
  button:hover:not(:disabled){background:var(--vscode-button-secondaryHoverBackground)}
  button.primary{color:var(--vscode-button-foreground);background:var(--vscode-button-background)}
  button.primary:hover:not(:disabled){background:var(--vscode-button-hoverBackground)}
  button:disabled{opacity:.5;cursor:default}
  .actions{display:flex;gap:8px;margin-top:20px;flex-wrap:wrap}
  .result{margin-top:14px;padding:8px 10px;border-radius:2px;white-space:pre-wrap;word-break:break-word;
          border:1px solid var(--vscode-panel-border)}
  .result.ok{border-color:var(--vscode-testing-iconPassed,var(--vscode-panel-border))}
  .result.fail{border-color:var(--vscode-errorForeground);color:var(--vscode-errorForeground)}
  .result:empty{display:none}
</style>
</head>
<body>
<h1>${e(title)}</h1>
<form id="form" novalidate autocomplete="off">
  ${plain('name', 'Name', `<input type="text" id="name" value="${e(v.name)}"${edit ? ' readonly' : ''} spellcheck="false">`,
    edit ? 'The name cannot be changed. Remove and re-add the connection to rename it.' : 'Letters, digits, "-", "_" and ".". Agents use it to pick the connection.')}
  ${plain('auth', 'Authentication', `<select id="auth">${authOptions}</select>`)}
  ${field('server', 'Server', `<input type="text" id="server" value="${e(v.server)}" spellcheck="false">`, 'host, host\\instance or host,port')}
  ${field('database', 'Database', `<div class="row"><input type="text" id="database" list="dblist" value="${e(v.database)}" spellcheck="false"><button type="button" id="listDatabases">List databases</button></div><datalist id="dblist"></datalist>`)}
  ${field('user', 'User', `<input type="text" id="user" value="${e(v.user)}" spellcheck="false">`)}
  ${field('password', 'Password', `<input type="password" id="password" autocomplete="new-password" placeholder="${e(passwordPlaceholder)}">`, 'Kept in VS Code secure storage, never in the connection list.')}
  ${field('rawConnectionString', 'Connection string', `<textarea id="rawConnectionString" spellcheck="false">${e(v.rawConnectionString)}</textarea>`, 'Must not contain a password; it is stored unencrypted.')}
  <div class="field${SHOWN_FOR.encrypt.split(' ').includes(v.auth) ? '' : ' hidden'}" data-field="encrypt" data-auth="${SHOWN_FOR.encrypt}">
    <label for="encrypt">Encryption</label>
    <select id="encrypt">${encOptions}</select>
    ${encHelp}
    <div style="margin-top:8px">${check('trustServerCertificate', 'Trust server certificate', e(TRUST_HELP))}</div>
  </div>
  ${plain('color', 'Color', `<div class="row"><span id="swatch" class="swatch"></span><select id="color">${colorOptions}</select></div>`, e(COLOR_HELP))}
  <div class="field" data-field="flags">
    ${check('readOnly', 'Read-only', 'Refuses write tools and connects with ApplicationIntent=ReadOnly.')}
    ${check('insights', 'AI Insights', 'Adds schema-insight tools for agents (also requires the msSqlMcp.insights setting).')}
    ${check('open', 'Open (expose to agents)', 'Closed connections stay in the list but agents and the explorer cannot use them.')}
    ${check('ddlHistory', e(DDL_HISTORY_LABEL), e(DDL_HISTORY_HELP))}
  </div>
  <div class="actions">
    <button type="submit" class="primary" id="save">Save</button>
    <button type="button" id="test">Test connection</button>
    <button type="button" id="cancel">Cancel</button>
  </div>
  <div class="result" id="result" role="status" aria-live="polite"></div>
</form>
<script nonce="${opts.nonce}">
  (function () {
    var vscode = acquireVsCodeApi();
    function el(id) { return document.getElementById(id); }
    function each(list, fn) { Array.prototype.forEach.call(list, fn); }
    var buttons = ['save', 'test', 'cancel', 'listDatabases'];

    function values() {
      return {
        name: el('name').value, auth: el('auth').value, server: el('server').value, database: el('database').value,
        user: el('user').value, password: el('password').value, rawConnectionString: el('rawConnectionString').value,
        encrypt: el('encrypt').value, trustServerCertificate: el('trustServerCertificate').checked,
        readOnly: el('readOnly').checked, insights: el('insights').checked, open: el('open').checked,
        ddlHistory: el('ddlHistory').checked, color: el('color').value
      };
    }
    function applyAuth() {
      var auth = el('auth').value;
      each(document.querySelectorAll('[data-auth]'), function (node) {
        node.classList.toggle('hidden', node.getAttribute('data-auth').split(' ').indexOf(auth) === -1);
      });
    }
    function applyEncryption() {
      var enc = el('encrypt').value;
      each(document.querySelectorAll('[data-enc]'), function (node) {
        node.classList.toggle('hidden', node.getAttribute('data-enc') !== enc);
      });
    }
    function clearErrors() { each(document.querySelectorAll('[data-err]'), function (n) { n.textContent = ''; }); }
    function showResult(ok, text) {
      var r = el('result');
      r.className = 'result ' + (ok ? 'ok' : 'fail');
      r.textContent = text;
    }
    function send(type) {
      clearErrors();
      el('result').textContent = '';
      vscode.postMessage({ type: type, values: values() });
    }

    el('auth').addEventListener('change', applyAuth);
    el('encrypt').addEventListener('change', applyEncryption);
    function applyColor() {
      var c = el('color').value;
      el('swatch').style.background = c ? 'var(--vscode-charts-' + c + ')' : 'transparent';
    }
    el('color').addEventListener('change', applyColor);
    el('form').addEventListener('submit', function (ev) { ev.preventDefault(); send('save'); });
    el('test').addEventListener('click', function () { send('test'); });
    el('listDatabases').addEventListener('click', function () { send('listDatabases'); });
    el('cancel').addEventListener('click', function () { vscode.postMessage({ type: 'cancel' }); });
    each(document.querySelectorAll('input, select, textarea'), function (node) {
      node.addEventListener('input', function () {
        var err = document.querySelector('[data-err="' + node.id + '"]');
        if (err) err.textContent = '';
      });
    });

    window.addEventListener('message', function (ev) {
      var m = ev.data || {};
      if (m.type === 'busy') {
        buttons.forEach(function (id) { if (id !== 'cancel') el(id).disabled = !!m.busy; });
        if (m.busy) showResult(true, 'Working...');
      } else if (m.type === 'testResult') {
        showResult(!!m.ok, String(m.text));
      } else if (m.type === 'databases') {
        var list = el('dblist');
        while (list.firstChild) list.removeChild(list.firstChild);
        (m.names || []).forEach(function (n) {
          var o = document.createElement('option');
          o.value = String(n);
          list.appendChild(o);
        });
        showResult(true, (m.names || []).length + ' database(s) found. Pick one from the Database field.');
      } else if (m.type === 'errors') {
        el('result').textContent = '';
        var errs = m.errors || {};
        Object.keys(errs).forEach(function (k) {
          var target = document.querySelector('[data-err="' + k + '"]');
          if (target) target.textContent = String(errs[k]);
        });
      }
    });

    applyAuth();
    applyEncryption();
    applyColor();
  })();
</script>
</body>
</html>`;
}
