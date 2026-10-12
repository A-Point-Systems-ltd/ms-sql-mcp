// HTML of the SQL Editor Options form. No 'vscode' import (unit-testable).

import { escapeHtml } from '../webviewUtil';
import { EditorOptionValues, KEYWORD_CASES, MAX_ITEMS_PER_ROW, MAX_SHORTCUT_LENGTH, NAME_MATCH_OPTIONS } from './editorOptionsModel';

export interface OptionsRenderOptions {
  nonce: string;
  /** A folder is open: the form offers to save to the workspace settings too. */
  hasWorkspace: boolean;
}

export function renderEditorOptions(v: EditorOptionValues, opts: OptionsRenderOptions): string {
  const e = escapeHtml;
  const check = (id: 'enhanced' | 'tableAliases' | 'formatFiles', label: string, hint: string): string =>
    `<label class="check"><input type="checkbox" id="${id}"${v[id] ? ' checked' : ''}> <span>${label}</span></label>
      <div class="hint indent">${hint}</div>`;
  const matchOptions = NAME_MATCH_OPTIONS.map(o => `<option value="${o.value}"${o.value === v.joinNameMatch ? ' selected' : ''}>${e(o.label)}</option>`).join('');
  const caseOptions = KEYWORD_CASES.map(c => `<option value="${c}"${c === v.keywordCase ? ' selected' : ''}>${c}</option>`).join('');
  const shortcutRows = v.shortcuts.map((sql, slot) => `<tr>
      <th scope="row"><label for="sc${slot}">Ctrl+${slot}</label></th>
      <td><input type="text" id="sc${slot}" data-slot="${slot}" value="${e(sql)}" maxlength="${MAX_SHORTCUT_LENGTH}" spellcheck="false" placeholder="(not set: Ctrl+${slot} keeps its VS Code action)"></td>
    </tr>`).join('');
  const scope = opts.hasWorkspace
    ? `<label for="scope">Save to</label>
       <select id="scope"><option value="user" selected>User settings (all workspaces)</option><option value="workspace">Workspace settings (this folder only)</option></select>`
    : '<input type="hidden" id="scope" value="user">';

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${opts.nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SQL Editor Options</title>
<style>
  body{margin:0;padding:16px 24px;font-family:var(--vscode-font-family);font-size:var(--vscode-font-size);
       color:var(--vscode-foreground);background:var(--vscode-editor-background)}
  h1{font-size:1.4em;font-weight:600;margin:0 0 16px}
  h2{font-size:1.1em;font-weight:600;margin:22px 0 10px;padding-bottom:4px;border-bottom:1px solid var(--vscode-panel-border)}
  form{max-width:760px}
  .field{margin-bottom:14px}
  label{display:block;margin-bottom:4px}
  .check{display:flex;align-items:center;gap:6px;margin:0}
  .check input{margin:0}
  input[type=text],input[type=number],select{box-sizing:border-box;width:100%;padding:5px 8px;
       color:var(--vscode-input-foreground);background:var(--vscode-input-background);
       border:1px solid var(--vscode-input-border,var(--vscode-panel-border));border-radius:2px;
       font-family:inherit;font-size:inherit;outline:none}
  input[type=number]{width:90px}
  select{color:var(--vscode-dropdown-foreground);background:var(--vscode-dropdown-background);
         border-color:var(--vscode-dropdown-border,var(--vscode-panel-border))}
  input:focus,select:focus{border-color:var(--vscode-focusBorder)}
  table{width:100%;border-collapse:collapse}
  th{text-align:left;font-weight:normal;white-space:nowrap;padding:3px 10px 3px 0;width:1%}
  th label{margin:0}
  td{padding:3px 0}
  td input{font-family:var(--vscode-editor-font-family),monospace}
  code{font-family:var(--vscode-editor-font-family),monospace}
  .hint{margin-top:4px;color:var(--vscode-descriptionForeground);font-size:.9em}
  .indent{margin:2px 0 12px 22px}
  button{padding:5px 14px;border:1px solid var(--vscode-button-border,transparent);border-radius:2px;cursor:pointer;
         font-family:inherit;font-size:inherit;color:var(--vscode-button-secondaryForeground);
         background:var(--vscode-button-secondaryBackground)}
  button:hover{background:var(--vscode-button-secondaryHoverBackground)}
  button.primary{color:var(--vscode-button-foreground);background:var(--vscode-button-background)}
  button.primary:hover{background:var(--vscode-button-hoverBackground)}
  .link{border:none;background:none;padding:0;color:var(--vscode-textLink-foreground);cursor:pointer}
  .link:hover{background:none;text-decoration:underline}
  .actions{display:flex;gap:8px;margin-top:20px;flex-wrap:wrap;align-items:center}
  .result{margin-top:14px;padding:8px 10px;border-radius:2px;border:1px solid var(--vscode-panel-border)}
  .result.fail{border-color:var(--vscode-errorForeground);color:var(--vscode-errorForeground)}
  .result:empty{display:none}
</style>
</head>
<body>
<h1>SQL Editor Options</h1>
<form id="form" novalidate autocomplete="off">
  <h2>Completions</h2>
  <div class="field">
    ${check('enhanced', 'Enhanced completions', 'Whole JOIN clauses and ON conditions, table aliases, the column picker, * expansion and snippets.')}
    ${check('tableAliases', 'Table aliases', 'Add a generated alias (Orders o) to tables picked after FROM or JOIN and in JOIN suggestions.')}
  </div>
  <div class="field">
    <label for="joinNameMatch">JOIN suggestions from same-named columns (when no foreign key connects the tables)</label>
    <select id="joinNameMatch">${matchOptions}</select>
    <div class="hint">Generic names (Id, Name, Code, ...) and IDENTITY columns never match; foreign keys always come first.</div>
  </div>

  <h2>Formatting (Ctrl+F2)</h2>
  <div class="field">
    <label for="keywordCase">Keyword case</label>
    <select id="keywordCase">${caseOptions}</select>
  </div>
  <div class="field">
    <label for="maxItemsPerRow">Most columns or values per row</label>
    <input type="number" id="maxItemsPerRow" min="${MAX_ITEMS_PER_ROW.min}" max="${MAX_ITEMS_PER_ROW.max}" step="1" value="${v.maxItemsPerRow}">
  </div>
  <div class="field">
    ${check('formatFiles', 'Format .sql files too', 'Format Document and format-on-save also in .sql files and untitled SQL editors (Ctrl+F2 works everywhere either way).')}
  </div>

  <h2>Query shortcuts</h2>
  <div class="hint" style="margin:-4px 0 10px">The SQL runs on the editor's connection and shows its results in the Results panel; the editor text is not changed.
    <code>{selection}</code> is replaced by the selected text, or the name under the cursor when nothing is selected.</div>
  <table>${shortcutRows}</table>

  <div class="field" style="margin-top:18px">${scope}</div>
  <div class="actions">
    <button type="submit" class="primary" id="save">Save</button>
    <button type="button" id="cancel">Close</button>
    <button type="button" class="link" id="openSettingsJson">Open settings.json</button>
  </div>
  <div class="result" id="result" role="status" aria-live="polite"></div>
</form>
<script nonce="${opts.nonce}">
  (function () {
    var vscode = acquireVsCodeApi();
    function el(id) { return document.getElementById(id); }
    function values() {
      var shortcuts = [];
      for (var i = 0; i < 10; i++) shortcuts.push(el('sc' + i).value);
      return {
        enhanced: el('enhanced').checked, tableAliases: el('tableAliases').checked, joinNameMatch: el('joinNameMatch').value,
        keywordCase: el('keywordCase').value, maxItemsPerRow: Number(el('maxItemsPerRow').value),
        formatFiles: el('formatFiles').checked, shortcuts: shortcuts
      };
    }
    function show(ok, text) {
      var r = el('result');
      r.className = 'result' + (ok ? '' : ' fail');
      r.textContent = text;
    }
    el('form').addEventListener('submit', function (ev) {
      ev.preventDefault();
      var v = values();
      if (!Number.isInteger(v.maxItemsPerRow) || v.maxItemsPerRow < ${MAX_ITEMS_PER_ROW.min} || v.maxItemsPerRow > ${MAX_ITEMS_PER_ROW.max}) {
        show(false, 'Columns per row must be a whole number from ${MAX_ITEMS_PER_ROW.min} to ${MAX_ITEMS_PER_ROW.max}.');
        return;
      }
      vscode.postMessage({ type: 'save', values: v, scope: el('scope').value });
    });
    el('cancel').addEventListener('click', function () { vscode.postMessage({ type: 'cancel' }); });
    el('openSettingsJson').addEventListener('click', function () { vscode.postMessage({ type: 'openSettingsJson' }); });
    window.addEventListener('message', function (ev) {
      var m = ev.data || {};
      if (m.type === 'saved') show(!!m.ok, String(m.text));
    });
  })();
</script>
</body>
</html>`;
}
