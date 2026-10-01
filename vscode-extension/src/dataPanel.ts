import * as vscode from 'vscode';
import { GridTable, rowCountLabel } from './dataTable';
import { escapeHtml, makeNonce } from './webviewUtil';

let panel: vscode.WebviewPanel | undefined;

/**
 * Show rows in a reusable, read-only webview grid. Local only: rows can hold client personal data,
 * so the panel never sends them anywhere (no export, no network - see the CSP).
 */
export function showDataPreview(objectName: string, connection: string, table: GridTable, truncated: boolean): void {
  if (!panel) {
    panel = vscode.window.createWebviewPanel(
      'msSqlMcp.dataView',
      `Data: ${objectName} (${connection})`,
      { viewColumn: vscode.ViewColumn.Active, preserveFocus: false },
      { enableScripts: true, retainContextWhenHidden: true },
    );
    panel.onDidDispose(() => {
      panel = undefined;
    });
  }
  panel.title = `Data: ${objectName} (${connection})`;
  panel.webview.html = renderHtml(objectName, table, truncated);
  panel.reveal(panel.viewColumn ?? vscode.ViewColumn.Active);
}

export function disposeDataPanel(): void {
  panel?.dispose();
  panel = undefined;
}

function renderHtml(objectName: string, table: GridTable, truncated: boolean): string {
  const nonce = makeNonce();
  const { columns, data } = table;

  const head = columns.map((c) => `<th>${escapeHtml(c)}</th>`).join('');
  const body = data
    .map((row) => {
      const cells = row
        .map((value) => {
          if (value === null || value === undefined) {
            return '<td class="null">NULL</td>';
          }
          const numeric = typeof value === 'number' || typeof value === 'bigint';
          const text = typeof value === 'object' ? JSON.stringify(value) : String(value);
          return `<td class="${numeric ? 'num' : ''}">${escapeHtml(text)}</td>`;
        })
        .join('');
      return `<tr>${cells}</tr>`;
    })
    .join('');

  const countLabel = rowCountLabel(data.length, truncated);
  // Zero rows: no header (read_data returns no column names without rows; they are in the DDL).
  const grid = data.length === 0 ? '' : `<table>
      <thead><tr>${head}</tr></thead>
      <tbody id="body">${body}</tbody>
    </table>`;
  const columnsLabel = data.length === 0 ? '' : ` · ${columns.length} column${columns.length === 1 ? '' : 's'}`;

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
  body{margin:0;padding:0;font-family:var(--vscode-font-family);font-size:var(--vscode-font-size);
       color:var(--vscode-foreground);background:var(--vscode-editor-background)}
  .bar{position:sticky;top:0;z-index:3;display:flex;gap:12px;align-items:center;flex-wrap:wrap;
       padding:10px 14px;background:var(--vscode-editor-background);
       border-bottom:1px solid var(--vscode-panel-border)}
  .name{font-weight:600}
  .meta{color:var(--vscode-descriptionForeground);font-size:12px}
  input{flex:1;min-width:160px;max-width:320px;padding:4px 8px;
        color:var(--vscode-input-foreground);background:var(--vscode-input-background);
        border:1px solid var(--vscode-input-border,var(--vscode-panel-border));border-radius:3px;
        font-family:inherit;font-size:12px;outline:none}
  input:focus{border-color:var(--vscode-focusBorder)}
  .scroll{overflow:auto;max-height:calc(100vh - 46px)}
  table{border-collapse:collapse;width:max-content;min-width:100%}
  th,td{padding:4px 10px;border-bottom:1px solid var(--vscode-panel-border);
        border-right:1px solid var(--vscode-panel-border);text-align:left;white-space:pre;
        font-family:var(--vscode-editor-font-family),monospace;font-size:12px;max-width:420px;
        overflow:hidden;text-overflow:ellipsis}
  th{position:sticky;top:0;z-index:2;background:var(--vscode-editorWidget-background,#2224);
     font-weight:600;font-family:var(--vscode-font-family)}
  tbody tr:nth-child(even){background:var(--vscode-list-hoverBackground)}
  tbody tr:hover{background:var(--vscode-list-activeSelectionBackground);
                 color:var(--vscode-list-activeSelectionForeground)}
  td.num{text-align:right;font-variant-numeric:tabular-nums}
  td.null{color:var(--vscode-descriptionForeground);font-style:italic}
  .hidden{display:none}
</style>
</head>
<body>
  <div class="bar">
    <span class="name">${escapeHtml(objectName)}</span>
    <span class="meta">${escapeHtml(countLabel)}${columnsLabel} · read-only</span>
    <input id="filter" type="text" placeholder="Filter rows…" aria-label="Filter rows">
    <span class="meta" id="shown"></span>
  </div>
  <div class="scroll">
    ${grid}
  </div>
<script nonce="${nonce}">
  (function () {
    var input = document.getElementById('filter');
    var rows = Array.prototype.slice.call(document.querySelectorAll('#body tr'));
    var shown = document.getElementById('shown');
    var total = rows.length;
    function apply() {
      var q = input.value.toLowerCase();
      var count = 0;
      for (var i = 0; i < rows.length; i++) {
        var match = q === '' || rows[i].textContent.toLowerCase().indexOf(q) !== -1;
        rows[i].classList.toggle('hidden', !match);
        if (match) count++;
      }
      shown.textContent = q === '' ? '' : count + ' of ' + total + ' match';
    }
    input.addEventListener('input', apply);
  })();
</script>
</body>
</html>`;
}
