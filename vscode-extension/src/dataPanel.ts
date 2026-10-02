import * as vscode from 'vscode';
import type { ServerProcessClient } from './client/serverProcessClient';
import type { ObjectRef } from './explorer/catalog';
import { dataViewRequest } from './explorer/treeModel';
import { renderDataView } from './grid/gridHtml';
import { DataViewOrder, SortDir, cellAt, copyText, isSortableType, parseDataViewMessage } from './grid/gridModel';
import type { Logger } from './logger';
import { RunScriptResultSet, parseRunScriptResult } from './query/runScript';
import { makeNonce } from './webviewUtil';

/** What the open Data View shows; kept until the panel is reused for another object (or closed). */
interface DataViewSession {
  ref: ObjectRef;
  title: string;
  top: number;
  /** Server-side sort, by column name (the columns come back with every query). */
  sort?: { column: string; dir: SortDir };
  result?: RunScriptResultSet;
  notes: string[];
  error?: string;
  /** Number of the latest query; an older query's answer is dropped. */
  seq: number;
}

let panel: vscode.WebviewPanel | undefined;
let session: DataViewSession | undefined;
let runner: ServerProcessClient | undefined;
let log: Logger | undefined;

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
  const sortCol = s.sort && s.result ? s.result.columns.findIndex(c => c.name === s.sort!.column) : -1;
  panel.webview.html = renderDataView({
    objectName: s.title,
    connection: s.ref.connection,
    top: s.top,
    loading,
    ...(s.error !== undefined ? { error: s.error } : {}),
    ...(s.result ? { result: { columns: s.result.columns, rows: s.result.rows, truncated: s.result.truncated } } : {}),
    ...(sortCol >= 0 ? { sort: { col: sortCol, dir: s.sort!.dir } } : {}),
    notes: s.notes,
  }, makeNonce());
}

/** Runs the Data View query for the current session state and shows its result or error. */
async function load(): Promise<void> {
  const s = session;
  if (!s || !runner) return;
  const seq = ++s.seq;
  const sortType = s.sort && s.result?.columns.find(c => c.name === s.sort!.column)?.type;
  const order: DataViewOrder | undefined = s.sort && sortType !== undefined ? { column: s.sort.column, type: sortType, dir: s.sort.dir } : undefined;
  let result: RunScriptResultSet | undefined;
  let notes: string[] = [];
  let error: string | undefined;
  try {
    const parsed = parseRunScriptResult(await runner.callResult(s.ref.connection, 'run_script', dataViewRequest(s.ref, s.top, order)));
    const errors = parsed.messages.filter(m => m.kind === 'error').map(m => m.text);
    notes = parsed.messages.filter(m => m.kind === 'warning').map(m => m.text);
    result = parsed.resultSets[0];
    if (errors.length || !result) error = errors.join('\n') || 'The query returned no result set.';
    log?.info('dataView', `run_script on '${s.ref.connection}': ${result ? `${result.rows.length} row(s), ${result.columns.length} column(s)` : 'no result set'}`
      + `${result?.truncated ? ' (truncated)' : ''}, ${errors.length} error(s)`);
  } catch (err) {
    error = err instanceof Error ? err.message : String(err);
    log?.warn('dataView', `run_script on '${s.ref.connection}' failed: ${error}`);
  }
  // A newer query, another object or a closed panel: this answer is stale.
  if (session !== s || seq !== s.seq) return;
  s.result = error === undefined ? result : undefined;
  s.notes = error === undefined ? notes : [];
  s.error = error;
  render();
}

function onMessage(raw: unknown): void {
  const s = session;
  if (!s) return;
  const message = parseDataViewMessage(raw, { rows: s.result?.rows.length ?? 0, cols: s.result?.columns.length ?? 0 });
  if (!message) return;
  switch (message.type) {
    case 'copy': {
      // Indexes only came from the webview; the value is resolved from the loaded result.
      const text = copyText(cellAt(s.result!.rows, message.row, message.col));
      vscode.env.clipboard.writeText(text).then(
        () => { vscode.window.setStatusBarMessage('Copied', 2000); },
        err => { void vscode.window.showWarningMessage(`APoint-ms-sql: copy failed: ${err instanceof Error ? err.message : String(err)}`); });
      return;
    }
    case 'sort': {
      const column = s.result!.columns[message.col];
      if (!isSortableType(column.type)) return;
      s.sort = message.dir === 'none' ? undefined : { column: column.name, dir: message.dir };
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
