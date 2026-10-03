import * as vscode from 'vscode';
import type { ServerProcessClient } from './client/serverProcessClient';
import type { ConnectionColor } from './connections/profile';
import type { ObjectRef } from './explorer/catalog';
import type { ExplorerClient } from './explorer/explorerClient';
import { qualified } from './explorer/sqlText';
import type { CellViewer } from './grid/cellViewer';
import {
  CellProblem, ColumnMeta, EditCapabilities, PendingChanges, applyEdit, buildSaveScript, columnMetaSql, deleteRows, editCapabilities, effectiveRows,
  emptyPending, parseColumnMeta, parseEditMessage, pendingCount, pendingProblems, revertRows, validateValue, writableColumns,
} from './grid/editModel';
import { runGridAction } from './grid/gridActions';
import { renderDataView } from './grid/gridHtml';
import {
  DataViewFilter, DataViewOrder, GridColumn, GridViewState, LOAD_CAP_NOTE, LoadedWith, MAX_LOADED_ROWS, MAX_TOP, dataViewSql, displayedParams,
  errorNamesKey, isSortableType, nextPageRequest, pagingNote, parseDataViewMessage, parsePrimaryKey, reconcileFilters, reconcileSort,
  sortIndicator, stripRowNumber, usablePrimaryKey,
} from './grid/gridModel';
import type { Logger } from './logger';
import { RunScriptResultSet, parseRunScriptResult } from './query/runScript';
import { makeNonce } from './webviewUtil';

/** The loaded rows of a Data View (first page plus Load more pages). */
interface Loaded { columns: GridColumn[]; rows: unknown[][]; truncated: boolean }

/** What a Data View panel shows; kept until the panel is reused for another object (or closed). */
interface DataViewSession {
  ref: ObjectRef;
  title: string;
  /** Rows per query (first page and each Load more page). */
  top: number;
  /** Server-side sort as set by the user: column name and type (from the result it was clicked on) and direction. */
  sort?: DataViewOrder;
  filters: DataViewFilter[];
  /** Primary-key columns (tie-breakers for ORDER BY and paging); [] when unknown or not usable. */
  pk: string[];
  result?: Loaded;
  /** The parameters `result` was queried with; Load more pages only with these, and a failed query restores them. */
  loadedWith?: LoadedWith;
  /** More rows exist beyond the loaded ones (and the load cap is not reached). */
  more: boolean;
  capped: boolean;
  notes: string[];
  error?: string;
  /** The grid view (column order, hidden, frozen, ...) and the column names it belongs to. */
  view?: GridViewState;
  viewKey?: string;
  /** One-shot for the next render: the filter input to refocus, and the scroll position to restore. */
  focus?: number;
  scroll?: [number, number];
  /** Number of the latest query; an older query's answer is dropped. */
  seq: number;
  /** Column facts for editing (tables on read-write connections); undefined when not read. */
  meta?: ColumnMeta[];
  pending: PendingChanges;
  /** Pending values that failed the pre-save check, shown on their cells. */
  problems: CellProblem[];
  /** A save is running: edits are refused meanwhile. */
  saving?: boolean;
}

export interface DataViewDeps {
  runner: ServerProcessClient;
  explorer: ExplorerClient;
  viewer: CellViewer;
  log: Logger;
  /** The connection's color (tab icon and header accent), when it has one. */
  colorOf?: (connection: string) => ConnectionColor | undefined;
  /** Whether the connection is read-only (no editing); unknown connections count as read-only. */
  isReadOnly?: (connection: string) => boolean;
  extensionUri?: vscode.Uri;
}

export interface DataViewOptions {
  /**
   * Show the object in the last active Data View panel instead of a new one ("Replace Current Tab"). By default a
   * new panel opens, unless one already shows this object: that one is revealed.
   */
  replace?: boolean;
}

/** One run_script of a Data View query: the result set without the paging column, or the error text. */
interface QueryOutcome { result?: RunScriptResultSet; notes: string[]; error?: string }

/** One Data View webview panel and the object it shows. */
interface DataView {
  panel: vscode.WebviewPanel;
  session?: DataViewSession;
  /** Render counter of the page on screen (`data-gen`); grid messages from an older page are ignored. */
  gen: number;
}

/** The last active Data View panel (the one "Replace Current Tab" reuses). */
let lastActive: DataView | undefined;
const views = new Set<DataView>();
let deps: DataViewDeps | undefined;
/** Primary keys per object (connection, schema, name), fetched once through the read-only explorer. */
const pkCache = new Map<string, string[]>();

const columnsKey = (columns: readonly GridColumn[]): string => JSON.stringify(columns.map(c => c.name));

/**
 * Opens a Data View panel for a table or view (reusing the last plain one unless `opts.newTab`) and loads TOP `top`
 * rows through the runner's run_script. Rows can hold client personal data: they stay in the panel, except what the
 * user copies or exports (Export asks first, every time). Copy, selection, the viewer and Export resolve values here
 * from the loaded rows.
 */
export async function showDataView(ref: ObjectRef, top: number, d: DataViewDeps, opts: DataViewOptions = {}): Promise<void> {
  deps = d;
  const title = ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
  const session: DataViewSession = { ref, title, top, filters: [], pk: [], more: false, capped: false, notes: [], seq: 0, pending: emptyPending(), problems: [] };
  if (!opts.replace) {
    const open = [...views].find(x => x.session && sameObject(x.session.ref, ref));
    if (open) {
      open.panel.reveal(open.panel.viewColumn ?? vscode.ViewColumn.Active);
      return;
    }
  }
  let v = opts.replace ? lastActive : undefined;
  if (v && !(await confirmLeave(v, `open ${title}`))) return;
  if (!v) {
    const panel = vscode.window.createWebviewPanel(
      'msSqlMcp.dataView',
      panelTitle(session),
      { viewColumn: vscode.ViewColumn.Active, preserveFocus: false },
      { enableScripts: true, retainContextWhenHidden: true, localResourceRoots: [] },
    );
    const created: DataView = { panel, gen: 0 };
    views.add(created);
    panel.onDidDispose(() => {
      views.delete(created);
      const lost = created.session ? pendingCount(created.session.pending) : 0;
      if (lost) void vscode.window.showWarningMessage(`APoint-ms-sql: ${lost} unsaved row change(s) in ${created.session!.title} were discarded (the Data View was closed).`);
      created.session = undefined;
      if (lastActive === created) lastActive = undefined;
    });
    panel.onDidChangeViewState(e => { if (e.webviewPanel.active) lastActive = created; });
    panel.webview.onDidReceiveMessage(raw => onMessage(created, raw));
    lastActive = created;
    v = created;
  }
  v.session = session;
  v.panel.title = panelTitle(session);
  v.panel.iconPath = tabIcon(ref.connection);
  render(v, true);
  v.panel.reveal(v.panel.viewColumn ?? vscode.ViewColumn.Active);
  const view = v;
  await vscode.window.withProgress({ location: { viewId: 'msSqlMcp.explorer' }, title: `Loading ${title}` }, async () => {
    session.pk = await primaryKey(ref);
    session.meta = await columnMeta(ref);
    await load(view, 'first');
  });
}

export function disposeDataPanel(): void {
  for (const v of [...views]) v.panel.dispose();
  views.clear();
  lastActive = undefined;
}

/** Re-applies connection colors (tab icon and header) after the connection list changed. */
export function refreshDataViewColors(): void {
  for (const v of views) {
    if (!v.session) continue;
    v.panel.iconPath = tabIcon(v.session.ref.connection);
    render(v);
  }
}

const sameObject = (a: ObjectRef, b: ObjectRef): boolean =>
  a.connection.toLowerCase() === b.connection.toLowerCase() && (a.schema ?? '') === (b.schema ?? '') && a.name === b.name;

const panelTitle = (s: DataViewSession): string => `Data: ${s.title} (${s.ref.connection})`;

const colorOf = (connection: string): ConnectionColor | undefined => deps?.colorOf?.(connection);

/** A colored database icon for the tab of a colored connection (webview tabs cannot color their text). */
function tabIcon(connection: string): vscode.Uri | undefined {
  const color = colorOf(connection);
  if (!color || !deps?.extensionUri) return undefined;
  return vscode.Uri.joinPath(deps.extensionUri, 'media', `db-${color}.svg`);
}

/**
 * The table's primary-key columns (describe_table through the read-only explorer), cached per object. `refresh`
 * (Reload) reads it again.
 */
async function primaryKey(ref: ObjectRef, refresh = false): Promise<string[]> {
  if (ref.scriptType !== 'Table' || !deps) return [];
  const key = JSON.stringify([ref.connection, ref.schema ?? '', ref.name]);
  if (refresh) pkCache.delete(key);
  const cached = pkCache.get(key);
  if (cached) return cached;
  try {
    const pk = parsePrimaryKey(await deps.explorer.call(ref.connection, 'describe_table', { name: qualified(ref.schema, ref.name) }));
    pkCache.set(key, pk);
    return pk;
  } catch (err) {
    deps.log.warn('dataView', `describe_table for the primary key failed: ${err instanceof Error ? err.message : String(err)}`);
    return [];
  }
}

/** Column facts for editing: tables on read-write connections only (one read-only SELECT on sys.columns). */
async function columnMeta(ref: ObjectRef): Promise<ColumnMeta[] | undefined> {
  if (ref.scriptType !== 'Table' || !deps || readOnly(ref.connection)) return undefined;
  const outcome = await query(ref, columnMetaSql(ref), 5000);
  if (!outcome.result) {
    deps.log.warn('dataView', `Column information for editing failed: ${outcome.error ?? 'no result'}`);
    return undefined;
  }
  const meta = parseColumnMeta(outcome.result.rows);
  return meta.length ? meta : undefined;
}

const readOnly = (connection: string): boolean => deps?.isReadOnly?.(connection) ?? true;

function capabilities(s: DataViewSession): EditCapabilities {
  return editCapabilities({ isTable: s.ref.scriptType === 'Table', readOnly: readOnly(s.ref.connection), pk: s.pk, metaLoaded: !!s.meta?.length });
}

/** Asks before an action that would drop unsaved changes (sort, filter, reload, Replace Current Tab). */
async function confirmLeave(v: DataView, action: string): Promise<boolean> {
  const s = v.session;
  const n = s ? pendingCount(s.pending) : 0;
  if (!s || !n) return true;
  const choice = await vscode.window.showWarningMessage(
    `${s.title} has ${n} unsaved row change(s). Save them before you ${action}?`, { modal: true }, 'Save', 'Discard');
  if (choice === 'Discard') {
    s.pending = emptyPending();
    s.problems = [];
    return true;
  }
  if (choice === 'Save') return save(v);
  return false;
}

function render(v: DataView, loading = false): void {
  const s = v.session;
  if (!s) return;
  const columns = s.result?.columns ?? [];
  const indicator = sortIndicator(s.sort, columns);
  const filters = s.filters.flatMap(f => {
    const i = columns.findIndex(c => c.name === f.column);
    return i >= 0 ? [{ col: i, op: f.op, value: f.value }] : [];
  });
  const notes = [...s.notes];
  // Paging without a primary key may repeat or skip rows: say so whenever more pages can be (or were) loaded.
  const paging = s.loadedWith && (s.more || s.capped || (s.result && s.result.rows.length > s.loadedWith.top))
    ? pagingNote(s.loadedWith.sort, s.loadedWith.pk) : undefined;
  if (paging) notes.push(paging);
  if (s.capped) notes.push(LOAD_CAP_NOTE);
  const view = s.view && s.viewKey === columnsKey(columns) ? s.view : undefined;
  const color = colorOf(s.ref.connection);
  const caps = capabilities(s);
  const editing = s.result && (caps.update || caps.insert || caps.delete) ? caps : undefined;
  const writable = editing ? writableColumns(columns, s.meta ?? []) : [];
  const auto = columns.map(c => { const m = s.meta?.find(x => x.name === c.name); return !!m && (m.identity || m.computed); });
  v.panel.webview.html = renderDataView({
    objectName: s.title,
    connection: s.ref.connection,
    ...(color ? { connectionColor: color } : {}),
    top: s.top,
    loading,
    ...(s.error !== undefined ? { error: s.error } : {}),
    ...(s.result ? { result: s.result } : {}),
    ...(indicator ? { sort: indicator } : {}),
    filters,
    canLoadMore: s.more,
    notes,
    gen: ++v.gen,
    ...(view ? { view } : {}),
    ...(s.focus !== undefined ? { focus: s.focus } : {}),
    ...(s.scroll ? { scroll: s.scroll } : {}),
    ...(editing ? {
      edit: { ...editing, writable, auto, edits: s.pending.edits, deletes: s.pending.deletes, inserts: s.pending.inserts, problems: s.problems },
      pending: pendingCount(s.pending),
    } : s.result && caps.reason ? { readOnlyReason: caps.reason } : {}),
  }, makeNonce());
  s.focus = undefined;
  s.scroll = undefined;
}

async function query(ref: ObjectRef, script: string, maxRows: number): Promise<QueryOutcome> {
  try {
    const parsed = parseRunScriptResult(await deps!.runner.callResult(ref.connection, 'run_script', { script, maxRows }));
    const errors = parsed.messages.filter(m => m.kind === 'error').map(m => m.text);
    const notes = parsed.messages.filter(m => m.kind === 'warning').map(m => m.text);
    const set = parsed.resultSets[0];
    const result = set ? stripRowNumber(set) : undefined;
    deps!.log.info('dataView', `run_script on '${ref.connection}': ${result ? `${result.rows.length} row(s), ${result.columns.length} column(s)` : 'no result set'}`
      + `${result?.truncated ? ' (more)' : ''}, ${errors.length} error(s)`);
    if (errors.length || !result) return { notes: [], error: errors.join('\n') || 'The query returned no result set.' };
    return { result, notes };
  } catch (err) {
    const error = err instanceof Error ? err.message : String(err);
    deps!.log.warn('dataView', `run_script on '${ref.connection}' failed: ${error}`);
    return { notes: [], error };
  }
}

/**
 * Runs the Data View query for the current session state and shows its result or error.
 * - 'first': TOP (n) with the filters, ORDER BY the stored sort and the primary key. A failure whose message names a
 *   primary-key column is retried without the key (e.g. a column name holding ','); a failure with a sort is retried
 *   without it: if that result no longer has the sorted column, the sort is cleared with a note. `rows` (Reload)
 *   asks for that many rows instead of TOP, so a reload re-reads every row already loaded (Load more pages included).
 * - 'more': the next n rows of the loaded rows' own parameters (loadedWith), appended; at most MAX_LOADED_ROWS.
 * On an error the previously loaded rows stay on screen under the error text, and the sort, filters and TOP shown
 * go back to the ones those rows were queried with.
 */
async function load(v: DataView, kind: 'first' | 'more', rows?: number): Promise<void> {
  const s = v.session;
  if (!s || !deps) return;
  const seq = ++s.seq;
  if (kind === 'more') {
    await loadMore(v, s, seq);
    return;
  }
  const count = Math.min(MAX_TOP, Math.max(s.top, rows ?? 0));
  const run = (order: DataViewOrder | undefined, pk: string[]) => query(s.ref, dataViewSql(s.ref, count, order, { pk, filters: s.filters }), count);
  let pk = s.pk;
  let sort = s.sort;
  let outcome = await run(sort, pk);
  if (outcome.error !== undefined && pk.length && errorNamesKey(outcome.error, pk)) {
    const withoutPk = await run(sort, []);
    if (withoutPk.error === undefined) { outcome = withoutPk; pk = []; }
  }
  if (outcome.error !== undefined && sort) {
    const unsorted = await run(undefined, pk);
    if (unsorted.result && reconcileSort(sort, unsorted.result.columns).note) outcome = unsorted;
  }
  if (v.session !== s || seq !== s.seq) return;
  if (outcome.result) {
    const { columns, rows: resultRows, truncated } = outcome.result;
    const reconciled = reconcileSort(sort, columns);
    sort = reconciled.sort;
    s.sort = sort;
    s.pk = usablePrimaryKey(pk, columns);
    s.filters = reconcileFilters(s.filters, columns);
    s.result = { columns, rows: resultRows, truncated };
    s.loadedWith = { ...(sort ? { sort } : {}), filters: [...s.filters], pk: [...s.pk], top: s.top };
    s.more = truncated && resultRows.length < MAX_LOADED_ROWS;
    s.capped = truncated && resultRows.length >= MAX_LOADED_ROWS;
    s.notes = reconciled.note ? [reconciled.note, ...outcome.notes] : outcome.notes;
    s.error = undefined;
  } else {
    s.error = outcome.error;
    s.scroll = undefined;
    if (s.result && s.loadedWith) {
      // The old rows stay: show the sort, filters and TOP they were queried with.
      const shown = displayedParams({ sort: s.sort, filters: s.filters }, s.loadedWith, true);
      s.sort = shown.sort;
      s.filters = shown.filters;
      s.top = s.loadedWith.top;
    } else {
      s.notes = [];
    }
  }
  render(v);
}

async function loadMore(v: DataView, s: DataViewSession, seq: number): Promise<void> {
  const loaded = s.result;
  const lw = s.loadedWith;
  if (!loaded || !lw || !s.more) return;
  // Only the parameters the loaded rows were queried with (never a sort / filter whose query failed).
  const page = nextPageRequest(s.ref, lw, loaded.rows.length);
  if (!page) {
    s.more = false;
    s.capped = true;
    render(v);
    return;
  }
  const outcome = await query(s.ref, page.script, page.maxRows);
  if (v.session !== s || seq !== s.seq) return;
  if (outcome.result && columnsKey(outcome.result.columns) !== columnsKey(loaded.columns)) {
    outcome.error = 'The columns of the object changed. Reload the view.';
  }
  if (outcome.error !== undefined || !outcome.result) {
    s.error = outcome.error;
  } else {
    const rows = loaded.rows.concat(outcome.result.rows);
    const truncated = outcome.result.truncated;
    s.result = { columns: loaded.columns, rows, truncated };
    s.more = truncated && rows.length < MAX_LOADED_ROWS;
    s.capped = truncated && rows.length >= MAX_LOADED_ROWS;
    s.notes = outcome.notes;
    s.error = undefined;
  }
  render(v);
}

/** Widths reset when new data loads (sort, filter); Load more and Reload keep them. */
function dropWidths(s: DataViewSession): void {
  if (s.view?.widths) {
    const { widths: _w, ...rest } = s.view;
    void _w;
    s.view = rest;
  }
}

function onMessage(v: DataView, raw: unknown): void {
  const s = v.session;
  if (!s || !deps) return;
  const columns = s.result?.columns ?? [];
  if (s.result && onEditMessage(v, s, raw)) return;
  const message = parseDataViewMessage(raw, { rows: s.result?.rows.length ?? 0, cols: columns.length }, v.gen, columns.map(c => c.type));
  if (!message) return;
  switch (message.type) {
    case 'copy':
    case 'copyRow':
    case 'copySelection':
    case 'openCell':
    case 'export':
      runGridAction(message, { columns, rows: effectiveRows(s.result!.rows, s.pending), objectName: s.title }, deps.viewer).catch(err => {
        void vscode.window.showErrorMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`);
      });
      return;
    case 'viewState':
      s.view = message.view;
      s.viewKey = columnsKey(columns);
      return;
    case 'sort': {
      const column = columns[message.col];
      if (!isSortableType(column.type)) return;
      if (pendingCount(s.pending)) { void confirmLeave(v, 'sort').then(ok => (ok ? onMessage(v, { ...(raw as object), gen: v.gen }) : render(v))); return; }
      s.sort = message.dir === 'none' ? undefined : { column: column.name, type: column.type, dir: message.dir };
      dropWidths(s);
      void requery(v, 'first');
      return;
    }
    case 'filter':
      if (pendingCount(s.pending)) { void confirmLeave(v, 'filter').then(ok => (ok ? onMessage(v, { ...(raw as object), gen: v.gen }) : render(v))); return; }
      s.filters = message.filters.map(f => ({ column: columns[f.col].name, type: columns[f.col].type, op: f.op, value: f.value }));
      s.focus = message.focus;
      dropWidths(s);
      void requery(v, 'first');
      return;
    case 'reload': {
      if (pendingCount(s.pending)) { void confirmLeave(v, 'reload').then(ok => (ok ? onMessage(v, { ...(raw as object), gen: v.gen }) : render(v))); return; }
      // Reload re-reads at least as many rows as are loaded now (Load more pages included), with the same sort and
      // filters, and keeps the scroll position, so rows changed or added within that range show up in place.
      const loadedRows = s.result && s.loadedWith && s.loadedWith.top === message.top ? s.result.rows.length : 0;
      s.top = message.top;
      if (message.scroll) s.scroll = message.scroll;
      // Reload also reads the primary key again (it may have changed since the view opened).
      void vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'APoint-ms-sql: loading data' }, async () => {
        s.pk = await primaryKey(s.ref, true);
        await load(v, 'first', loadedRows);
      });
      return;
    }
    case 'loadMore':
      s.scroll = message.scroll;
      void requery(v, 'more');
      return;
  }
}

/** Handles an editing message; false when `raw` is not one. */
function onEditMessage(v: DataView, s: DataViewSession, raw: unknown): boolean {
  const type = (raw as { type?: unknown } | undefined)?.type;
  if (type !== 'edit' && type !== 'deleteRows' && type !== 'revertRows' && type !== 'addRow' && type !== 'save' && type !== 'discard') return false;
  const result = s.result!;
  const caps = capabilities(s);
  const m = parseEditMessage(raw, {
    gen: v.gen, loaded: result.rows.length, inserted: s.pending.inserts.length, writable: writableColumns(result.columns, s.meta ?? []), caps,
  });
  if (!m || s.saving) return true;
  const loaded = result.rows.length;
  switch (m.type) {
    case 'edit': {
      applyEdit(s.pending, result.rows, m.row, m.col, m.value);
      const nullable = s.meta?.find(x => x.name === result.columns[m.col].name)?.nullable ?? true;
      const error = validateValue(result.columns[m.col].type, m.value, nullable);
      s.problems = s.problems.filter(p => p.row !== m.row || p.col !== m.col);
      if (error) s.problems.push({ row: m.row, col: m.col, message: error });
      const reverted = m.row < loaded && !s.pending.edits.get(m.row)?.has(m.col);
      void v.panel.webview.postMessage({ type: 'pending', count: pendingCount(s.pending), row: m.row, col: m.col, ...(error ? { error } : {}), ...(reverted ? { reverted: true } : {}) });
      return true;
    }
    case 'deleteRows':
      deleteRows(s.pending, loaded, m.rows);
      break;
    case 'revertRows':
      revertRows(s.pending, loaded, m.rows);
      break;
    case 'addRow':
      s.pending.inserts.push(result.columns.map(() => undefined));
      s.scroll = [10_000_000, 0];
      render(v);
      return true;
    case 'discard':
      s.pending = emptyPending();
      break;
    case 'save':
      if (m.scroll) s.scroll = m.scroll;
      void save(v);
      return true;
  }
  // Row indexes of new rows shift when one is removed: re-check what is still pending.
  s.problems = pendingProblems(s.pending, result.columns, s.meta ?? [], loaded);
  if ('scroll' in m && m.scroll) s.scroll = m.scroll;
  render(v);
  return true;
}

/**
 * Saves the pending changes in one transaction through the runner (the connection must still be read-write). Deletes
 * are confirmed first. On success the rows are reloaded (as many as were loaded, same scroll); on an error nothing is
 * saved and the changes stay pending. Returns true when everything was saved.
 */
async function save(v: DataView): Promise<boolean> {
  const s = v.session;
  if (!s || !s.result || !deps || s.saving) return false;
  const count = pendingCount(s.pending);
  if (!count) return true;
  if (readOnly(s.ref.connection)) {
    void vscode.window.showErrorMessage(`APoint-ms-sql: '${s.ref.connection}' is read-only; changes cannot be saved.`);
    return false;
  }
  const { columns, rows } = s.result;
  s.problems = pendingProblems(s.pending, columns, s.meta ?? [], rows.length);
  if (s.problems.length) {
    render(v);
    const first = s.problems[0];
    void vscode.window.showErrorMessage(`APoint-ms-sql: ${s.problems.length} value(s) cannot be saved - row ${first.row < rows.length ? first.row + 1 : 'new'}, ${columns[first.col].name}: ${first.message}`);
    return false;
  }
  let built;
  try { built = buildSaveScript(s.ref, columns, rows, s.pk, s.pending); }
  catch (err) { void vscode.window.showErrorMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`); return false; }
  if (built.deleted) {
    const ok = await vscode.window.showWarningMessage(
      `Delete ${built.deleted} row(s) from ${s.title} on '${s.ref.connection}'?`, { modal: true, detail: 'All changes are saved together in one transaction.' }, 'Save and Delete');
    if (ok !== 'Save and Delete') return false;
  }
  s.saving = true;
  try {
    deps.log.info('dataView', `Saving ${s.title} on '${s.ref.connection}': ${built.updated} update(s), ${built.deleted} delete(s), ${built.inserted} insert(s)`);
    const outcome = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'APoint-ms-sql: saving changes' },
      () => saveQuery(s.ref, built.script));
    if (v.session !== s) return false;
    if (outcome.error !== undefined) {
      s.error = `Nothing was saved: ${outcome.error}`;
      render(v);
      return false;
    }
    const loadedRows = rows.length;
    s.pending = emptyPending();
    s.problems = [];
    s.error = undefined;
    const parts = [built.updated && `${built.updated} updated`, built.inserted && `${built.inserted} added`, built.deleted && `${built.deleted} deleted`].filter(Boolean);
    void vscode.window.setStatusBarMessage(`APoint-ms-sql: ${s.title} saved (${parts.join(', ')})`, 5000);
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'APoint-ms-sql: loading data' },
      () => load(v, 'first', loadedRows + built.inserted));
    return true;
  } finally {
    s.saving = false;
  }
}

/** Runs the save script; the error text (server errors joined) or nothing. */
async function saveQuery(ref: ObjectRef, script: string): Promise<{ error?: string }> {
  try {
    const parsed = parseRunScriptResult(await deps!.runner.callResult(ref.connection, 'run_script', { script, maxRows: 1 }));
    const errors = parsed.messages.filter(m => m.kind === 'error').map(m => m.text);
    return errors.length ? { error: errors.join('\n') } : {};
  } catch (err) {
    return { error: err instanceof Error ? err.message : String(err) };
  }
}

function requery(v: DataView, kind: 'first' | 'more'): Thenable<void> {
  return vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'APoint-ms-sql: loading data' }, () => load(v, kind));
}
