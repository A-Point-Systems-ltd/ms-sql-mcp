// The data grid shared by Data View and the Results panel: grid HTML (toolbar, header with filter row, menus), its
// stylesheet and its client script, plus the Data View page. No 'vscode' import (unit-testable).
//
// Rows can hold client personal data: every value is HTML-escaped, nothing is loaded from the network, and the
// client script never builds HTML. It changes the page through textContent, setAttribute / removeAttribute, style
// and form-control state (checkbox `checked`, scroll offsets, focus): column order, hidden and frozen columns and
// the selected columns are generated CSS (integers only) written to a <style> element's textContent; the local sort
// is the flex `order` of each row. Copy, copy row, copy selection, the cell viewer and Export post only indexes;
// the extension resolves the values from its own copy of the result.
import { rowCountLabel } from '../dataTable';
import { escapeHtml } from '../webviewUtil';
import {
  FILTER_OPS, FILTER_OP_LABELS, FilterOp, GridColumn, GridViewState, MAX_TOP, MIN_TOP, SortDir, TRUNCATED_SUFFIX, cellMatches,
  cellText, compareGridValues, countMatches, defaultFilterOp, filterMatches, gridSortOrder, isDateType, isNumericType, isSortableType,
  isTextFilterable, numericStats, parsePeriod, tooltipText,
} from './gridModel';
import type { ConnectionColor } from '../connections/profile';

/** Column widths before the client script fits them (px), and the auto-fit limits it applies. */
export const DEFAULT_COLUMN_WIDTH = 120;
export const MIN_COLUMN_WIDTH = 40;
export const FIT_MAX_WIDTH = 600;
/** The first auto-fit (on load) looks at this many rows and stops at this width. */
export const INITIAL_FIT_ROWS = 200;
export const INITIAL_FIT_MAX_WIDTH = 300;
/** A row auto-fitted to its content grows to at most this height; taller cells scroll. */
export const ROW_FIT_MAX_HEIGHT = 400;
/** Data View filters are applied after this pause in typing (or at once with Enter). */
export const SERVER_FILTER_DEBOUNCE_MS = 400;
/** A column whose filter is a from / to period is at least this wide, so both date pickers fit. */
export const PERIOD_FILTER_WIDTH = 250;

/**
 * Data View editing state for the grid. Pending values are shown in place of the loaded ones; new rows follow the
 * loaded rows (row index = loaded + insert index).
 */
export interface GridEditSpec {
  update: boolean;
  insert: boolean;
  delete: boolean;
  /** Per column: values can be written. */
  writable: readonly boolean[];
  /** Per column: filled by the server in a new row (identity, computed). */
  auto: readonly boolean[];
  edits: ReadonlyMap<number, ReadonlyMap<number, string | null>>;
  deletes: ReadonlySet<number>;
  inserts: readonly (readonly (string | null | undefined)[])[];
  /** Cells whose pending value cannot be saved: row, column and message. */
  problems?: readonly { row: number; col: number; message: string }[];
}

/** A filter as the filter row shows it (column index, operator, value). */
export interface GridFilterInput { col: number; op: FilterOp; value: string }

export interface GridSpec {
  /** Element id of the grid; letters, digits, '-' and '_' only. */
  id: string;
  columns: readonly GridColumn[];
  rows: readonly (readonly unknown[])[];
  /**
   * 'local': header clicks sort and the filter row filters the loaded rows in the webview (Results panel).
   * 'server': they post {type:'sort'} / {type:'filter'} to re-query (Data View).
   */
  sortMode: 'local' | 'server';
  /** Server mode: the sort the rows were queried with (column index). */
  sort?: { col: number; dir: SortDir };
  /** Server mode: the filters the rows were queried with. */
  filters?: readonly GridFilterInput[];
  /** Results panel: the result set index, sent with every grid message. */
  set?: number;
  /** Render counter of the page (`data-gen`); grid messages echo it so the extension can drop stale ones. */
  gen: number;
  /** The client view to restore (column order, hidden, frozen, wrap, striping, widths). */
  view?: GridViewState;
  /** Extra toolbar controls (Data View: TOP, Reload, Load more); trusted HTML built by this module. */
  toolbarExtra?: string;
  /** Column index of the filter input to focus after the render (the one being typed in). */
  focus?: number;
  /** Scroll offsets [top, left] to restore after the render (Load more). */
  scroll?: readonly [number, number];
  /** Data View editing (in-cell edits, new and deleted rows); omitted when the grid is read-only. */
  edit?: GridEditSpec;
  /**
   * Data View without editing: why (read-only connection, a view, ...). With it (or with `edit`) a double-click on a
   * cell that cannot be edited explains why instead of opening the value in a viewer tab; Results grids keep that.
   */
  readOnlyReason?: string;
}

const SAFE_ID = /^[A-Za-z][\w-]*$/;

const svg = (path: string, extra = '') =>
  `<svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" focusable="false"${extra}><path fill="currentColor" d="${path}"/></svg>`;

const COPY_ICON = svg('M4 4V2.5A1.5 1.5 0 0 1 5.5 1h7A1.5 1.5 0 0 1 14 2.5v7a1.5 1.5 0 0 1-1.5 1.5H11v1.5'
  + 'A1.5 1.5 0 0 1 9.5 14h-7A1.5 1.5 0 0 1 1 12.5v-7A1.5 1.5 0 0 1 2.5 4H4Zm1 0h4.5A1.5 1.5 0 0 1 11 5.5V10h1.5a.5.5 0 0 0 '
  + '.5-.5v-7a.5.5 0 0 0-.5-.5h-7a.5.5 0 0 0-.5.5V4ZM2.5 5a.5.5 0 0 0-.5.5v7a.5.5 0 0 0 .5.5h7a.5.5 0 0 0 .5-.5v-7a.5.5 0 0 '
  + '0 0-.5-.5h-7Z');
const VIEW_ICON = svg('M2 2h7v1H3v10h10V7h1v7H2V2Zm8 0h4v4h-1V3.7L8.4 8.3l-.7-.7L12.3 3H10V2Z');
const MORE_ICON = svg('M3 7h2v2H3V7Zm4 0h2v2H7V7Zm4 0h2v2h-2V7Z');
const COLUMNS_ICON = svg('M1 2h14v12H1V2Zm1 1v10h3V3H2Zm4 0v10h4V3H6Zm5 0v10h3V3h-3Z');
const WRAP_ICON = svg('M1 3h14v1H1V3Zm0 4h11a2.5 2.5 0 0 1 0 5H9v1.5L6.5 11.5 9 9.5V11h3a1.5 1.5 0 0 0 0-3H1V7Zm0 5h4v1H1v-1Z');
const STRIPE_ICON = svg('M1 2h14v3H1V2Zm0 4.5h14v1H1v-1ZM1 9h14v3H1V9Zm0 4.5h14v1H1v-1Z');
const EXPORT_ICON = svg('M8 1v8.3l2.6-2.6.7.7L7.5 11.2 3.7 7.4l.7-.7L7 9.3V1h1ZM2 12h1v2h9v-2h1v3H2v-3Z');
const PREV_ICON = svg('M8 4.3 3.4 9l.7.7L8 5.7l3.9 4 .7-.7L8 4.3Z');
const NEXT_ICON = svg('M8 11.7 3.4 7l.7-.7L8 10.3l3.9-4 .7.7L8 11.7Z');

/** Menu items: action, label, and the menus they appear in (head = header, cell = data cell, row = row number). */
const MENU_ITEMS: readonly [string, string, string][] = [
  ['freeze', 'Freeze up to here', 'head'],
  ['unfreeze', 'Unfreeze columns', 'head'],
  ['hide', 'Hide column', 'head'],
  ['copysel', 'Copy', 'cell'],
  ['copycell', 'Copy cell', 'cell'],
  ['rowtsv', 'Copy row (tab-separated)', 'cell row'],
  ['rowjson', 'Copy row (JSON)', 'cell row'],
  ['view', 'Open in viewer', 'cell'],
  ['selall', 'Select all', 'cell'],
  ['editcell', 'Edit cell (F2)', 'cell'],
  ['setnull', 'Set to NULL', 'cell'],
  ['delrows', 'Delete row(s)', 'cell row'],
  ['revert', 'Revert row(s)', 'cell row'],
];

/** Menu actions that need editing, and the grid capability each one needs (u update, d delete, any = any edit). */
const EDIT_ACTIONS: Readonly<Record<string, string>> = { editcell: 'any', setnull: 'any', delrows: 'any', revert: 'any' };

/** One grid: toolbar, header row with sort / resize / reorder, filter row, row-number gutter, menus, hover buttons. */
export function renderGrid(spec: GridSpec): string {
  if (!SAFE_ID.test(spec.id)) throw new Error(`Invalid grid id: ${spec.id}`);
  const { id, columns, rows } = spec;
  const numeric = columns.map(c => isNumericType(c.type));
  const name = (c: GridColumn) => c.name || '(No column name)';

  const head = columns.map((c, i) => {
    const sortable = spec.sortMode === 'local' || isSortableType(c.type);
    const dir = spec.sort && spec.sort.col === i ? spec.sort.dir : '';
    const tip = `${name(c)} (${c.type || 'unknown type'})${sortable ? '' : ` - cannot be sorted (${c.type || 'unknown type'})`}`;
    return `<th data-c="${i}" data-sort="${dir}"${sortable ? '' : ' data-nosort="1"'}${numeric[i] ? ' data-num="1"' : ''}`
      + `${spec.edit?.writable[i] ? ' data-w="1"' : ''}${spec.edit?.auto[i] ? ' data-auto="1"' : ''}`
      + ` draggable="true" title="${escapeHtml(tip)}"><span class="hl">${escapeHtml(name(c))}</span><span class="si"></span><span class="rz"></span></th>`;
  }).join('');

  const filterRow = columns.map((c, i) => {
    const current = spec.filters?.find(f => f.col === i);
    const textless = spec.sortMode === 'server' && !isTextFilterable(c.type);
    const date = isDateType(c.type);
    const op: FilterOp = current?.op ?? (textless ? 'contains' : defaultFilterOp(c.type));
    const options = FILTER_OPS
      .filter(o => (!textless || o === 'contains' || o === 'null' || o === 'notnull') && (o !== 'period' || date))
      .map(o => `<option value="${o}"${o === op ? ' selected' : ''}>${escapeHtml(textless && o === 'contains' ? 'any' : FILTER_OP_LABELS[o])}</option>`)
      .join('');
    const disabled = textless || op === 'null' || op === 'notnull';
    const label = escapeHtml(name(c));
    const text = op === 'period' ? '' : current?.value ?? '';
    const period = op === 'period' && current ? parsePeriod(current.value) ?? {} : {};
    const dates = date
      ? `<span class="gper"><input type="date" data-p="from" aria-label="${label} from" title="From (inclusive)" min="0001-01-01" max="9999-12-31" value="${escapeHtml(period.from ?? '')}">`
        + `<input type="date" data-p="to" aria-label="${label} to" title="To (inclusive)" min="0001-01-01" max="9999-12-31" value="${escapeHtml(period.to ?? '')}"></span>`
      : '';
    return `<th class="fc" data-fc="${i}" data-op="${op}"${textless ? ' data-textless="1"' : ''}><select aria-label="Filter operator for ${label}">${options}</select>`
      + `<input type="text" data-c="${i}" aria-label="Filter ${label}" placeholder="filter" value="${escapeHtml(text)}"${disabled ? ' disabled' : ''}>${dates}</th>`;
  }).join('');

  const ed = spec.edit;
  const problems = new Map((ed?.problems ?? []).map(p => [`${p.row}:${p.col}`, p.message]));
  const mark = (r: number, c: number, edited: boolean) => {
    const problem = problems.get(`${r}:${c}`);
    return (edited ? ' data-ed="1"' : '') + (problem ? ` data-err="1" title="${escapeHtml(problem)}"` : '');
  };
  const body = rows.map((row, r) => {
    const edits = ed?.edits.get(r);
    const state = ed?.deletes.has(r) ? ' data-del="1"' : edits?.size ? ' data-dirty="1"' : '';
    return `<tr data-r="${r}"${state}><th class="rn">${r + 1}<span class="rh"></span></th>${columns.map((_, c) =>
      (edits?.has(c) ? cell(edits.get(c), mark(r, c, true)) : cell(row[c], mark(r, c, false)))).join('')}</tr>`;
  }).join('') + (ed?.inserts ?? []).map((cells, i) => {
    const r = rows.length + i;
    return `<tr data-r="${r}" data-new="1"><th class="rn" title="New row">*<span class="rh"></span></th>${columns.map((_, c) => {
      if (ed!.auto[c]) return '<td class="dflt" data-auto="1">(auto)</td>';
      const v = cells[c];
      return v === undefined ? `<td class="dflt" data-dflt="1"${mark(r, c, false)}>(default)</td>` : cell(v, mark(r, c, true));
    }).join('')}</tr>`;
  }).join('');

  // Per-grid rules: one width variable per column, and right alignment for numeric columns.
  const rules = [`#${id} .gt tr>:nth-child(1){width:var(--rn)}`];
  const vars = [`--rn:${Math.max(36, String(rows.length).length * 8 + 20)}px`];
  columns.forEach((_, i) => {
    rules.push(`#${id} .gt tr>:nth-child(${i + 2}){width:var(--c${i})}`);
    if (numeric[i]) rules.push(`#${id} .gt tbody tr>:nth-child(${i + 2}){text-align:right;font-variant-numeric:tabular-nums}`);
    vars.push(`--c${i}:${spec.view?.widths?.[i] ?? DEFAULT_COLUMN_WIDTH}px`);
  });

  const v = spec.view;
  const ints = (a: readonly number[] | undefined) => (a ?? []).map(n => Math.floor(n)).join(',');
  const attrs = [
    `data-mode="${spec.sortMode}"`,
    ...(ed ? [`data-edit="${[ed.update ? 'u' : '', ed.insert ? 'i' : '', ed.delete ? 'd' : ''].join('')}"`, `data-loaded="${rows.length}"`] : []),
    ...(spec.set === undefined ? [] : [`data-set="${Math.floor(spec.set)}"`]),
    `data-gen="${Math.floor(spec.gen)}"`,
    ...(v ? [`data-order="${ints(v.order)}"`, `data-hidden="${ints(v.hidden)}"`, `data-freeze="${Math.floor(v.freeze)}"`,
      `data-wrap="${v.wrap ? 1 : 0}"`, `data-stripe="${v.stripe ? 1 : 0}"`] : ['data-freeze="1"', 'data-wrap="0"', 'data-stripe="1"']),
    ...(v?.widths ? [`data-widths="${ints(v.widths)}"`] : []),
    ...(spec.scroll ? [`data-scroll="${ints(spec.scroll)}"`] : []),
    ...(spec.focus !== undefined ? [`data-focus="${Math.floor(spec.focus)}"`] : []),
    ...(!ed && spec.readOnlyReason !== undefined ? [`data-ro="${escapeHtml(spec.readOnlyReason)}"`] : []),
  ].join(' ');

  const checklist = columns.map((c, i) =>
    `<label><input type="checkbox" data-col="${i}" checked> ${escapeHtml(name(c))}</label>`).join('');
  const menu = MENU_ITEMS.filter(([act]) => !EDIT_ACTIONS[act] || ed).map(([act, label, kinds]) =>
    `<button type="button" role="menuitem" data-act="${act}" data-k="${kinds}">${escapeHtml(label)}</button>`).join('');
  const tb = (act: string, icon: string, label: string, title: string, pressed?: boolean) =>
    `<button type="button" class="tb" data-act="${act}" title="${escapeHtml(title)}"${pressed === undefined ? '' : ` aria-pressed="${pressed}"`}>${icon}<span>${escapeHtml(label)}</span></button>`;

  return `<div class="dgrid" id="${id}" ${attrs} style="${vars.join(';')}">
<style class="gdyn"></style>
<style>${rules.join('\n')}</style>
<div class="gbar" role="toolbar" aria-label="Grid tools">${spec.toolbarExtra ?? ''}${tb('columns', COLUMNS_ICON, 'Columns', 'Show or hide columns')}${tb('wrap', WRAP_ICON, 'Wrap', 'Wrap all rows', false)}${tb('stripe', STRIPE_ICON, 'Stripes', 'Row striping', true)}<span class="gsearch"><input class="gq" type="search" placeholder="Search" aria-label="Search loaded rows"><span class="gqn" aria-live="polite"></span>${tb('prev', PREV_ICON, '', 'Previous match')}${tb('next', NEXT_ICON, '', 'Next match')}</span>${tb('export', EXPORT_ICON, 'CSV', 'Export CSV…')}<span class="gstats" aria-live="polite"></span></div>
<div class="gcols" role="dialog" aria-label="Columns" style="display:none">${checklist}<button type="button" data-act="showall">Show all</button></div>
<div class="gmenu" role="menu" style="display:none">${menu}</div>
<div class="gscroll"><table class="gt"><thead><tr class="gh"><th class="rn" aria-label="Row number"></th>${head}</tr><tr class="gf"><th class="rn"></th>${filterRow}</tr></thead><tbody>${body}</tbody></table>
<button type="button" class="ghover gcopy" title="Copy to clipboard" aria-label="Copy cell value to clipboard" style="display:none">${COPY_ICON}</button>
<button type="button" class="ghover gview" title="Open in viewer" aria-label="Open cell value in a viewer" style="display:none">${VIEW_ICON}</button>
<button type="button" class="ghover growbtn" title="Row actions" aria-label="Row actions" aria-haspopup="menu" style="display:none">${MORE_ICON}</button>
<div class="gtip" role="status" style="display:none"></div>${ed ? '<textarea class="gedit" rows="1" spellcheck="false" aria-label="Cell value (Enter saves, Alt+Enter new line, Esc cancels)" style="display:none"></textarea>' : ''}</div>
</div>`;
}

/**
 * One cell. Its tooltip is set lazily by the client script on hover (from textContent, capped by tooltipText), so
 * values are not rendered twice; only a value cut by the server gets its tooltip here, with the full-size note.
 */
function cell(value: unknown, attrs = ''): string {
  if (value === null || value === undefined) return `<td class="null" data-null="1"${attrs}>NULL</td>`;
  const text = cellText(value);
  const cut = typeof value === 'string' ? TRUNCATED_SUFFIX.exec(value) : null;
  if (!cut) return `<td${attrs}>${escapeHtml(text)}</td>`;
  const tip = `${tooltipText(text)}\n\nTruncated by the server: the full value has ${cut[1]} ${cut[2]}.`;
  return `<td class="trunc" title="${escapeHtml(tip)}"${attrs.replace(/ title="[^"]*"/, '')}>${escapeHtml(text)}</td>`;
}

/** The grid stylesheet (theme variables only). */
export const GRID_CSS = `
  .dgrid { display: flex; flex-direction: column; min-height: 0; position: relative;
    --g-bg: var(--vscode-editor-background);
    --g-alt: color-mix(in srgb, var(--vscode-list-hoverBackground) 45%, transparent);
    --g-sel: var(--vscode-list-inactiveSelectionBackground, var(--vscode-editor-selectionBackground));
    --g-hit: var(--vscode-editor-findMatchHighlightBackground, rgba(234, 92, 0, 0.33));
    --g-cur: var(--vscode-editor-findMatchBackground, rgba(81, 92, 106, 0.8)); }
  .gbar { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; padding: 3px 0; }
  .gbar .tb { display: inline-flex; align-items: center; gap: 3px; padding: 2px 6px; border: 1px solid transparent; border-radius: 3px;
    background: transparent; color: var(--vscode-foreground); cursor: pointer; font-family: inherit; font-size: 12px; }
  .gbar .tb:hover { background: var(--vscode-toolbar-hoverBackground, var(--vscode-list-hoverBackground)); }
  .gbar .tb:focus-visible, .gmenu button:focus-visible, .gcols button:focus-visible { outline: 1px solid var(--vscode-focusBorder); }
  .gbar .tb[aria-pressed="true"] { background: var(--vscode-inputOption-activeBackground, var(--vscode-list-activeSelectionBackground));
    border-color: var(--vscode-inputOption-activeBorder, transparent); }
  .gbar .tb span:empty { display: none; }
  .gsearch { display: inline-flex; align-items: center; gap: 2px; }
  .gq, .gbar input { padding: 2px 6px; color: var(--vscode-input-foreground); background: var(--vscode-input-background);
    border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); border-radius: 2px; font-family: inherit; font-size: 12px; outline: none; }
  .gq { width: 150px; }
  .gq:focus, .gbar input:focus { border-color: var(--vscode-focusBorder); }
  .gqn, .gstats { color: var(--vscode-descriptionForeground); font-size: 12px; }
  .gstats { margin-left: auto; font-variant-numeric: tabular-nums; }
  .gcols, .gmenu { position: fixed; z-index: 10; display: flex; flex-direction: column; min-width: 160px; max-height: 60vh; overflow: auto;
    padding: 4px; border: 1px solid var(--vscode-menu-border, var(--vscode-widget-border, var(--vscode-panel-border))); border-radius: 4px;
    color: var(--vscode-menu-foreground, var(--vscode-foreground)); background: var(--vscode-menu-background, var(--vscode-editorWidget-background));
    box-shadow: 0 2px 8px var(--vscode-widget-shadow, rgba(0, 0, 0, 0.36)); font-size: 12px; }
  .gcols label { display: flex; align-items: center; gap: 4px; padding: 2px 4px; white-space: nowrap; }
  .gmenu button { display: block; width: 100%; }
  .gmenu button, .gcols button { padding: 3px 10px; border: none; border-radius: 2px; text-align: left; cursor: pointer;
    background: transparent; color: inherit; font-family: inherit; font-size: 12px; white-space: nowrap; }
  .gmenu button:hover, .gmenu button:focus, .gcols button:hover { color: var(--vscode-menu-selectionForeground, inherit);
    background: var(--vscode-menu-selectionBackground, var(--vscode-list-activeSelectionBackground)); }
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
  .gt thead th[data-drop="l"] { box-shadow: inset 2px 0 0 var(--vscode-focusBorder); }
  .gt thead th[data-drop="r"] { box-shadow: inset -2px 0 0 var(--vscode-focusBorder); }
  .gt thead tr.gf th { display: flex; gap: 2px; padding: 1px 2px; cursor: default; font-weight: normal; }
  .gt thead tr.gf select { width: 44px; flex: 0 0 auto; font-size: 11px; color: var(--vscode-dropdown-foreground);
    background: var(--vscode-dropdown-background); border: 1px solid var(--vscode-dropdown-border, var(--vscode-panel-border)); }
  .gt thead tr.gf input { flex: 1 1 auto; min-width: 0; padding: 0 4px; font-size: 11px; color: var(--vscode-input-foreground);
    background: var(--vscode-input-background); border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); outline: none; }
  .gt thead tr.gf input:focus { border-color: var(--vscode-focusBorder); }
  .gt thead tr.gf input:disabled { opacity: 0.5; }
  .gt thead tr.gf th[data-op="period"] > input[type="text"], .gt thead tr.gf th:not([data-op="period"]) .gper { display: none; }
  .gt thead tr.gf .gper { display: flex; flex: 1 1 auto; gap: 2px; min-width: 0; }
  .gt thead tr.gf .gper input { flex: 1 1 0; padding: 0 2px; }
  body.vscode-dark .gt thead tr.gf .gper input, body.vscode-high-contrast:not(.vscode-high-contrast-light) .gt thead tr.gf .gper input { color-scheme: dark; }
  .gt .si { margin-left: 4px; font-size: 9px; }
  .gt th[data-sort="asc"] .si::after { content: "\\25B2"; }
  .gt th[data-sort="desc"] .si::after { content: "\\25BC"; }
  .gt .rz { position: absolute; top: 0; right: 0; width: 6px; height: 100%; cursor: col-resize; }
  .gt .rz:hover { background: var(--vscode-sash-hoverBorder, var(--vscode-focusBorder)); }
  .gt .rn { position: sticky; left: 0; z-index: 1; padding: 2px 6px; text-align: right; font-weight: normal;
    font-variant-numeric: tabular-nums; color: var(--vscode-descriptionForeground); user-select: none; cursor: default;
    background: var(--vscode-editorWidget-background, var(--vscode-editor-background)); }
  .gt thead .rn { z-index: 4; padding-right: 6px; }
  .gt .rh { position: absolute; left: 0; bottom: 0; width: 100%; height: 5px; cursor: row-resize; }
  .gt .rh:hover { background: var(--vscode-sash-hoverBorder, var(--vscode-focusBorder)); }
  .gt tbody td { --ov: transparent; background-image: linear-gradient(var(--ov), var(--ov)); }
  .dgrid[data-stripe="1"] .gt tbody tr[data-alt="1"] > td { --ov: var(--g-alt); }
  .gt tbody tr:hover > td { --ov: var(--vscode-list-hoverBackground); }
  .gt tbody tr > td[data-hit="1"] { --ov: var(--g-hit); }
  .gt tbody tr > td[data-hit="2"] { --ov: var(--g-cur); outline: 1px solid var(--vscode-focusBorder); outline-offset: -1px; }
  .gt td.null { color: var(--vscode-descriptionForeground); font-style: italic; }
  .gt td.trunc { text-decoration: underline dotted var(--vscode-descriptionForeground); }
  .gtip { position: absolute; z-index: 6; max-width: 360px; padding: 4px 8px; font-size: 12px; pointer-events: none;
          color: var(--vscode-editorHoverWidget-foreground, var(--vscode-foreground));
          background: var(--vscode-editorHoverWidget-background, var(--vscode-editor-background));
          border: 1px solid var(--vscode-editorHoverWidget-border, var(--vscode-panel-border)); border-radius: 3px; }
  .gt tbody tr[data-fit="1"] > td, .dgrid[data-wrap="1"] .gt tbody tr:not([data-fit="0"]) > td { white-space: pre-wrap;
    overflow-wrap: anywhere; text-overflow: clip; max-height: ${ROW_FIT_MAX_HEIGHT}px; overflow: auto; }
  .gt tbody tr > td[data-ed="1"] { --ov: color-mix(in srgb, var(--vscode-editorGutter-modifiedBackground, #1b81a8) 28%, transparent); }
  .gt tbody tr[data-dirty="1"] > th.rn { color: var(--vscode-editorGutter-modifiedBackground, #1b81a8); font-weight: 600;
    box-shadow: inset 3px 0 0 var(--vscode-editorGutter-modifiedBackground, #1b81a8); }
  .gt tbody tr[data-new="1"] > th.rn { color: var(--vscode-editorGutter-addedBackground, #487e02); font-weight: 600;
    box-shadow: inset 3px 0 0 var(--vscode-editorGutter-addedBackground, #487e02); }
  .gt tbody tr[data-new="1"] > td { --ov: color-mix(in srgb, var(--vscode-editorGutter-addedBackground, #487e02) 14%, transparent); }
  .gt tbody tr[data-del="1"] > th.rn { color: var(--vscode-editorGutter-deletedBackground, #f14c4c); font-weight: 600;
    box-shadow: inset 3px 0 0 var(--vscode-editorGutter-deletedBackground, #f14c4c); }
  .gt tbody tr[data-del="1"] > td { text-decoration: line-through; opacity: 0.55;
    --ov: color-mix(in srgb, var(--vscode-editorGutter-deletedBackground, #f14c4c) 14%, transparent); }
  .gt td.dflt { color: var(--vscode-descriptionForeground); font-style: italic; }
  .gt tbody tr > td[data-err="1"] { outline: 1px solid var(--vscode-inputValidation-errorBorder, var(--vscode-errorForeground)); outline-offset: -1px; }
  .gedit { position: absolute; z-index: 5; box-sizing: border-box; margin: 0; padding: 1px 7px; resize: none; overflow: hidden;
    white-space: pre; line-height: 18px; color: var(--vscode-input-foreground); background: var(--vscode-input-background);
    border: 1px solid var(--vscode-focusBorder); outline: none;
    font-family: var(--vscode-editor-font-family), monospace; font-size: var(--vscode-editor-font-size, 12px); }
  .ghover { position: absolute; z-index: 4; width: 20px; height: 20px; padding: 2px; line-height: 0; cursor: pointer;
    border: 1px solid var(--vscode-widget-border, var(--vscode-panel-border)); border-radius: 3px;
    color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
    background: var(--vscode-button-secondaryBackground, var(--vscode-editorWidget-background)); }
  .ghover:hover { background: var(--vscode-button-secondaryHoverBackground, var(--vscode-list-hoverBackground)); }
`;

/**
 * Functions of gridModel that the client script uses, inlined by name with Function.prototype.toString (see the
 * BUILD NOTE in gridModel). gridScript() is built from this map, so the isolation test covers every one of them.
 */
const INLINED: Readonly<Record<string, (...args: never[]) => unknown>> = {
  compareGridValues, gridSortOrder, tooltipText, cellMatches, countMatches, filterMatches, numericStats,
};
export const INLINED_FUNCTIONS: readonly string[] = Object.keys(INLINED);

/**
 * The grid client script: the inlined gridModel functions, then `initGrids(vscode)`, which wires every `.dgrid` on
 * the page. Pages call initGrids with their acquireVsCodeApi() object.
 */
export function gridScript(): string {
  return `${Object.values(INLINED).map(f => f.toString()).join('\n')}
${GRID_CLIENT}`;
}

const GRID_CLIENT = String.raw`
function initGrids(vscode) {
  var MIN_W = ${MIN_COLUMN_WIDTH}, FIT_MAX = ${FIT_MAX_WIDTH}, INIT_ROWS = ${INITIAL_FIT_ROWS}, INIT_MAX = ${INITIAL_FIT_MAX_WIDTH};
  var MIN_H = 16, SERVER_DEBOUNCE = ${SERVER_FILTER_DEBOUNCE_MS}, LOCAL_DEBOUNCE = 150, PERIOD_W = ${PERIOD_FILTER_WIDTH};
  var ctx = document.createElement('canvas').getContext('2d');
  // One collator for every local text compare (Intl.Collator is much faster than repeated localeCompare).
  var collator = new Intl.Collator();
  var drag = null, dragEnded = 0, frame = 0;
  // The grid the keyboard shortcuts (Ctrl+A, Ctrl+C, Escape) act on: the last one used with the mouse.
  var active = null;
  // The grid whose cell selection is being dragged.
  var selecting = null;
  var apis = [];

  // Drags write at most once per animation frame.
  function applyDrag() {
    frame = 0;
    if (!drag || drag.value === undefined) return;
    if (drag.kind === 'col') drag.set(drag.value);
    else drag.tr.style.height = drag.value + 'px';
  }
  document.addEventListener('mousemove', function (e) {
    if (!drag) return;
    drag.value = drag.kind === 'col'
      ? Math.max(MIN_W, drag.start + e.clientX - drag.x)
      : Math.max(MIN_H, drag.start + e.clientY - drag.y);
    if (!frame) frame = requestAnimationFrame(applyDrag);
  });
  document.addEventListener('mouseup', function () {
    selecting = null;
    if (!drag) return;
    if (frame) { cancelAnimationFrame(frame); applyDrag(); }
    if (drag.done) drag.done();
    drag = null;
    dragEnded = Date.now();
    document.body.style.cursor = '';
  });
  document.addEventListener('mousedown', function (e) {
    for (var i = 0; i < apis.length; i++) apis[i].closePopups(e.target);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      for (var i = 0; i < apis.length; i++) apis[i].closePopups(null);
      if (active) active.clearSelection();
      return;
    }
    var t = e.target;
    var typing = t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA');
    if (!typing && active && active.editKey && active.editKey(e)) { e.preventDefault(); return; }
    if (typing || !active || !(e.ctrlKey || e.metaKey)) return;
    var k = e.key.toLowerCase();
    if (k === 'a') { e.preventDefault(); active.selectAll(); }
    else if (k === 'c' && active.hasSelection()) { e.preventDefault(); active.copySelection(); }
  });

  var grids = document.querySelectorAll('.dgrid');
  for (var g = 0; g < grids.length; g++) apis.push(setUp(grids[g]));

  function intList(s) {
    if (!s) return [];
    return s.split(',').map(Number).filter(function (n) { return Number.isInteger(n) && n >= 0; });
  }

  function setUp(grid) {
    var id = grid.id;
    var scroll = grid.querySelector('.gscroll');
    var dyn = grid.querySelector('style.gdyn');
    var copyBtn = grid.querySelector('.gcopy');
    var viewBtn = grid.querySelector('.gview');
    var rowBtn = grid.querySelector('.growbtn');
    var menu = grid.querySelector('.gmenu');
    var colsPop = grid.querySelector('.gcols');
    var stats = grid.querySelector('.gstats');
    var qInput = grid.querySelector('.gq');
    var qCount = grid.querySelector('.gqn');
    var heads = Array.prototype.slice.call(grid.querySelectorAll('thead tr.gh th[data-c]'));
    var rows = Array.prototype.slice.call(grid.querySelectorAll('tbody tr'));
    var n = heads.length;
    var mode = grid.getAttribute('data-mode');
    var set = grid.getAttribute('data-set');
    var gen = Number(grid.getAttribute('data-gen'));

    // View state (restored from the extension's copy on a re-render).
    var order = intList(grid.getAttribute('data-order'));
    if (order.length !== n) order = heads.map(function (_, i) { return i; });
    var hidden = {};
    intList(grid.getAttribute('data-hidden')).forEach(function (c) { if (c < n) hidden[c] = true; });
    var freeze = Number(grid.getAttribute('data-freeze'));
    if (!Number.isInteger(freeze) || freeze < 0) freeze = 1;
    var wrap = grid.getAttribute('data-wrap') === '1';
    var stripe = grid.getAttribute('data-stripe') !== '0';
    var widths = [];
    var gutter = parseFloat(grid.style.getPropertyValue('--rn')) || 40;

    // Display state: rows / columns in display order, and each index's display position (-1 when not shown).
    var rowOrder = rows.map(function (_, i) { return i; });
    var visibleRow = rows.map(function () { return true; });
    var disp = { rows: [], cols: [], rowPos: [], colPos: [] };
    var anchor = null, focus = null, selRows = [];
    var hits = [], hitCur = -1;
    var hovered = null, hoveredRow = null;
    var menuCtx = null;
    var fonts = {};
    var filterTimer = 0, searchTimer = 0, statsFrame = 0;
    var api = {};

    function cellOf(r, c) { return rows[r].children[c + 1]; }
    function valueOf(td) { return !td || td.getAttribute('data-null') === '1' ? null : td.textContent; }

    function recompute() {
      disp.cols = order.filter(function (c) { return !hidden[c]; });
      disp.rows = rowOrder.filter(function (r) { return visibleRow[r]; });
      disp.colPos = heads.map(function () { return -1; });
      disp.rowPos = rows.map(function () { return -1; });
      disp.cols.forEach(function (c, p) { disp.colPos[c] = p; });
      disp.rows.forEach(function (r, p) { disp.rowPos[r] = p; });
    }

    function restripe() {
      for (var p = 0; p < disp.rows.length; p++) rows[disp.rows[p]].setAttribute('data-alt', p % 2 ? '1' : '0');
    }

    function nth(c) { return '#' + id + ' .gt tr>:nth-child(' + (c + 2) + ')'; }
    function rect() {
      if (!anchor || !focus) return null;
      return { r1: Math.min(anchor.r, focus.r), r2: Math.max(anchor.r, focus.r), c1: Math.min(anchor.c, focus.c), c2: Math.max(anchor.c, focus.c) };
    }

    // Column order, hidden columns, frozen columns and the selected columns, as generated CSS (integers only).
    function restyle() {
      var css = [];
      for (var p = 0; p < order.length; p++) css.push(nth(order[p]) + '{order:' + p + '}');
      for (var c = 0; c < n; c++) if (hidden[c]) css.push(nth(c) + '{display:none}');
      var fz = Math.min(freeze, disp.cols.length);
      var left = gutter;
      for (var f = 0; f < fz; f++) {
        var fc = disp.cols[f];
        css.push(nth(fc) + '{position:sticky;left:' + left + 'px;z-index:1' + (f === fz - 1 ? ';box-shadow:inset -2px 0 0 var(--vscode-panel-border)' : '') + '}');
        css.push('#' + id + ' .gt tbody tr>:nth-child(' + (fc + 2) + '){background-color:var(--g-bg)}');
        css.push('#' + id + ' .gt thead tr>:nth-child(' + (fc + 2) + '){z-index:3}');
        left += widths[fc] || 0;
      }
      var s = rect();
      if (s) for (var q = s.c1; q <= s.c2; q++) css.push('#' + id + ' .gt tbody tr[data-sel="1"]>:nth-child(' + (disp.cols[q] + 2) + '){--ov:var(--g-sel)}');
      dyn.textContent = css.join('\n');
      grid.setAttribute('data-wrap', wrap ? '1' : '0');
      grid.setAttribute('data-stripe', stripe ? '1' : '0');
      setPressed('wrap', wrap);
      setPressed('stripe', stripe);
    }
    function setPressed(act, on) {
      var b = grid.querySelector('.gbar [data-act="' + act + '"]');
      if (b) b.setAttribute('aria-pressed', on ? 'true' : 'false');
    }

    function postView() {
      vscode.postMessage(withSet({
        type: 'viewState', gen: gen,
        view: {
          order: order.slice(), hidden: Object.keys(hidden).map(Number).filter(function (c) { return hidden[c]; }),
          freeze: Math.min(freeze, n), wrap: wrap, stripe: stripe,
          widths: widths.map(function (w) { return Math.max(20, Math.min(4000, Math.round(w || MIN_W))); })
        }
      }));
    }
    function withSet(msg) { if (set !== null) msg.set = Number(set); return msg; }

    function fontOf(el, key) {
      if (fonts[key]) return fonts[key];
      var cs = getComputedStyle(el);
      return (fonts[key] = {
        font: cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily,
        pad: parseFloat(cs.paddingLeft) + parseFloat(cs.paddingRight) + parseFloat(cs.borderLeftWidth) + parseFloat(cs.borderRightWidth)
      });
    }
    function setWidth(c, w) {
      widths[c] = Math.round(w);
      grid.style.setProperty('--c' + c, widths[c] + 'px');
    }
    // Auto-fit: measure every requested column first (style reads and measureText only), then write all widths in
    // one pass, so the layout is not invalidated between measurements.
    function fitColumns(cols, rowLimit, cap) {
      var result = [];
      var limit = Math.min(rows.length, rowLimit);
      for (var k = 0; k < cols.length; k++) {
        var c = cols[k];
        var head = heads[c];
        var hf = fontOf(head, 'th');
        ctx.font = hf.font;
        var current = 'th';
        var w = ctx.measureText(head.querySelector('.hl').textContent).width + hf.pad + 12;
        for (var i = 0; i < limit && w < cap; i++) {
          if (!visibleRow[i]) continue;
          var td = cellOf(i, c);
          if (!td) continue;
          var key = 'td.' + (td.getAttribute('class') || '');
          var f = fontOf(td, key);
          if (current !== key) { ctx.font = f.font; current = key; }
          var text = td.textContent;
          if (text.length > 2000) text = text.slice(0, 2000);
          var tw = ctx.measureText(text.replace(/\s+/g, ' ')).width + f.pad;
          if (tw > w) w = tw;
        }
        var fit = Math.min(cap, Math.max(MIN_W, Math.ceil(w) + 2));
        result.push(isPeriod(c) ? Math.max(fit, PERIOD_W) : fit);
      }
      for (var j = 0; j < cols.length; j++) setWidth(cols[j], result[j]);
    }

    function hideHover() {
      hovered = null;
      hoveredRow = null;
      copyBtn.style.display = 'none';
      viewBtn.style.display = 'none';
      rowBtn.style.display = 'none';
    }

    // --- Selection -------------------------------------------------------------------------------------------------
    function posOf(td) {
      var tr = td.parentNode;
      var r = Number(tr.getAttribute('data-r'));
      var c = Array.prototype.indexOf.call(tr.children, td) - 1;
      return { r: disp.rowPos[r], c: c >= 0 ? disp.colPos[c] : -1 };
    }
    function markSelection() {
      for (var i = 0; i < selRows.length; i++) rows[selRows[i]].setAttribute('data-sel', '0');
      selRows = [];
      var s = rect();
      if (s) for (var p = s.r1; p <= s.r2; p++) { var r = disp.rows[p]; rows[r].setAttribute('data-sel', '1'); selRows.push(r); }
      restyle();
      if (!statsFrame) statsFrame = requestAnimationFrame(updateStats);
    }
    function updateStats() {
      statsFrame = 0;
      var s = rect();
      if (!s) { stats.textContent = ''; return; }
      var count = 0, nums = [];
      for (var p = s.r1; p <= s.r2; p++) {
        var r = disp.rows[p];
        for (var q = s.c1; q <= s.c2; q++) {
          var c = disp.cols[q];
          count++;
          if (heads[c].getAttribute('data-num') === '1') {
            var v = valueOf(cellOf(r, c));
            if (v !== null) nums.push(v);
          }
        }
      }
      var text = 'Count: ' + count;
      var st = nums.length ? numericStats(nums) : undefined;
      if (st) text += ' · Sum: ' + st.sum + ' · Min: ' + st.min + ' · Max: ' + st.max + ' · Avg: ' + st.avg + (st.exact ? '' : ' (approximate)');
      stats.textContent = text;
    }
    function clearSelection() { anchor = null; focus = null; markSelection(); }
    function selectAll() {
      if (!disp.rows.length || !disp.cols.length) return;
      anchor = { r: 0, c: 0 };
      focus = { r: disp.rows.length - 1, c: disp.cols.length - 1 };
      markSelection();
    }
    function copySelection() {
      var s = rect();
      if (!s) return;
      vscode.postMessage(withSet({
        type: 'copySelection', gen: gen, rows: disp.rows.slice(s.r1, s.r2 + 1), cols: disp.cols.slice(s.c1, s.c2 + 1),
        r1: s.r1, c1: s.c1, r2: s.r2, c2: s.c2
      }));
    }

    // --- Local filters and quick search ------------------------------------------------------------------------------
    function filterCells() { return Array.prototype.slice.call(grid.querySelectorAll('thead tr.gf th[data-fc]')); }
    function isPeriod(c) {
      var fc = grid.querySelector('thead tr.gf th[data-fc="' + c + '"]');
      return !!fc && fc.getAttribute('data-op') === 'period';
    }
    function readFilters() {
      return filterCells().map(function (fc) {
        var op = fc.querySelector('select').value;
        var value = fc.querySelector('input[type="text"]').value;
        if (op === 'period') {
          var from = fc.querySelector('input[data-p="from"]').value, to = fc.querySelector('input[data-p="to"]').value;
          value = from || to ? from + '..' + to : '';
        }
        return { col: Number(fc.getAttribute('data-fc')), op: op, value: value };
      }).filter(function (f) { return f.op === 'null' || f.op === 'notnull' || f.value !== ''; });
    }
    function applyLocalFilters() {
      var filters = readFilters();
      for (var r = 0; r < rows.length; r++) {
        var ok = true;
        for (var i = 0; i < filters.length && ok; i++) ok = filterMatches(valueOf(cellOf(r, filters[i].col)), filters[i].op, filters[i].value);
        visibleRow[r] = ok;
        rows[r].style.display = ok ? '' : 'none';
      }
      var note = document.querySelector('[data-filternote="' + id + '"]');
      if (note) note.style.display = filters.length ? '' : 'none';
      afterRowsChanged();
    }
    function postFilters(focusCol) {
      var status = document.getElementById('gstatus');
      if (status) status.textContent = 'Loading…';
      var msg = { type: 'filter', gen: gen, filters: readFilters() };
      if (focusCol !== undefined) msg.focus = focusCol;
      vscode.postMessage(msg);
    }
    function onFilterInput(col, now) {
      clearTimeout(filterTimer);
      if (mode === 'server') filterTimer = setTimeout(function () { postFilters(col); }, now ? 0 : SERVER_DEBOUNCE);
      else filterTimer = setTimeout(applyLocalFilters, now ? 0 : LOCAL_DEBOUNCE);
    }

    function runSearch() {
      for (var i = 0; i < hits.length; i++) hits[i].setAttribute('data-hit', '0');
      hits = [];
      hitCur = -1;
      var q = qInput ? qInput.value : '';
      if (q !== '') {
        for (var p = 0; p < disp.rows.length; p++) {
          for (var k = 0; k < disp.cols.length; k++) {
            var td = cellOf(disp.rows[p], disp.cols[k]);
            if (td && cellMatches(td.textContent, q)) { td.setAttribute('data-hit', '1'); hits.push(td); }
          }
        }
      }
      if (qCount) qCount.textContent = q === '' ? '' : hits.length + (hits.length === 1 ? ' match' : ' matches');
    }
    function stepSearch(delta) {
      if (!hits.length) return;
      if (hitCur >= 0) hits[hitCur].setAttribute('data-hit', '1');
      hitCur = (hitCur + delta + hits.length) % hits.length;
      hits[hitCur].setAttribute('data-hit', '2');
      hits[hitCur].scrollIntoView({ block: 'nearest', inline: 'nearest' });
      if (qCount) qCount.textContent = (hitCur + 1) + ' of ' + hits.length;
    }

    function afterRowsChanged() {
      recompute();
      restripe();
      anchor = null;
      focus = null;
      markSelection();
      runSearch();
    }
    function afterColumnsChanged() {
      recompute();
      anchor = null;
      focus = null;
      markSelection();
      runSearch();
      syncChecklist();
      postView();
    }

    // --- Columns checklist and menus ---------------------------------------------------------------------------------
    function syncChecklist() {
      var boxes = colsPop.querySelectorAll('input[data-col]');
      for (var i = 0; i < boxes.length; i++) boxes[i].checked = !hidden[Number(boxes[i].getAttribute('data-col'))];
    }
    function showAt(el, x, y) {
      el.style.display = 'block';
      var r = el.getBoundingClientRect();
      el.style.left = Math.max(0, Math.min(x, window.innerWidth - r.width - 4)) + 'px';
      el.style.top = Math.max(0, Math.min(y, window.innerHeight - r.height - 4)) + 'px';
    }
    function openMenu(kind, x, y, mctx) {
      menuCtx = mctx;
      var items = menu.querySelectorAll('[data-act]');
      var first = null;
      for (var i = 0; i < items.length; i++) {
        var kinds = (items[i].getAttribute('data-k') || '').split(' ');
        var a = items[i].getAttribute('data-act');
        var show = kinds.indexOf(kind) !== -1 && !(a === 'copysel' && !rect()) && editActionShown(a, mctx);
        items[i].style.display = show ? '' : 'none';
        if (show && !first) first = items[i];
      }
      showAt(menu, x, y);
      if (first) first.focus();
    }
    api.closePopups = function (target) {
      if (target && (menu.contains(target) || colsPop.contains(target))) return;
      if (target && target.closest && target.closest('[data-act="columns"]')) return;
      menu.style.display = 'none';
      colsPop.style.display = 'none';
    };
    menu.addEventListener('keydown', function (e) {
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      e.preventDefault();
      var items = Array.prototype.filter.call(menu.querySelectorAll('[data-act]'), function (b) { return b.style.display !== 'none'; });
      var i = items.indexOf(document.activeElement);
      items[(i + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length].focus();
    });
    menu.addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('[data-act]') : null;
      if (!b) return;
      menu.style.display = 'none';
      act(b.getAttribute('data-act'), menuCtx || {});
    });

    function act(name, m) {
      switch (name) {
        case 'freeze': if (m.col !== undefined && disp.colPos[m.col] >= 0) { freeze = disp.colPos[m.col] + 1; restyle(); postView(); } break;
        case 'unfreeze': freeze = 0; restyle(); postView(); break;
        case 'hide':
          if (m.col !== undefined && disp.cols.length > 1) { hidden[m.col] = true; afterColumnsChanged(); }
          break;
        case 'copysel': copySelection(); break;
        case 'copycell': if (m.row !== undefined) vscode.postMessage(withSet({ type: 'copy', gen: gen, row: m.row, col: m.col })); break;
        case 'rowtsv':
        case 'rowjson':
          if (m.row !== undefined) vscode.postMessage(withSet({ type: 'copyRow', gen: gen, row: m.row, cols: disp.cols.slice(), format: name === 'rowtsv' ? 'tsv' : 'json' }));
          break;
        case 'view': if (m.row !== undefined) vscode.postMessage(withSet({ type: 'openCell', gen: gen, row: m.row, col: m.col })); break;
        case 'selall': selectAll(); break;
        case 'editcell': if (m.row !== undefined) { var etd = cellOf(m.row, m.col); if (etd && canEdit(etd)) startEdit(etd); } break;
        case 'setnull': setNullSelection(m); break;
        case 'delrows': postRows('deleteRows', m); break;
        case 'revert': postRows('revertRows', m); break;
      }
    }

    // --- Editing (Data View) -----------------------------------------------------------------------------------------
    var editFlags = grid.getAttribute('data-edit');
    var loadedRows = Number(grid.getAttribute('data-loaded')) || 0;
    var editor = grid.querySelector('.gedit');
    var editing = null;
    var isDataView = editFlags !== null || grid.hasAttribute('data-ro');
    var tip = grid.querySelector('.gtip');
    var tipTimer = 0;
    function showTip(td, text) {
      if (!tip) return;
      var sr = scroll.getBoundingClientRect(), cr = td.getBoundingClientRect();
      tip.textContent = text;
      tip.style.top = (cr.bottom - sr.top + scroll.scrollTop + 2) + 'px';
      tip.style.left = (cr.left - sr.left + scroll.scrollLeft) + 'px';
      tip.style.display = 'block';
      clearTimeout(tipTimer);
      tipTimer = setTimeout(function () { tip.style.display = 'none'; }, 4000);
    }
    /** Why a Data View cell is not editable (the double-click hint). */
    function whyNotEditable(td) {
      if (editFlags === null) return 'Read-only: ' + (grid.getAttribute('data-ro') || 'this view cannot be edited.');
      var tr = td.parentNode;
      var c = Array.prototype.indexOf.call(tr.children, td) - 1;
      if (tr.getAttribute('data-del') === '1') return 'This row is marked for deletion (right-click to revert).';
      if (td.getAttribute('data-auto') === '1') return 'Identity or computed column: filled in by the server.';
      if ((td.getAttribute('class') || '').indexOf('trunc') !== -1) return 'The value is too long to edit here (it was cut for display). Use the viewer button to read it.';
      if (c >= 0 && heads[c].getAttribute('data-w') !== '1') return 'This column cannot be edited here (identity, computed, or a type such as timestamp / binary / spatial / CLR).';
      if (tr.getAttribute('data-new') !== '1' && editFlags.indexOf('u') === -1) return 'No primary key: existing rows cannot be changed (new rows can be added).';
      return 'This cell cannot be edited.';
    }
    function canEdit(td) {
      if (editFlags === null || !td || td.tagName !== 'TD') return false;
      var tr = td.parentNode;
      var c = Array.prototype.indexOf.call(tr.children, td) - 1;
      if (c < 0 || heads[c].getAttribute('data-w') !== '1' || td.getAttribute('data-auto') === '1') return false;
      if (tr.getAttribute('data-del') === '1' || (td.getAttribute('class') || '').indexOf('trunc') !== -1) return false;
      return tr.getAttribute('data-new') === '1' ? editFlags.indexOf('i') !== -1 : editFlags.indexOf('u') !== -1;
    }
    function rowsOfContext(m) {
      var s = rect(), list = [];
      if (s && m.row !== undefined && disp.rowPos[m.row] >= s.r1 && disp.rowPos[m.row] <= s.r2) {
        for (var p = s.r1; p <= s.r2; p++) list.push(disp.rows[p]);
      } else if (m.row !== undefined) list.push(m.row);
      return list;
    }
    function editActionShown(a, m) {
      if (a !== 'editcell' && a !== 'setnull' && a !== 'delrows' && a !== 'revert') return true;
      if (editFlags === null || !m || m.row === undefined) return false;
      if (a === 'editcell') return canEdit(cellOf(m.row, m.col));
      if (a === 'setnull') return canEdit(cellOf(m.row, m.col)) && heads[m.col].getAttribute('data-w') === '1';
      var list = rowsOfContext(m);
      if (a === 'delrows') return list.some(function (r) { return r >= loadedRows ? true : editFlags.indexOf('d') !== -1 && rows[r].getAttribute('data-del') !== '1'; });
      return list.some(function (r) { var tr = rows[r]; return r >= loadedRows || tr.getAttribute('data-dirty') === '1' || tr.getAttribute('data-del') === '1'; });
    }
    function scrollPos() { return [Math.round(scroll.scrollTop), Math.round(scroll.scrollLeft)]; }
    function postRows(type, m) {
      var list = rowsOfContext(m);
      if (list.length) vscode.postMessage({ type: type, gen: gen, rows: list, scroll: scrollPos() });
    }
    function postEdit(td, value) {
      var tr = td.parentNode;
      var r = Number(tr.getAttribute('data-r')), c = Array.prototype.indexOf.call(tr.children, td) - 1;
      vscode.postMessage({ type: 'edit', gen: gen, row: r, col: c, value: value });
      td.removeAttribute('title');
      td.removeAttribute('data-err');
      td.removeAttribute('data-dflt');
      td.setAttribute('data-ed', '1');
      if (value === null) { td.textContent = 'NULL'; td.setAttribute('class', 'null'); td.setAttribute('data-null', '1'); }
      else { td.textContent = value; td.removeAttribute('class'); td.removeAttribute('data-null'); }
      if (tr.getAttribute('data-new') !== '1') tr.setAttribute('data-dirty', '1');
    }
    function setNullSelection(m) {
      var s = rect(), cells = [];
      if (s && m.row !== undefined && disp.rowPos[m.row] >= s.r1 && disp.rowPos[m.row] <= s.r2) {
        for (var p = s.r1; p <= s.r2; p++) for (var q = s.c1; q <= s.c2; q++) cells.push(cellOf(disp.rows[p], disp.cols[q]));
      } else if (m.row !== undefined) cells.push(cellOf(m.row, m.col));
      cells.forEach(function (td) { if (canEdit(td) && td.getAttribute('data-null') !== '1') postEdit(td, null); });
    }
    function startEdit(td, typed) {
      if (!editor || !canEdit(td)) return;
      finishEdit(true);
      hideHover();
      var sr = scroll.getBoundingClientRect(), cr = td.getBoundingClientRect();
      editing = td;
      editor.style.top = (cr.top - sr.top + scroll.scrollTop) + 'px';
      editor.style.left = (cr.left - sr.left + scroll.scrollLeft) + 'px';
      editor.style.width = Math.max(cr.width, 140) + 'px';
      editor.style.height = Math.max(cr.height, 22) + 'px';
      var blank = td.getAttribute('data-null') === '1' || td.getAttribute('data-dflt') === '1';
      editor.value = typed !== undefined ? typed : blank ? '' : td.textContent;
      editor.setAttribute('data-blank', blank ? '1' : '0');
      editor.setAttribute('data-orig', typed !== undefined ? '\u0000' : editor.value);
      editor.style.display = 'block';
      editor.focus();
      editor.setSelectionRange(editor.value.length, editor.value.length);
    }
    function finishEdit(save) {
      if (!editing) return;
      var td = editing;
      editing = null;
      editor.style.display = 'none';
      if (!save) return;
      var v = editor.value;
      if (v === editor.getAttribute('data-orig')) return;
      if (v === '' && editor.getAttribute('data-blank') === '1') return;
      postEdit(td, v);
    }
    function moveEdit(td, dr, dc) {
      var p = posOf(td);
      var r = p.r + dr, c = p.c + dc;
      while (r >= 0 && r < disp.rows.length && c >= 0 && c < disp.cols.length) {
        var next = cellOf(disp.rows[r], disp.cols[c]);
        if (canEdit(next)) { anchor = { r: r, c: c }; focus = anchor; markSelection(); next.scrollIntoView({ block: 'nearest', inline: 'nearest' }); startEdit(next); return; }
        if (dc) c += dc; else r += dr;
      }
      anchor = { r: Math.max(0, Math.min(disp.rows.length - 1, p.r + dr)), c: p.c }; focus = anchor; markSelection();
    }
    if (editor) {
      editor.addEventListener('keydown', function (e) {
        e.stopPropagation();
        var td = editing;
        if (e.key === 'Escape') { e.preventDefault(); finishEdit(false); return; }
        if (e.key === 'Enter' && e.altKey) {
          e.preventDefault();
          var s0 = editor.selectionStart, s1 = editor.selectionEnd;
          editor.value = editor.value.slice(0, s0) + '\n' + editor.value.slice(s1);
          editor.setSelectionRange(s0 + 1, s0 + 1);
          return;
        }
        if (e.key === 'Enter' || e.key === 'Tab') {
          e.preventDefault();
          finishEdit(true);
          if (td) moveEdit(td, e.key === 'Enter' ? (e.shiftKey ? -1 : 1) : 0, e.key === 'Tab' ? (e.shiftKey ? -1 : 1) : 0);
        }
      });
      editor.addEventListener('blur', function () { finishEdit(true); });
      editor.addEventListener('mousedown', function (e) { e.stopPropagation(); });
    }
    api.editKey = function (e) {
      if (editFlags === null || editing) return false;
      var s = rect();
      if (!s || s.r1 !== s.r2 || s.c1 !== s.c2) return false;
      var td = cellOf(disp.rows[s.r1], disp.cols[s.c1]);
      if (!canEdit(td)) return false;
      if (e.key === 'F2' || e.key === 'Enter') { startEdit(td); return true; }
      if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) { startEdit(td, e.key); return true; }
      return false;
    };

    // --- Toolbar -----------------------------------------------------------------------------------------------------
    grid.querySelector('.gbar').addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('[data-act]') : null;
      if (!b) return;
      active = api;
      switch (b.getAttribute('data-act')) {
        case 'columns': {
          if (colsPop.style.display === 'block') { colsPop.style.display = 'none'; break; }
          syncChecklist();
          var r = b.getBoundingClientRect();
          showAt(colsPop, r.left, r.bottom + 2);
          var firstBox = colsPop.querySelector('input');
          if (firstBox) firstBox.focus();
          break;
        }
        case 'wrap': wrap = !wrap; restyle(); postView(); break;
        case 'stripe': stripe = !stripe; restyle(); postView(); break;
        case 'prev': stepSearch(-1); break;
        case 'next': stepSearch(1); break;
        case 'export': vscode.postMessage(withSet({ type: 'export', gen: gen, rows: disp.rows.slice(), cols: disp.cols.slice() })); break;
      }
    });
    if (qInput) {
      qInput.addEventListener('input', function () { clearTimeout(searchTimer); searchTimer = setTimeout(runSearch, 150); });
      qInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); clearTimeout(searchTimer); if (hitCur < 0 && !hits.length) runSearch(); stepSearch(e.shiftKey ? -1 : 1); }
      });
    }
    colsPop.addEventListener('click', function (e) {
      var t = e.target;
      if (t.getAttribute && t.getAttribute('data-act') === 'showall') { hidden = {}; afterColumnsChanged(); restyle(); return; }
      if (!t.getAttribute || t.getAttribute('data-col') === null) return;
      var c = Number(t.getAttribute('data-col'));
      // At least one column stays visible.
      if (!t.checked && disp.cols.length <= 1) { e.preventDefault(); return; }
      if (t.checked) delete hidden[c]; else hidden[c] = true;
      afterColumnsChanged();
      restyle();
    });

    // --- Filter row --------------------------------------------------------------------------------------------------
    filterCells().forEach(function (fc) {
      var col = Number(fc.getAttribute('data-fc'));
      var input = fc.querySelector('input[type="text"]');
      var select = fc.querySelector('select');
      input.addEventListener('input', function () { onFilterInput(col, false); });
      input.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); onFilterInput(col, true); } });
      Array.prototype.forEach.call(fc.querySelectorAll('.gper input'), function (d) {
        d.addEventListener('change', function () { onFilterInput(col, true); });
        d.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); onFilterInput(col, true); } });
      });
      select.addEventListener('change', function () {
        var nullOp = select.value === 'null' || select.value === 'notnull';
        if (nullOp || fc.getAttribute('data-textless') === '1') input.setAttribute('disabled', '');
        else input.removeAttribute('disabled');
        fc.setAttribute('data-op', select.value);
        if (select.value === 'period' && (widths[col] || 0) < PERIOD_W) { setWidth(col, PERIOD_W); restyle(); postView(); }
        onFilterInput(col, true);
      });
    });

    // --- Resize, fit, sort, reorder ----------------------------------------------------------------------------------
    grid.addEventListener('mousedown', function (e) {
      var t = e.target, cls = t.getAttribute && t.getAttribute('class');
      active = api;
      if (cls === 'rz') {
        var c = Number(t.parentNode.getAttribute('data-c'));
        drag = { kind: 'col', x: e.clientX, start: heads[c].getBoundingClientRect().width,
          set: function (w) { setWidth(c, w); if (disp.colPos[c] >= 0 && disp.colPos[c] < freeze) restyle(); }, done: postView };
        document.body.style.cursor = 'col-resize';
      } else if (cls === 'rh') {
        var tr = t.parentNode.parentNode;
        drag = { kind: 'row', y: e.clientY, start: tr.getBoundingClientRect().height, tr: tr };
        document.body.style.cursor = 'row-resize';
      } else return;
      e.preventDefault();
      hideHover();
    });

    grid.addEventListener('dblclick', function (e) {
      var t = e.target, cls = t.getAttribute && t.getAttribute('class');
      if (cls === 'rz') {
        fitColumns([Number(t.parentNode.getAttribute('data-c'))], rows.length, FIT_MAX);
        restyle();
        postView();
      } else if (cls === 'rh') {
        // Fit the row to its content (wrapped, capped); a second double-click returns it to one line.
        var tr = t.parentNode.parentNode;
        var fit = tr.getAttribute('data-fit');
        var wrapped = fit === '1' || (wrap && fit !== '0');
        tr.setAttribute('data-fit', wrapped ? '0' : '1');
        tr.style.height = '';
      } else {
        var td = t.closest ? t.closest('tbody td') : null;
        if (!td) return;
        if (canEdit(td)) { e.preventDefault(); startEdit(td); return; }
        // A Data View edits in place: a cell that cannot be edited says why instead of opening a viewer tab (the
        // hover button still opens one). The Results grid, which never edits, keeps opening the viewer.
        if (isDataView) { e.preventDefault(); hideHover(); showTip(td, whyNotEditable(td)); return; }
        var p = td.parentNode;
        vscode.postMessage(withSet({ type: 'openCell', gen: gen, row: Number(p.getAttribute('data-r')), col: Array.prototype.indexOf.call(p.children, td) - 1 }));
      }
      e.preventDefault();
      hideHover();
    });

    var headRow = grid.querySelector('thead tr.gh');
    headRow.addEventListener('click', function (e) {
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
        vscode.postMessage({ type: 'sort', gen: gen, col: col, dir: next || 'none' });
        return;
      }
      for (var h = 0; h < heads.length; h++) heads[h].setAttribute('data-sort', heads[h] === th ? next : '');
      if (!next) {
        rowOrder = rows.map(function (_, i) { return i; });
        for (var i = 0; i < rows.length; i++) rows[i].style.order = '';
      } else {
        var values = rows.map(function (_, r) { return valueOf(cellOf(r, col)); });
        rowOrder = gridSortOrder(values, th.getAttribute('data-num') === '1', next, collator.compare);
        for (var p = 0; p < rowOrder.length; p++) rows[rowOrder[p]].style.order = String(p);
      }
      var note = document.querySelector('[data-sortnote="' + id + '"]');
      if (note) note.style.display = next ? '' : 'none';
      afterRowsChanged();
      hideHover();
    });

    var dragCol = null;
    headRow.addEventListener('dragstart', function (e) {
      var th = e.target.closest ? e.target.closest('th[data-c]') : null;
      if (drag || !th) { e.preventDefault(); return; }
      dragCol = Number(th.getAttribute('data-c'));
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', String(dragCol));
    });
    headRow.addEventListener('dragover', function (e) {
      var th = e.target.closest ? e.target.closest('th[data-c]') : null;
      if (dragCol === null || !th) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      var r = th.getBoundingClientRect();
      for (var h = 0; h < heads.length; h++) heads[h].setAttribute('data-drop', '');
      th.setAttribute('data-drop', e.clientX < r.left + r.width / 2 ? 'l' : 'r');
    });
    function endDrop() { dragCol = null; for (var h = 0; h < heads.length; h++) heads[h].setAttribute('data-drop', ''); }
    headRow.addEventListener('dragend', endDrop);
    headRow.addEventListener('drop', function (e) {
      var th = e.target.closest ? e.target.closest('th[data-c]') : null;
      if (dragCol === null || !th) { endDrop(); return; }
      e.preventDefault();
      var target = Number(th.getAttribute('data-c'));
      var after = th.getAttribute('data-drop') === 'r';
      var from = dragCol;
      endDrop();
      if (target === from) return;
      order = order.filter(function (c) { return c !== from; });
      var at = order.indexOf(target) + (after ? 1 : 0);
      order.splice(at, 0, from);
      restyle();
      afterColumnsChanged();
    });

    // --- Context menu, hover buttons, cell selection -----------------------------------------------------------------
    grid.addEventListener('contextmenu', function (e) {
      var t = e.target;
      if (!t.closest) return;
      var th = t.closest('thead tr.gh th[data-c]');
      var td = t.closest('tbody td');
      var rn = t.closest('tbody th.rn');
      active = api;
      if (th) openMenu('head', e.clientX, e.clientY, { col: Number(th.getAttribute('data-c')) });
      else if (td) {
        var tr = td.parentNode;
        var r = Number(tr.getAttribute('data-r'));
        var c = Array.prototype.indexOf.call(tr.children, td) - 1;
        var p = posOf(td), s = rect();
        if (!s || p.r < s.r1 || p.r > s.r2 || p.c < s.c1 || p.c > s.c2) { anchor = p; focus = p; markSelection(); }
        openMenu('cell', e.clientX, e.clientY, { row: r, col: c });
      } else if (rn) openMenu('row', e.clientX, e.clientY, { row: Number(rn.parentNode.getAttribute('data-r')) });
      else return;
      e.preventDefault();
    });

    var tbody = grid.querySelector('tbody');
    tbody.addEventListener('mousedown', function (e) {
      if (e.button !== 0 || !e.target.closest) return;
      var cls = e.target.getAttribute && e.target.getAttribute('class');
      if (cls === 'rh') return;
      var td = e.target.closest('td');
      var rn = e.target.closest('th.rn');
      if (td) {
        var p = posOf(td);
        if (p.r < 0 || p.c < 0) return;
        if (e.shiftKey && anchor) focus = p; else { anchor = p; focus = p; }
      } else if (rn) {
        // A row number selects the whole row (Shift extends).
        var r = disp.rowPos[Number(rn.parentNode.getAttribute('data-r'))];
        if (r < 0 || !disp.cols.length) return;
        if (e.shiftKey && anchor) focus = { r: r, c: disp.cols.length - 1 };
        else { anchor = { r: r, c: 0 }; focus = { r: r, c: disp.cols.length - 1 }; }
      } else return;
      active = api;
      selecting = api;
      markSelection();
      e.preventDefault();
    });

    tbody.addEventListener('mouseover', function (e) {
      if (drag || !e.target.closest) return;
      var td = e.target.closest('td');
      if (selecting === api && td) {
        var p = posOf(td);
        if (p.r >= 0 && p.c >= 0 && (!focus || p.r !== focus.r || p.c !== focus.c)) { focus = p; markSelection(); }
      }
      var sr = scroll.getBoundingClientRect();
      var rn = e.target.closest('th.rn');
      if (rn && rn !== hoveredRow) {
        hoveredRow = rn;
        var rr = rn.getBoundingClientRect();
        rowBtn.style.top = (rr.top - sr.top + scroll.scrollTop + 1) + 'px';
        rowBtn.style.left = (rr.left - sr.left + scroll.scrollLeft + 1) + 'px';
        rowBtn.setAttribute('data-r', rn.parentNode.getAttribute('data-r'));
        rowBtn.style.display = 'block';
      }
      if (!td || td === hovered) return;
      hovered = td;
      // Lazy tooltip: the full value (capped) from the cell text, set on first hover. NULL cells get none.
      if (!td.hasAttribute('title') && td.getAttribute('data-null') !== '1') td.setAttribute('title', tooltipText(td.textContent));
      var tr = td.parentNode;
      var cr = td.getBoundingClientRect();
      var top = (cr.top - sr.top + scroll.scrollTop + 1) + 'px';
      copyBtn.style.top = top;
      viewBtn.style.top = top;
      copyBtn.style.left = (cr.right - sr.left + scroll.scrollLeft - 23) + 'px';
      viewBtn.style.left = (cr.right - sr.left + scroll.scrollLeft - 45) + 'px';
      var r = tr.getAttribute('data-r'), c = String(Array.prototype.indexOf.call(tr.children, td) - 1);
      copyBtn.setAttribute('data-r', r);
      copyBtn.setAttribute('data-c', c);
      viewBtn.setAttribute('data-r', r);
      viewBtn.setAttribute('data-c', c);
      copyBtn.style.display = 'block';
      viewBtn.style.display = 'block';
    });
    scroll.addEventListener('mouseleave', hideHover);
    scroll.addEventListener('scroll', hideHover);
    function hoverPost(btn, type) {
      btn.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        vscode.postMessage(withSet({ type: type, gen: gen, row: Number(btn.getAttribute('data-r')), col: Number(btn.getAttribute('data-c')) }));
      });
      btn.addEventListener('mousedown', function (e) { e.stopPropagation(); });
    }
    hoverPost(copyBtn, 'copy');
    hoverPost(viewBtn, 'openCell');
    rowBtn.addEventListener('mousedown', function (e) { e.stopPropagation(); });
    rowBtn.addEventListener('click', function (e) {
      e.preventDefault();
      e.stopPropagation();
      var r = rowBtn.getBoundingClientRect();
      openMenu('row', r.right, r.top, { row: Number(rowBtn.getAttribute('data-r')) });
    });

    api.clearSelection = clearSelection;
    api.selectAll = selectAll;
    api.copySelection = copySelection;
    api.hasSelection = function () { return !!rect(); };

    // --- Initial state -----------------------------------------------------------------------------------------------
    recompute();
    var saved = intList(grid.getAttribute('data-widths'));
    if (saved.length === n) for (var c0 = 0; c0 < n; c0++) setWidth(c0, saved[c0]);
    else fitColumns(heads.map(function (_, c) { return c; }), INIT_ROWS, INIT_MAX);
    restripe();
    restyle();
    syncChecklist();
    var sc = intList(grid.getAttribute('data-scroll'));
    if (sc.length === 2) { scroll.scrollTop = sc[0]; scroll.scrollLeft = sc[1]; }
    var fcs = grid.getAttribute('data-focus');
    if (fcs !== null && fcs !== '') {
      var fi = grid.querySelector('thead tr.gf input[data-c="' + Number(fcs) + '"]');
      if (fi) { fi.focus(); fi.setSelectionRange(fi.value.length, fi.value.length); }
    }
    return api;
  }
}
`;

// --- Data View page --------------------------------------------------------------------------------------------

export interface DataViewModel {
  /** `schema.name` of the table or view. */
  objectName: string;
  connection: string;
  /** The connection's color: the connection name in the header is drawn in it. */
  connectionColor?: ConnectionColor;
  /** The TOP the rows were (or are being) queried with. */
  top: number;
  loading?: boolean;
  /** Shown above the grid (or instead of it when there is no result yet). */
  error?: string;
  result?: { columns: readonly GridColumn[]; rows: readonly (readonly unknown[])[]; truncated: boolean };
  /** Server-side sort of `result` (column index). */
  sort?: { col: number; dir: SortDir };
  /** Server-side filters of `result`. */
  filters?: readonly GridFilterInput[];
  /** Whether Load more is offered. */
  canLoadMore?: boolean;
  /** Server warnings (e.g. a response cap) and sort / paging notes, shown under the bar. */
  notes?: readonly string[];
  /** Render counter (`data-gen` of the grid). */
  gen: number;
  view?: GridViewState;
  focus?: number;
  scroll?: readonly [number, number];
  /** Editing state (omitted when the view cannot be edited). */
  edit?: GridEditSpec;
  /** Number of rows with unsaved changes. */
  pending?: number;
  /** Why the view is read-only (shown next to the row count). */
  readOnlyReason?: string;
}

const pendingText = (n: number): string => (n ? `${n} unsaved row change${n === 1 ? '' : 's'}` : '');

/** The Data View webview document. */
export function renderDataView(model: DataViewModel, nonce: string): string {
  const { result } = model;
  let meta = '';
  let content = '';
  const error = model.error !== undefined ? `<p class="error">${escapeHtml(model.error)}</p>` : '';
  const extra = `<label class="topl" for="top">TOP <input id="top" type="number" min="${MIN_TOP}" max="${MAX_TOP}" step="1" value="${model.top}" aria-label="Rows to load (TOP)"></label>`
    + '<button type="button" class="tb" id="reload" title="Load the rows again with this TOP, the filters and the current sort"><span>Reload</span></button>'
    + `<button type="button" class="tb" id="more" title="Load the next rows with the same filters and sort"${model.canLoadMore ? '' : ' style="display:none"'}><span>Load more</span></button>`
    + (model.edit?.insert ? '<button type="button" class="tb" id="gadd" title="Add a new row (saved with Save)"><span>+ Add row</span></button>' : '')
    + (model.edit
      ? `<span class="pend" id="gpend">${escapeHtml(pendingText(model.pending ?? 0))}</span>`
        + `<button type="button" class="tb save" id="gsave" title="Save all changes in one transaction"${model.pending ? '' : ' style="display:none"'}><span>Save</span></button>`
        + `<button type="button" class="tb" id="gdiscard" title="Discard all unsaved changes"${model.pending ? '' : ' style="display:none"'}><span>Discard</span></button>`
      : '')
    + '<span id="toperr" role="alert"></span>'
    + `<span class="meta" id="gstatus">${model.loading ? 'Loading…' : ''}</span>`;
  if (result) {
    const cols = result.columns.length;
    const mode = model.edit ? (model.edit.update ? 'editable' : 'add rows only') : 'read-only';
    meta = `${rowCountLabel(result.rows.length, result.truncated)} · ${cols} column${cols === 1 ? '' : 's'} · ${mode}`;
    content = renderGrid({
      id: 'gd', columns: result.columns, rows: result.rows, sortMode: 'server', sort: model.sort, filters: model.filters, gen: model.gen,
      view: model.view, focus: model.focus, scroll: model.scroll, toolbarExtra: extra,
      ...(model.edit ? { edit: model.edit } : { readOnlyReason: model.readOnlyReason ?? 'this view cannot be edited.' }),
    }) + (result.rows.length === 0 ? '<p class="hint">No rows.</p>' : '');
  } else if (model.error === undefined) {
    content = '<p class="hint">Loading…</p>';
  } else {
    // No result to show: keep TOP and Reload so the query can be retried.
    content = `<div class="gbar">${extra}</div>`;
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
  body { margin: 0; padding: 0 12px; display: flex; flex-direction: column; overflow: hidden;
         font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
         color: var(--vscode-foreground); background: var(--vscode-editor-background); }
  .bar { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; padding: 8px 0 4px;
         border-bottom: 1px solid var(--vscode-panel-border); }
  .name { font-weight: 600; }
  .meta, .note, .hint { color: var(--vscode-descriptionForeground); font-size: 12px; }
  .note { padding: 3px 0; }
  .hint { padding: 4px 0; margin: 0; }
  .error { padding: 6px 0; margin: 0; color: var(--vscode-errorForeground); white-space: pre-wrap; }
  .topl { display: flex; align-items: center; gap: 4px; font-size: 12px; }
  #top { width: 64px; }
  #top[aria-invalid="true"] { border-color: var(--vscode-inputValidation-errorBorder, var(--vscode-errorForeground)); }
  #toperr { color: var(--vscode-errorForeground); font-size: 12px; }
  .dgrid { flex: 1; }
  .pend { color: var(--vscode-editorGutter-modifiedBackground, #1b81a8); font-size: 12px; font-weight: 600; }
  .gbar .tb.save { color: var(--vscode-button-foreground); background: var(--vscode-button-background); }
  .gbar .tb.save:hover { background: var(--vscode-button-hoverBackground); }
  .meta.conn { color: var(--conn); font-weight: 600; }
  .meta.conn::before { content: ""; display: inline-block; width: 8px; height: 8px; margin-right: 5px; border-radius: 50%; background: var(--conn); }
${GRID_CSS}
</style>
</head>
<body data-gen="${Math.floor(model.gen)}">
  <div class="bar">
    <span class="name">${escapeHtml(model.objectName)}</span>
    <span class="meta${model.connectionColor ? ' conn' : ''}" title="Connection"${model.connectionColor ? ` style="--conn: var(--vscode-charts-${model.connectionColor})"` : ''}>${escapeHtml(model.connection)}</span>
    <span class="meta"${model.readOnlyReason || model.edit ? ` title="${escapeHtml(model.readOnlyReason ?? 'Double-click, F2 or type to edit a cell; right-click for NULL, delete and revert. Changes are saved with Save.')}"` : ''}>${escapeHtml(meta)}</span>
  </div>
  ${error}
  ${notes}
  ${content}
<script nonce="${nonce}">
${gridScript()}
(function () {
  var vscode = acquireVsCodeApi();
  initGrids(vscode);
  var top = document.getElementById('top');
  var err = document.getElementById('toperr');
  if (!top) return;
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
    var msg = { type: 'reload', gen: Number(document.body.getAttribute('data-gen')), top: n };
    var sc = document.querySelector('#gd .gscroll');
    if (sc) msg.scroll = [Math.round(sc.scrollTop), Math.round(sc.scrollLeft)];
    vscode.postMessage(msg);
  }
  document.getElementById('reload').addEventListener('click', reload);
  top.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); reload(); } });
  var grid = document.getElementById('gd');
  function scrollPos() { var sc = grid && grid.querySelector('.gscroll'); return sc ? [Math.round(sc.scrollTop), Math.round(sc.scrollLeft)] : [0, 0]; }
  function gridGen() { return Number((grid || document.body).getAttribute('data-gen')); }
  [['gadd', 'addRow'], ['gsave', 'save'], ['gdiscard', 'discard']].forEach(function (b) {
    var el = document.getElementById(b[0]);
    if (el) el.addEventListener('click', function () {
      if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
      vscode.postMessage({ type: b[1], gen: gridGen(), scroll: scrollPos() });
    });
  });
  window.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || m.type !== 'pending') return;
    var pend = document.getElementById('gpend');
    if (pend) pend.textContent = m.count ? m.count + ' unsaved row change' + (m.count === 1 ? '' : 's') : '';
    ['gsave', 'gdiscard'].forEach(function (id) { var el = document.getElementById(id); if (el) el.style.display = m.count ? '' : 'none'; });
    if (grid && typeof m.row === 'number' && typeof m.col === 'number') {
      var tr = grid.querySelector('tbody tr[data-r="' + m.row + '"]');
      var td = tr && tr.children[m.col + 1];
      if (td) {
        if (m.error) { td.setAttribute('data-err', '1'); td.setAttribute('title', String(m.error)); }
        else { td.removeAttribute('data-err'); td.removeAttribute('title'); }
        if (m.reverted) { td.removeAttribute('data-ed'); if (!tr.querySelector('td[data-ed="1"]')) tr.removeAttribute('data-dirty'); }
      }
    }
  });
  var more = document.getElementById('more');
  if (more && grid) {
    more.addEventListener('click', function () {
      var sc = grid.querySelector('.gscroll');
      more.setAttribute('disabled', '');
      document.getElementById('gstatus').textContent = 'Loading…';
      vscode.postMessage({ type: 'loadMore', gen: Number(grid.getAttribute('data-gen')), scroll: [Math.round(sc.scrollTop), Math.round(sc.scrollLeft)] });
    });
  }
})();
</script>
</body>
</html>`;
}
