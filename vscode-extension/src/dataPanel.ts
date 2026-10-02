import * as vscode from 'vscode';
import type { ServerProcessClient } from './client/serverProcessClient';
import type { ObjectRef } from './explorer/catalog';
import { dataViewRequest } from './explorer/treeModel';
import { renderDataView } from './grid/gridHtml';
import {
  DataViewOrder, cellAt, copiedMessage, copyText, isSortableType, parseDataViewMessage, reconcileSort,
} from './grid/gridModel';
import type { Logger } from './logger';
import { RunScriptResultSet, parseRunScriptResult } from './query/runScript';
import { makeNonce } from './webviewUtil';

/** What the open Data View shows; kept until the panel is reused for another object (or closed). */
interface DataViewSession {
  ref: ObjectRef;
  title: string;
  top: number;
  /** Server-side sort as set by the user: column name and type (from the result it was clicked on) and direction. */
  sort?: DataViewOrder;
  result?: RunScriptResultSet;
  notes: string[];
  error?: string;
  /** Number of the latest query; an older query's answer is dropped. */
  seq: number;
}

/** One run_script of the Data View query: the first result set, or the error text. */
interface QueryOutcome { result?: RunScriptResultSet; notes: string[]; error?: string }

let panel: vscode.WebviewPanel | undefined;
let session: DataViewSession | undefined;
let runner: ServerProcessClient | undefined;
let log: Logger | undefined;
/** Render counter of the page on screen (`data-gen`); copy messages from an older page are ignored. */
let gen = 0;

/**
 * Opens (or reuses) the Data View panel for a table or view and loads TOP `top` rows through the runner's
 * run_script. Local only: rows can hold client personal data, so the panel never sends them anywhere (no export,
 * no network - see the CSP); a copy click copies one cell, resolved here from the loaded result.
 */
export async function showDataView(ref: ObjectRef, top: number, deps: { runner: ServerProcessClient; log: Logger }): Promise<void> {
  runner = deps.runner;
  log = deps.log;
  const title = ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
  session = { ref, title, top, notes: [], seq: 0 };
  if (!panel) {
    panel = vscode.window.createWebviewPanel(
      'msSqlMcp.dataView',
      panelTitle(session),
      { viewColumn: vscode.ViewColumn.Active, preserveFocus: false },
      { enableScripts: true, retainContextWhenHidden: true, localResourceRoots: [] },
    );
    panel.onDidDispose(() => {
      panel = undefined;
      session = undefined;
    });
    panel.webview.onDidReceiveMessage(onMessage);
  }
  panel.title = panelTitle(session);
  render(true);
  panel.reveal(panel.viewColumn ?? vscode.ViewColumn.Active);
  await vscode.window.withProgress({ location: { viewId: 'msSqlMcp.explorer' }, title: `Loading ${title}` }, () => load());
}

export function disposeDataPanel(): void {
  panel?.dispose();
  panel = undefined;
  session = undefined;
}

const panelTitle = (s: DataViewSession): string => `Data: ${s.title} (${s.ref.connection})`;

function render(loading = false): void {
  if (!panel || !session) return;
  const s = session;
  const col = s.sort && s.result ? s.result.columns.findIndex(c => c.name === s.sort!.column) : -1;
  panel.webview.html = renderDataView({
    objectName: s.title,
    connection: s.ref.connection,
    top: s.top,
    loading,
    ...(s.error !== undefined ? { error: s.error } : {}),
    ...(s.result ? { result: { columns: s.result.columns, rows: s.result.rows, truncated: s.result.truncated } } : {}),
    ...(s.sort && col >= 0 ? { sort: { col, dir: s.sort.dir } } : {}),
    notes: s.notes,
    gen: ++gen,
  }, makeNonce());
}

async function query(ref: ObjectRef, top: number, order: DataViewOrder | undefined): Promise<QueryOutcome> {
  try {
    const parsed = parseRunScriptResult(await runner!.callResult(ref.connection, 'run_script', dataViewRequest(ref, top, order)));
    const errors = parsed.messages.filter(m => m.kind === 'error').map(m => m.text);
    const notes = parsed.messages.filter(m => m.kind === 'warning').map(m => m.text);
    const result = parsed.resultSets[0];
    log?.info('dataView', `run_script on '${ref.connection}': ${result ? `${result.rows.length} row(s), ${result.columns.length} column(s)` : 'no result set'}`
      + `${result?.truncated ? ' (truncated)' : ''}, ${errors.length} error(s)${order ? ', sorted' : ''}`);
    if (errors.length || !result) return { notes: [], error: errors.join('\n') || 'The query returned no result set.' };
    return { result, notes };
  } catch (err) {
    const error = err instanceof Error ? err.message : String(err);
    log?.warn('dataView', `run_script on '${ref.connection}' failed: ${error}`);
    return { notes: [], error };
  }
}

/**
 * Runs the Data View query for the current session state and shows its result or error. ORDER BY comes from the
 * stored sort. When a sorted query fails, it is retried once without the sort: if that result no longer has the
 * sorted column (dropped or renamed), the sort is cleared with a note; otherwise the original error is shown.
 */
async function load(): Promise<void> {
  const s = session;
  if (!s || !runner) return;
  const seq = ++s.seq;
  let outcome = await query(s.ref, s.top, s.sort);
  let sort = s.sort;
  if (outcome.error !== undefined && sort) {
    const unsorted = await query(s.ref, s.top, undefined);
    if (unsorted.result && reconcileSort(sort, unsorted.result.columns).note) outcome = unsorted;
  }
  if (outcome.result) {
    const reconciled = reconcileSort(sort, outcome.result.columns);
    sort = reconciled.sort;
    if (reconciled.note) outcome.notes = [reconciled.note, ...outcome.notes];
  }
  // A newer query, another object or a closed panel: this answer is stale.
  if (session !== s || seq !== s.seq) return;
  s.sort = outcome.error === undefined ? sort : s.sort;
  s.result = outcome.result;
  s.notes = outcome.notes;
  s.error = outcome.error;
  render();
}

function onMessage(raw: unknown): void {
  const s = session;
  if (!s) return;
  const message = parseDataViewMessage(raw, { rows: s.result?.rows.length ?? 0, cols: s.result?.columns.length ?? 0 }, gen);
  if (!message) return;
  switch (message.type) {
    case 'copy': {
      // Indexes only came from the webview; the value is resolved from the loaded result.
      const text = copyText(cellAt(s.result!.rows, message.row, message.col));
      vscode.env.clipboard.writeText(text).then(
        () => { vscode.window.setStatusBarMessage(copiedMessage(text), 2000); },
        err => { void vscode.window.showWarningMessage(`APoint-ms-sql: copy failed: ${err instanceof Error ? err.message : String(err)}`); });
      return;
    }
    case 'sort': {
      const column = s.result!.columns[message.col];
      if (!isSortableType(column.type)) return;
      s.sort = message.dir === 'none' ? undefined : { column: column.name, type: column.type, dir: message.dir };
      void requery();
      return;
    }
    case 'reload':
      s.top = message.top;
      void requery();
      return;
  }
}

function requery(): Thenable<void> {
  return vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'APoint-ms-sql: loading data' }, () => load());
}
