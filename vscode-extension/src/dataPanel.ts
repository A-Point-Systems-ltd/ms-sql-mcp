import * as vscode from 'vscode';
import { DataTable } from './dataTable';

let panel: vscode.WebviewPanel | undefined;

/** Show query/table results in a reusable, read-only webview grid. */
export function showDataPreview(objectName: string, connection: string, table: DataTable): void {
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
  panel.webview.html = renderHtml(panel.webview, objectName, table);
  panel.reveal(panel.viewColumn ?? vscode.ViewColumn.Active);
}

export function disposeDataPanel(): void {
  panel?.dispose();
  panel = undefined;
}

function renderHtml(webview: vscode.Webview, objectName: string, table: DataTable): string {
  const nonce = makeNonce();
  const { columns, rows, truncated, limit } = table;

  const head = columns.map((c) => `<th>${escapeHtml(c)}</th>`).join('');
  const body = rows
    .map((row) => {
      const cells = columns
        .map((col) => {
          const value = row[col];
          if (value === null || value === undefined) {
            return '<td class="null">NULL</td>';
          }
          const numeric = typeof value === 'number' || typeof value === 'bigint';
          return `<td class="${numeric ? 'num' : ''}">${escapeHtml(String(value))}</td>`;
        })
        .join('');
      return `<tr>${cells}</tr>`;
    })
    .join('');

  const countLabel = truncated
    ? `first ${rows.length.toLocaleString()} rows (limit ${limit.toLocaleString()})`
    : `${rows.length.toLocaleString()} row${rows.length === 1 ? '' : 's'}`;

  const empty = rows.length === 0 ? '<p class="empty">No rows returned.</p>' : '';

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
  .empty{padding:18px 14px;color:var(--vscode-descriptionForeground)}
  .hidden{display:none}
</style>
</head>
<body>
  <div class="bar">
    <span class="name">${escapeHtml(objectName)}</span>
    <span class="meta">${escapeHtml(countLabel)} · ${columns.length} column${columns.length === 1 ? '' : 's'} · read-only</span>
    <input id="filter" type="text" placeholder="Filter rows…" aria-label="Filter rows">
    <span class="meta" id="shown"></span>
  </div>
  <div class="scroll">
    <table>
      <thead><tr>${head}</tr></thead>
      <tbody id="body">${body}</tbody>
    </table>
  </div>
  ${empty}
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

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function makeNonce(): string {
  let nonce = '';
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) {
    nonce += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return nonce;
}
