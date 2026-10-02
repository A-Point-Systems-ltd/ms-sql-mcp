import * as vscode from 'vscode';
import type { CellViewer } from '../grid/cellViewer';
import { runGridAction } from '../grid/gridActions';
import { GridViewState, parseResultsMessage } from '../grid/gridModel';
import { makeNonce } from '../webviewUtil';
import { ResultsState, renderResults } from './resultsHtml';
import { editorLine } from './runScript';

export const RESULTS_VIEW_ID = 'msSqlMcp.results';

const EMPTY: ResultsState = { kind: 'empty' };

/**
 * The "Results" webview view in the bottom panel. Holds the last run state per document (in memory only; rows leave
 * it only through the user's copy and Export actions) and shows the state of the active bound document.
 */
export class ResultsViewProvider implements vscode.WebviewViewProvider, vscode.Disposable {
  private view: vscode.WebviewView | undefined;
  private readonly states = new Map<string, ResultsState>();
  /** Document whose state is on screen, and that state (to skip re-rendering an unchanged view). */
  private shownKey: string | undefined;
  private shownState: ResultsState | undefined;
  /** Render counter of the page on screen; grid messages from an older page are ignored. */
  private gen = 0;
  /** Grid view (column order, hidden, frozen, ...) per result set of a state; dropped with the state. */
  private readonly views = new WeakMap<ResultsState, (GridViewState | undefined)[]>();
  /** Listeners of the current webview; disposed with it. */
  private viewSubs: vscode.Disposable[] = [];

  constructor(
    /** The document the view should show (the active bound one), or undefined to keep the current one. */
    private readonly target: () => { key: string; bound: boolean } | undefined,
    /** Called when the webview's Cancel button is pressed for document `key`. */
    private readonly onCancel: (key: string) => void,
    /** Opens cell values in read-only mssql-cell: documents. */
    private readonly viewer: CellViewer,
  ) {}

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = { enableScripts: true, localResourceRoots: [] };
    this.disposeViewSubs();
    this.viewSubs = [
      view.webview.onDidReceiveMessage((message: unknown) => this.onMessage(message)),
      view.onDidDispose(() => {
        if (this.view !== view) return;
        this.view = undefined;
        this.shownState = undefined;
        this.disposeViewSubs();
      }),
    ];
    this.shownState = undefined;
    this.update();
  }

  stateOf(key: string): ResultsState | undefined {
    return this.states.get(key);
  }

  /** Sets the state of document `key` and re-renders when that document is on screen. */
  set(key: string, state: ResultsState): void {
    this.states.set(key, state);
    this.update();
  }

  /** Forgets document `key` (closed). */
  forget(key: string): void {
    if (!this.states.delete(key)) return;
    this.update();
  }

  /** Re-renders when the document to show or its state changed. */
  update(): void {
    const target = this.target();
    if (target) this.shownKey = target.key;
    const state = target
      ? (target.bound ? this.states.get(target.key) ?? EMPTY : EMPTY)
      : (this.shownKey !== undefined ? this.states.get(this.shownKey) ?? EMPTY : EMPTY);
    if (!this.view || state === this.shownState) return;
    this.shownState = state;
    this.view.webview.html = renderResults(state, makeNonce(), this.view.webview.cspSource, ++this.gen, this.views.get(state) ?? []);
  }

  /**
   * Brings the view into sight without taking focus from `editor`, so a second F5 still reaches the editor.
   */
  async reveal(editor: vscode.TextEditor): Promise<void> {
    if (this.view) {
      this.view.show(true);
      return;
    }
    // Not resolved yet: the focus command creates it; then hand focus back to the editor.
    await vscode.commands.executeCommand(`${RESULTS_VIEW_ID}.focus`, { preserveFocus: true });
    await vscode.window.showTextDocument(editor.document, { viewColumn: editor.viewColumn, preserveFocus: false });
  }

  dispose(): void {
    this.disposeViewSubs();
    this.states.clear();
  }

  private disposeViewSubs(): void {
    for (const s of this.viewSubs) s.dispose();
    this.viewSubs = [];
  }

  private onMessage(raw: unknown): void {
    const key = this.shownKey;
    if (!key) return;
    const state = this.states.get(key);
    const sets = state?.kind === 'done' ? state.result.resultSets : [];
    const message = parseResultsMessage(raw, sets.map(s => ({ rows: s.rows.length, cols: s.columns.length })), this.gen);
    if (!message) return;
    if (message.type === 'cancel') {
      this.onCancel(key);
      return;
    }
    if (message.type === 'reveal') {
      if (state?.kind !== 'done') return;
      const line = editorLine(message.line, state.lineOffset);
      if (line === undefined) return;
      revealLine(key, line).catch(err => {
        void vscode.window.showWarningMessage(`APoint-ms-sql: could not show line ${line + 1}: ${err instanceof Error ? err.message : String(err)}`);
      });
      return;
    }
    if (state?.kind !== 'done') return;
    if (message.type === 'viewState') {
      const list = this.views.get(state) ?? [];
      list[message.set] = message.view;
      this.views.set(state, list);
      return;
    }
    // Copy, copy row, copy selection, viewer and Export: resolved from the stored result of the shown document;
    // the webview only sent indexes.
    const set = sets[message.set];
    runGridAction(message, { columns: set.columns, rows: set.rows, objectName: 'results' }, this.viewer).catch(err => {
      void vscode.window.showErrorMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`);
    });
  }
}

/** Shows document `key` and selects editor line `line` (0-based). */
async function revealLine(key: string, line: number): Promise<void> {
  const uri = vscode.Uri.parse(key);
  const visible = vscode.window.visibleTextEditors.find(e => e.document.uri.toString() === key);
  const document = visible?.document ?? await vscode.workspace.openTextDocument(uri);
  if (line >= document.lineCount) {
    void vscode.window.showInformationMessage(`APoint-ms-sql: line ${line + 1} is past the end of the document (it changed after the run).`);
    return;
  }
  const range = document.lineAt(line).range;
  const editor = await vscode.window.showTextDocument(document, {
    viewColumn: visible?.viewColumn,
    preserveFocus: false,
    selection: new vscode.Selection(range.start, range.end),
  });
  editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
}
