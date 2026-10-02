// The data grid shared by Data View and the Results panel: grid HTML, its stylesheet and its client script, plus
// the Data View page. No 'vscode' import (unit-testable).
//
// Rows can hold client personal data: every value is HTML-escaped, nothing is loaded from the network, and the
// client script never builds HTML. It changes the page only through textContent, setAttribute and style (column
// widths are CSS variables, row heights inline heights, the local sort is the flex `order` of each row).
// A copy click posts only indexes; the extension resolves the value from its own copy of the result.
import { rowCountLabel } from '../dataTable';
import { escapeHtml } from '../webviewUtil';
import {
  GridColumn, MAX_TOP, MIN_TOP, SortDir, TRUNCATED_SUFFIX, cellText, compareGridValues, gridSortOrder, isNumericType,
  isSortableType, tooltipText,
} from './gridModel';

/** Column widths before the client script fits them (px), and the auto-fit limits it applies. */
export const DEFAULT_COLUMN_WIDTH = 120;
export const MIN_COLUMN_WIDTH = 40;
export const FIT_MAX_WIDTH = 600;
/** The first auto-fit (on load) looks at this many rows and stops at this width. */
export const INITIAL_FIT_ROWS = 200;
export const INITIAL_FIT_MAX_WIDTH = 300;
/** A row auto-fitted to its content grows to at most this height; taller cells scroll. */
export const ROW_FIT_MAX_HEIGHT = 400;

export interface GridSpec {
  /** Element id of the grid; letters, digits, '-' and '_' only. */
  id: string;
  columns: readonly GridColumn[];
  rows: readonly (readonly unknown[])[];
  /** 'local': header clicks sort the loaded rows in the webview. 'server': they post {type:'sort'} to re-query. */
  sortMode: 'local' | 'server';
  /** Server mode: the sort the rows were queried with (column index). */
  sort?: { col: number; dir: SortDir };
  /** Results panel: the result set index, sent with copy messages. */
  set?: number;
}

const SAFE_ID = /^[A-Za-z][\w-]*$/;

const COPY_ICON = '<svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" focusable="false">'
  + '<path fill="currentColor" d="M4 4V2.5A1.5 1.5 0 0 1 5.5 1h7A1.5 1.5 0 0 1 14 2.5v7a1.5 1.5 0 0 1-1.5 1.5H11v1.5'
  + 'A1.5 1.5 0 0 1 9.5 14h-7A1.5 1.5 0 0 1 1 12.5v-7A1.5 1.5 0 0 1 2.5 4H4Zm1 0h4.5A1.5 1.5 0 0 1 11 5.5V10h1.5a.5.5 0 0 0 '
  + '.5-.5v-7a.5.5 0 0 0-.5-.5h-7a.5.5 0 0 0-.5.5V4ZM2.5 5a.5.5 0 0 0-.5.5v7a.5.5 0 0 0 .5.5h7a.5.5 0 0 0 .5-.5v-7a.5.5 0 0 '
  + '0 0-.5-.5h-7Z"/></svg>';

/** One grid: a header row with sort / resize handles, a row-number gutter, and the hover copy button. */
export function renderGrid(spec: GridSpec): string {
  if (!SAFE_ID.test(spec.id)) throw new Error(`Invalid grid id: ${spec.id}`);
  const { id, columns, rows } = spec;
  const numeric = columns.map(c => isNumericType(c.type));

  const head = columns.map((c, i) => {
    const name = c.name || '(No column name)';
    const sortable = spec.sortMode === 'local' || isSortableType(c.type);
    const dir = spec.sort && spec.sort.col === i ? spec.sort.dir : '';
    const tip = `${name} (${c.type || 'unknown type'})${sortable ? '' : ` - cannot be sorted (${c.type || 'unknown type'})`}`;
    return `<th data-c="${i}" data-sort="${dir}"${sortable ? '' : ' data-nosort="1"'}${numeric[i] ? ' data-num="1"' : ''}`
      + ` title="${escapeHtml(tip)}"><span class="hl">${escapeHtml(name)}</span><span class="si"></span><span class="rz"></span></th>`;
  }).join('');

  const body = rows.map((row, r) =>
    `<tr data-r="${r}"><th class="rn">${r + 1}<span class="rh"></span></th>${columns.map((_, c) => cell(row[c])).join('')}</tr>`,
  ).join('');

  // Per-grid rules: one width variable per column, and right alignment for numeric columns.
  const rules = [`#${id} .gt tr>:nth-child(1){width:var(--rn)}`];
  const vars = [`--rn:${Math.max(36, String(rows.length).length * 8 + 20)}px`];
  columns.forEach((_, i) => {
    rules.push(`#${id} .gt tr>:nth-child(${i + 2}){width:var(--c${i})}`);
    if (numeric[i]) rules.push(`#${id} .gt tbody tr>:nth-child(${i + 2}){text-align:right;font-variant-numeric:tabular-nums}`);
    vars.push(`--c${i}:${DEFAULT_COLUMN_WIDTH}px`);
  });

  const set = spec.set === undefined ? '' : ` data-set="${Math.floor(spec.set)}"`;
  return `<div class="dgrid" id="${id}" data-mode="${spec.sortMode}"${set} style="${vars.join(';')}">
<style>${rules.join('\n')}</style>
<div class="gscroll"><table class="gt"><thead><tr><th class="rn" aria-label="Row number"></th>${head}</tr></thead><tbody>${body}</tbody></table>
<button type="button" class="gcopy" title="Copy to clipboard" aria-label="Copy cell value to clipboard" style="display:none">${COPY_ICON}</button></div>
</div>`;
}

function cell(value: unknown): string {
  if (value === null || value === undefined) return '<td class="null" data-null="1">NULL</td>';
  const text = cellText(value);
  const cut = typeof value === 'string' ? TRUNCATED_SUFFIX.exec(value) : null;
  const tip = cut
    ? `${tooltipText(text)}\n\nTruncated by the server: the full value has ${cut[1]} ${cut[2]}.`
    : tooltipText(text);
  return `<td${cut ? ' class="trunc"' : ''} title="${escapeHtml(tip)}">${escapeHtml(text)}</td>`;
}

/** The grid stylesheet (theme variables only). */
export const GRID_CSS = `
  .dgrid { display: flex; flex-direction: column; min-height: 0; }
  .gscroll { position: relative; overflow: auto; flex: 1 1 auto; min-height: 0; }
  .gt { display: block; border-collapse: separate; border-spacing: 0; width: max-content; min-width: 100%; user-select: text; }
  .gt thead { display: block; position: sticky; top: 0; z-index: 2; }
  .gt tbody { display: flex; flex-direction: column; }
  .gt tr { display: flex; align-items: stretch; }
  .gt th, .gt td { flex: 0 0 auto; box-sizing: border-box; padding: 2px 8px; line-height: 18px;
    border-right: 1px solid var(--vscode-panel-border); border-bottom: 1px solid var(--vscode-panel-border);
    text-align: left; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    font-family: var(--vscode-editor-font-family), monospace; font-size: var(--vscode-editor-font-size, 12px); }
  .gt thead th { position: relative; padding-right: 16px; font-weight: 600; font-family: var(--vscode-font-family);
    background: var(--vscode-editorWidget-background, var(--vscode-editor-background)); cursor: pointer; user-select: none; }
  .gt thead th[data-nosort="1"] { cursor: default; }
  .gt .si { margin-left: 4px; font-size: 9px; }
  .gt th[data-sort="asc"] .si::after { content: "\\25B2"; }
  .gt th[data-sort="desc"] .si::after { content: "\\25BC"; }
  .gt .rz { position: absolute; top: 0; right: 0; width: 6px; height: 100%; cursor: col-resize; }
  .gt .rz:hover { background: var(--vscode-sash-hoverBorder, var(--vscode-focusBorder)); }
  .gt .rn { position: sticky; left: 0; z-index: 1; padding: 2px 6px; text-align: right; font-weight: normal;
    font-variant-numeric: tabular-nums; color: var(--vscode-descriptionForeground); user-select: none;
    background: var(--vscode-editorWidget-background, var(--vscode-editor-background)); }
  .gt thead .rn { z-index: 3; cursor: default; padding-right: 6px; }
  .gt .rh { position: absolute; left: 0; bottom: 0; width: 100%; height: 5px; cursor: row-resize; }
  .gt .rh:hover { background: var(--vscode-sash-hoverBorder, var(--vscode-focusBorder)); }
  .gt tbody tr:hover > td { background: var(--vscode-list-hoverBackground); }
  .gt td.null { color: var(--vscode-descriptionForeground); font-style: italic; }
  .gt td.trunc { text-decoration: underline dotted var(--vscode-descriptionForeground); }
  .gt tbody tr[data-fit="1"] > td { white-space: pre-wrap; overflow-wrap: anywhere; text-overflow: clip;
    max-height: ${ROW_FIT_MAX_HEIGHT}px; overflow: auto; }
  .gcopy { position: absolute; z-index: 4; width: 20px; height: 20px; padding: 2px; line-height: 0; cursor: pointer;
    border: 1px solid var(--vscode-widget-border, var(--vscode-panel-border)); border-radius: 3px;
    color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
    background: var(--vscode-button-secondaryBackground, var(--vscode-editorWidget-background)); }
  .gcopy:hover { background: var(--vscode-button-secondaryHoverBackground, var(--vscode-list-hoverBackground)); }
`;

/**
 * The grid client script: defines `initGrids(vscode)`, which wires every `.dgrid` on the page. The sort comparator
 * is the tested gridModel code, inlined. Pages call initGrids with their acquireVsCodeApi() object.
 */
export function gridScript(): string {
  return `${compareGridValues.toString()}
${gridSortOrder.toString()}
${GRID_CLIENT}`;
}

const GRID_CLIENT = String.raw`
function initGrids(vscode) {
  var MIN_W = ${MIN_COLUMN_WIDTH}, FIT_MAX = ${FIT_MAX_WIDTH}, INIT_ROWS = ${INITIAL_FIT_ROWS}, INIT_MAX = ${INITIAL_FIT_MAX_WIDTH}, MIN_H = 16;
  var ctx = document.createElement('canvas').getContext('2d');
  var drag = null, dragEnded = 0;
  document.addEventListener('mousemove', function (e) {
    if (!drag) return;
    if (drag.kind === 'col') drag.set(Math.max(MIN_W, drag.start + e.clientX - drag.x));
    else drag.tr.style.height = Math.max(MIN_H, drag.start + e.clientY - drag.y) + 'px';
  });
  document.addEventListener('mouseup', function () {
    if (drag) { drag = null; dragEnded = Date.now(); document.body.style.cursor = ''; }
  });
  var grids = document.querySelectorAll('.dgrid');
  for (var g = 0; g < grids.length; g++) setUp(grids[g]);

  function fontOf(el) {
    var cs = getComputedStyle(el);
    return {
      font: cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily,
      pad: parseFloat(cs.paddingLeft) + parseFloat(cs.paddingRight) + parseFloat(cs.borderLeftWidth) + parseFloat(cs.borderRightWidth)
    };
  }

  function setUp(grid) {
    var scroll = grid.querySelector('.gscroll');
    var button = grid.querySelector('.gcopy');
    var heads = Array.prototype.slice.call(grid.querySelectorAll('thead th[data-c]'));
    var rows = Array.prototype.slice.call(grid.querySelectorAll('tbody tr'));
    var mode = grid.getAttribute('data-mode');
    var set = grid.getAttribute('data-set');
    var hovered = null;

    function setWidth(c, w) { grid.style.setProperty('--c' + c, Math.round(w) + 'px'); }

    // Widest rendered cell of column c (header included), measured with each cell's computed font plus padding.
    function fit(c, rowLimit, cap) {
      var head = heads[c];
      var hf = fontOf(head);
      ctx.font = hf.font;
      var w = ctx.measureText(head.querySelector('.hl').textContent).width + hf.pad + 12;
      var fonts = {}, current = '';
      var n = Math.min(rows.length, rowLimit);
      for (var i = 0; i < n && w < cap; i++) {
        var tr = rows[i];
        if (tr.style.display === 'none') continue;
        var td = tr.children[c + 1];
        if (!td) continue;
        var key = td.getAttribute('class') || '';
        var f = fonts[key] || (fonts[key] = fontOf(td));
        if (current !== key) { ctx.font = f.font; current = key; }
        var text = td.textContent;
        if (text.length > 2000) text = text.slice(0, 2000);
        var tw = ctx.measureText(text.replace(/\s+/g, ' ')).width + f.pad;
        if (tw > w) w = tw;
      }
      setWidth(c, Math.min(cap, Math.max(MIN_W, Math.ceil(w) + 2)));
    }
    for (var c0 = 0; c0 < heads.length; c0++) fit(c0, INIT_ROWS, INIT_MAX);

    function hide() { hovered = null; button.style.display = 'none'; }

    grid.addEventListener('mousedown', function (e) {
      var t = e.target, cls = t.getAttribute && t.getAttribute('class');
      if (cls === 'rz') {
        var c = Number(t.parentNode.getAttribute('data-c'));
        drag = { kind: 'col', x: e.clientX, start: heads[c].getBoundingClientRect().width, set: function (w) { setWidth(c, w); } };
        document.body.style.cursor = 'col-resize';
      } else if (cls === 'rh') {
        var tr = t.parentNode.parentNode;
        drag = { kind: 'row', y: e.clientY, start: tr.getBoundingClientRect().height, tr: tr };
        document.body.style.cursor = 'row-resize';
      } else return;
      e.preventDefault();
      hide();
    });

    grid.addEventListener('dblclick', function (e) {
      var t = e.target, cls = t.getAttribute && t.getAttribute('class');
      if (cls === 'rz') {
        fit(Number(t.parentNode.getAttribute('data-c')), rows.length, FIT_MAX);
      } else if (cls === 'rh') {
        // Fit the row to its content (wrapped, capped); a second double-click returns it to one line.
        var tr = t.parentNode.parentNode;
        tr.setAttribute('data-fit', tr.getAttribute('data-fit') === '1' ? '0' : '1');
        tr.style.height = '';
      } else return;
      e.preventDefault();
      hide();
    });

    grid.querySelector('thead').addEventListener('click', function (e) {
      var t = e.target;
      if (Date.now() - dragEnded < 250 || (t.getAttribute && t.getAttribute('class') === 'rz')) return;
      var th = t.closest ? t.closest('th[data-c]') : null;
      if (!th) return;
      var cur = th.getAttribute('data-sort') || '';
      var next = cur === '' ? 'asc' : cur === 'asc' ? 'desc' : '';
      var col = Number(th.getAttribute('data-c'));
      if (mode === 'server') {
        if (th.getAttribute('data-nosort') === '1') return;
        var status = document.getElementById('gstatus');
        if (status) status.textContent = 'Loading…';
        vscode.postMessage({ type: 'sort', col: col, dir: next || 'none' });
        return;
      }
      for (var h = 0; h < heads.length; h++) heads[h].setAttribute('data-sort', heads[h] === th ? next : '');
      if (!next) {
        for (var i = 0; i < rows.length; i++) rows[i].style.order = '';
      } else {
        var values = rows.map(function (tr) {
          var td = tr.children[col + 1];
          return !td || td.getAttribute('data-null') === '1' ? null : td.textContent;
        });
        var order = gridSortOrder(values, th.getAttribute('data-num') === '1', next);
        for (var p = 0; p < order.length; p++) rows[order[p]].style.order = String(p);
      }
      var note = document.querySelector('[data-sortnote="' + grid.id + '"]');
      if (note) note.style.display = next ? '' : 'none';
      hide();
    });

    grid.querySelector('tbody').addEventListener('mouseover', function (e) {
      if (drag) return;
      var td = e.target.closest ? e.target.closest('td') : null;
      if (!td || td === hovered) return;
      hovered = td;
      var tr = td.parentNode;
      var sr = scroll.getBoundingClientRect(), cr = td.getBoundingClientRect();
      button.style.top = (cr.top - sr.top + scroll.scrollTop + 1) + 'px';
      button.style.left = (cr.right - sr.left + scroll.scrollLeft - 23) + 'px';
      button.setAttribute('data-r', tr.getAttribute('data-r'));
      button.setAttribute('data-c', String(Array.prototype.indexOf.call(tr.children, td) - 1));
      button.style.display = 'block';
    });
    scroll.addEventListener('mouseleave', hide);
    scroll.addEventListener('scroll', hide);
    button.addEventListener('click', function (e) {
      e.preventDefault();
      e.stopPropagation();
      var msg = { type: 'copy', row: Number(button.getAttribute('data-r')), col: Number(button.getAttribute('data-c')) };
      if (set !== null) msg.set = Number(set);
      vscode.postMessage(msg);
    });
  }
}
`;

// --- Data View page --------------------------------------------------------------------------------------------

export interface DataViewModel {
  /** `schema.name` of the table or view. */
  objectName: string;
  connection: string;
  /** The TOP the rows were (or are being) queried with. */
  top: number;
  loading?: boolean;
  /** Shown instead of the grid (e.g. the runner is unavailable). */
  error?: string;
  result?: { columns: readonly GridColumn[]; rows: readonly (readonly unknown[])[]; truncated: boolean };
  /** Server-side sort of `result` (column index). */
  sort?: { col: number; dir: SortDir };
  /** Server warnings (e.g. a response cap), shown under the bar. */
  notes?: readonly string[];
}

/** The Data View webview document. */
export function renderDataView(model: DataViewModel, nonce: string): string {
  const { result } = model;
  let meta = '';
  let content = '';
  if (model.error !== undefined) {
    content = `<p class="error">${escapeHtml(model.error)}</p>`;
  } else if (result) {
    const cols = result.columns.length;
    meta = `${rowCountLabel(result.rows.length, result.truncated)} · ${cols} column${cols === 1 ? '' : 's'} · read-only`;
    content = renderGrid({ id: 'gd', columns: result.columns, rows: result.rows, sortMode: 'server', sort: model.sort })
      + (result.rows.length === 0 ? '<p class="hint">No rows.</p>' : '');
  } else {
    content = '<p class="hint">Loading…</p>';
  }
  const notes = (model.notes ?? []).map(n => `<div class="note">${escapeHtml(n)}</div>`).join('');

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
  html, body { height: 100%; }
  body { margin: 0; padding: 0; display: flex; flex-direction: column; overflow: hidden;
         font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
         color: var(--vscode-foreground); background: var(--vscode-editor-background); }
  .bar { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; padding: 8px 12px;
         border-bottom: 1px solid var(--vscode-panel-border); }
  .name { font-weight: 600; }
  .meta, .note, .hint { color: var(--vscode-descriptionForeground); font-size: 12px; }
  .note { padding: 4px 12px; }
  .hint { padding: 4px 12px; margin: 0; }
  .error { padding: 10px 12px; margin: 0; color: var(--vscode-errorForeground); white-space: pre-wrap; }
  label { display: flex; align-items: center; gap: 4px; font-size: 12px; }
  input { padding: 3px 6px; color: var(--vscode-input-foreground); background: var(--vscode-input-background);
          border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); border-radius: 2px;
          font-family: inherit; font-size: 12px; outline: none; }
  input:focus { border-color: var(--vscode-focusBorder); }
  input[aria-invalid="true"] { border-color: var(--vscode-inputValidation-errorBorder, var(--vscode-errorForeground)); }
  #top { width: 64px; }
  #filter { flex: 1; min-width: 140px; max-width: 300px; }
  button.act { padding: 3px 10px; border: none; border-radius: 2px; cursor: pointer; font-family: inherit; font-size: 12px;
               color: var(--vscode-button-foreground); background: var(--vscode-button-background); }
  button.act:hover { background: var(--vscode-button-hoverBackground); }
  #toperr { color: var(--vscode-errorForeground); font-size: 12px; }
  .dgrid { flex: 1; }
${GRID_CSS}
</style>
</head>
<body>
  <div class="bar">
    <span class="name">${escapeHtml(model.objectName)}</span>
    <span class="meta" title="Connection">${escapeHtml(model.connection)}</span>
    <label for="top">TOP <input id="top" type="number" min="${MIN_TOP}" max="${MAX_TOP}" step="1" value="${model.top}" aria-label="Rows to load (TOP)"></label>
    <button type="button" class="act" id="reload" title="Load the rows again with this TOP and the current sort">Reload</button>
    <span id="toperr" role="alert"></span>
    <span class="meta" id="gstatus">${model.loading ? 'Loading…' : ''}</span>
    <span class="meta">${escapeHtml(meta)}</span>
    <input id="filter" type="text" placeholder="Filter rows…" aria-label="Filter rows">
    <span class="meta" id="shown"></span>
  </div>
  ${notes}
  ${content}
<script nonce="${nonce}">
${gridScript()}
(function () {
  var vscode = acquireVsCodeApi();
  initGrids(vscode);
  var top = document.getElementById('top');
  var err = document.getElementById('toperr');
  function reload() {
    var v = top.value.trim();
    var n = /^\\d+$/.test(v) ? Number(v) : NaN;
    if (!(n >= ${MIN_TOP} && n <= ${MAX_TOP})) {
      err.textContent = 'TOP must be a whole number from ${MIN_TOP} to ${MAX_TOP}.';
      top.setAttribute('aria-invalid', 'true');
      return;
    }
    err.textContent = '';
    top.setAttribute('aria-invalid', 'false');
    document.getElementById('gstatus').textContent = 'Loading…';
    vscode.postMessage({ type: 'reload', top: n });
  }
  document.getElementById('reload').addEventListener('click', reload);
  top.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); reload(); } });

  var filter = document.getElementById('filter');
  var shown = document.getElementById('shown');
  var rows = Array.prototype.slice.call(document.querySelectorAll('#gd tbody tr'));
  // Cell text only (not the row numbers), lower-cased once.
  var texts = rows.map(function (tr) {
    return Array.prototype.map.call(tr.querySelectorAll('td'), function (td) { return td.textContent; }).join('\\u0001').toLowerCase();
  });
  filter.addEventListener('input', function () {
    var q = filter.value.toLowerCase();
    var count = 0;
    for (var i = 0; i < rows.length; i++) {
      var match = q === '' || texts[i].indexOf(q) !== -1;
      rows[i].style.display = match ? '' : 'none';
      if (match) count++;
    }
    shown.textContent = q === '' ? '' : count + ' of ' + rows.length + ' match';
  });
})();
</script>
</body>
</html>`;
}
