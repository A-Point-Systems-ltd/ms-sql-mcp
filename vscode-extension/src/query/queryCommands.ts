import * as vscode from 'vscode';
import { McpToolError } from '../client/parse';
import type { ServerProcessClient } from '../client/serverProcessClient';
import { pickProfile } from '../connections/connectionCommands';
import type { ConnectionProfile } from '../connections/profile';
import { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';
import { EditorRunState, editorRunState, findProfile, runContextDocs } from './editorState';
import { QueryAssociation, QueryDocuments, defersRunCleanup, isNeverBound } from './queryDocuments';
import { SqlDocFileSystem, openDocumentKeys } from './sqlDocFs';
import { REOPEN_GRACE_MS, SqlDocLifecycle } from './sqlDocLifecycle';
import { RESULTS_VIEW_ID, ResultsViewProvider } from './resultsView';
import type { ResultsState } from './resultsHtml';
import { RunRegistry } from './runRegistry';
import { appliedSuccessfully, buildRunRequest, clampMaxRows, parseRunScriptResult } from './runScript';
import { describeTarget, targetOf, wrongTargetPrompt } from './targetGuard';

/** The active editor is bound to a connection (F5 keybinding, command palette). */
const CONNECTED_KEY = 'msSqlMcp.editorConnected';

/** True while the active document's query runs (command palette Cancel). */
const RUNNING_KEY = 'msSqlMcp.queryRunning';

/**
 * Per-document keys for the editor title buttons (`resource in msSqlMcp.runnableDocs`): arrays of `uri.toString()`,
 * so each editor group's title reflects its own document, not the active one.
 */
const DOC_KEYS = {
  runnable: 'msSqlMcp.runnableDocs',
  blocked: 'msSqlMcp.blockedDocs',
  running: 'msSqlMcp.runningDocs',
} as const;

/** setContext only when the value changed (compared as JSON). */
function contextSetter(): (key: string, value: boolean | string[]) => void {
  const last = new Map<string, string>();
  return (key, value) => {
    const json = JSON.stringify(value);
    if (last.get(key) === json) return;
    last.set(key, json);
    void vscode.commands.executeCommand('setContext', key, value);
  };
}

/** The document a command acts on: the editor/title uri argument (any visible or open document), else the active editor. */
function commandTarget(arg: unknown): { document: vscode.TextDocument; editor: vscode.TextEditor | undefined } | undefined {
  if (arg instanceof vscode.Uri) {
    const key = arg.toString();
    const editor = vscode.window.activeTextEditor?.document.uri.toString() === key
      ? vscode.window.activeTextEditor
      : vscode.window.visibleTextEditors.find(e => e.document.uri.toString() === key);
    const document = editor?.document ?? vscode.workspace.textDocuments.find(d => d.uri.toString() === key);
    return document ? { document, editor } : undefined;
  }
  const editor = vscode.window.activeTextEditor;
  return editor ? { document: editor.document, editor } : undefined;
}

/** Everything known about one document's binding. */
export interface DocumentRunContext {
  assoc: QueryAssociation | undefined;
  profile: ConnectionProfile | undefined;
  state: EditorRunState;
}

/**
 * Keeps the connection status bar item and the `msSqlMcp.editor*` context keys in step with the active editor,
 * the document associations and the connection store.
 */
export class QueryEditorTracker implements vscode.Disposable {
  private readonly item: vscode.StatusBarItem;
  private readonly subs: vscode.Disposable[] = [];
  private readonly emitter = new vscode.EventEmitter<void>();
  /** Fired after the active editor's run context was recomputed. */
  readonly onDidChange = this.emitter.event;
  private readonly setKey = contextSetter();

  constructor(private readonly store: ConnectionStore, private readonly docs: QueryDocuments) {
    this.item = vscode.window.createStatusBarItem('msSqlMcp.editorConnection', vscode.StatusBarAlignment.Left, 100);
    this.item.name = 'MSSQL-MCP Connection';
    this.item.command = 'msSqlMcp.changeConnection';
    this.subs.push(
      this.item,
      this.emitter,
      vscode.window.onDidChangeActiveTextEditor(() => this.refresh()),
      // A language change reopens the document without changing the active editor.
      vscode.workspace.onDidOpenTextDocument(doc => {
        if (doc === vscode.window.activeTextEditor?.document) this.refresh();
      }),
      docs.onDidChange(() => this.refresh()),
      store.onDidChange(() => this.refresh()),
    );
    this.refresh();
  }

  /** Association, profile and run state of `document`. `mssql-ddl:` documents are never bound. */
  contextOf(document: vscode.TextDocument): DocumentRunContext {
    const assoc = isNeverBound(document.uri.scheme) ? undefined : this.docs.get(document.uri);
    const profile = assoc ? findProfile(this.store.list(), assoc.connection) : undefined;
    return { assoc, profile, state: editorRunState(assoc, profile) };
  }

  /** The active text editor and its run context, if there is an active text editor. */
  active(): (DocumentRunContext & { editor: vscode.TextEditor }) | undefined {
    const editor = vscode.window.activeTextEditor;
    return editor ? { editor, ...this.contextOf(editor.document) } : undefined;
  }

  /** Recomputes the status bar item and the context keys for the active editor. */
  refresh(): void {
    const active = this.active();
    const visible = !!active && !isNeverBound(active.editor.document.uri.scheme)
      && (active.editor.document.languageId === 'sql' || !!active.assoc);
    const state = active?.state;
    if (visible && state) {
      this.item.text = state.statusText;
      this.item.tooltip = state.tooltip;
      this.item.show();
    } else {
      this.item.hide();
    }
    this.setKey(CONNECTED_KEY, !!state?.connected);
    const { runnable, blocked } = runContextDocs(this.docs.all(), this.store.list());
    this.setKey(DOC_KEYS.runnable, runnable);
    this.setKey(DOC_KEYS.blocked, blocked);
    this.emitter.fire();
  }

  dispose(): void {
    for (const s of this.subs) s.dispose();
  }
}

/**
 * What the query commands need beyond the store: the runner process, the `mssql-sql:` file system and a way to refresh
 * the object tree.
 */
export interface QueryRunDeps {
  runner: ServerProcessClient;
  /** Backs New Query documents and object documents. */
  sqlDocs: SqlDocFileSystem;
  /** Refreshes the object tree after an object document was applied. */
  refreshTree: () => void;
}

/**
 * Creates the query-document association store, the editor tracker and the results view, and registers
 * `msSqlMcp.newQuery`, `msSqlMcp.openRecentQuery`, `msSqlMcp.changeConnection`, `msSqlMcp.runQuery`,
 * `msSqlMcp.cancelQuery` and `msSqlMcp.runQueryReadOnly`.
 */
export function registerQueryCommands(
  context: vscode.ExtensionContext, store: ConnectionStore, log: Logger, deps: QueryRunDeps,
): { docs: QueryDocuments; tracker: QueryEditorTracker; results: ResultsViewProvider } {
  const docs = new QueryDocuments(context.workspaceState);
  const tracker = new QueryEditorTracker(store, docs);
  context.subscriptions.push(docs, tracker);
  /** One run per document, each with its own token (see RunRegistry). */
  const running = new RunRegistry();
  const lifecycle = new SqlDocLifecycle(context, docs, deps.sqlDocs, log, store, key => running.has(key));
  context.subscriptions.push(lifecycle);

  const cancel = (key: string) => running.cancel(key);
  const results = new ResultsViewProvider(() => {
    const active = tracker.active();
    return active ? { key: active.editor.document.uri.toString(), bound: !!active.assoc } : undefined;
  }, cancel);
  const setKey = contextSetter();
  const closeTimers = new Set<NodeJS.Timeout>();
  const updateRunning = () => {
    const editor = vscode.window.activeTextEditor;
    setKey(RUNNING_KEY, !!editor && running.has(editor.document.uri.toString()));
    setKey(DOC_KEYS.running, running.keys());
  };
  context.subscriptions.push(
    results,
    vscode.window.registerWebviewViewProvider(RESULTS_VIEW_ID, results, { webviewOptions: { retainContextWhenHidden: true } }),
    tracker.onDidChange(() => {
      updateRunning();
      results.update();
    }),
    // Results are kept in memory per document. Closing a document aborts its run and drops its results and guard: at
    // once for untitled documents, so a new document reusing the uri starts clean (the old run no longer updates
    // anything); after the reopen grace for mssql-sql documents, whose language change closes and reopens them.
    vscode.workspace.onDidCloseTextDocument(doc => {
      const key = doc.uri.toString();
      const cleanUp = () => {
        if (running.close(key)) {
          log.info('query', 'The document of a running query was closed: the run was cancelled.');
          updateRunning();
        }
        results.forget(key);
      };
      if (!defersRunCleanup(doc.uri.scheme)) {
        cleanUp();
        return;
      }
      const timer = setTimeout(() => {
        closeTimers.delete(timer);
        if (!openDocumentKeys().has(key)) cleanUp();
      }, REOPEN_GRACE_MS);
      closeTimers.add(timer);
    }),
    { dispose: () => { for (const t of closeTimers) clearTimeout(t); closeTimers.clear(); } },
    running,
  );
  updateRunning();

  const reg = (id: string, fn: (arg?: unknown) => Promise<void>) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (arg?: unknown) => {
      try { await fn(arg); }
      catch (err) {
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`MSSQL-MCP: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  // From a connection node (arg = ConnectionNode) or the palette (pick an open connection).
  reg('newQuery', async arg => {
    const p = await pickProfile(store, arg, 'Connection for the new query', x => x.open);
    if (!p) return;
    if (!p.open) {
      void vscode.window.showWarningMessage(`MSSQL-MCP: open the connection '${p.name}' first.`);
      return;
    }
    // An empty mssql-sql: document titled "Query N - <server> - <database>" (not untitled: its tab shows the target).
    await lifecycle.createQuery(p);
  });

  // Palette and the explorer title: kept query windows (closed with text), newest first.
  reg('openRecentQuery', () => lifecycle.openRecentQuery(store));

  // The status bar item calls this without arguments (the active editor).
  reg('changeConnection', async () => {
    const editor = vscode.window.activeTextEditor;
    if (editor && isNeverBound(editor.document.uri.scheme)) {
      void vscode.window.showInformationMessage('MSSQL-MCP: a DDL view is read-only and is not bound to a connection. Use New Query to run SQL.');
      return;
    }
    const current = editor ? docs.get(editor.document.uri) : undefined;
    if (!editor || (editor.document.languageId !== 'sql' && !current)) {
      void vscode.window.showInformationMessage('MSSQL-MCP: open a SQL editor first.');
      return;
    }
    const placeHolder = current ? `Connection for this editor (now '${current.connection}')` : 'Connection for this editor';
    const p = await pickProfile(store, undefined, placeHolder, x => x.open);
    if (!p) return;
    const changed = !current || current.connection.toLowerCase() !== p.name.toLowerCase();
    const next: QueryAssociation = current?.kind === 'object'
      ? {
        ...current, connection: p.name, ...(current.object ? { object: { ...current.object, connection: p.name } } : {}),
        // The script keeps its recorded origin; Run asks before applying it through another connection.
        ...(changed || current.rebound ? { rebound: true } : {}),
      }
      : { connection: p.name, kind: 'query' };
    await docs.set(editor.document.uri, next);
    if (current?.kind === 'object' && changed) {
      void vscode.window.showInformationMessage(`MSSQL-MCP: this script was generated from ${describeTarget(current.target)}. `
        + `The next run asks you to confirm before it is applied through '${p.name}' (${describeTarget(targetOf(p))}).`);
    }
  });

  // F5 (the active editor) / the editor title's Run button (arg = that editor's uri, also in an inactive group).
  reg('runQuery', async arg => {
    const target = commandTarget(arg);
    const run = target ? tracker.contextOf(target.document) : undefined;
    if (!target || !run?.assoc) {
      void vscode.window.showInformationMessage('MSSQL-MCP: open a SQL editor bound to a connection first (New Query or Change Connection).');
      return;
    }
    const { document, editor } = target;
    const { assoc, profile, state } = run;
    if (!state.canRun || !profile) {
      void vscode.window.showWarningMessage(`MSSQL-MCP: ${state.reason ?? 'this editor cannot run now.'}`);
      return;
    }
    const key = document.uri.toString();
    if (running.has(key)) {
      void vscode.window.showInformationMessage('A query is already running in this window.');
      return;
    }
    // Wrong-target guard: an object script generated from another server/database (or rebound) runs only when confirmed.
    const prompt = wrongTargetPrompt(assoc, profile);
    if (prompt && (await vscode.window.showWarningMessage(prompt, { modal: true }, 'Run')) !== 'Run') return;
    if (document.isClosed) return;
    const token = running.start(key);
    if (!token) return;
    updateRunning();
    const connection = profile.name;
    /** The last state this run set; updates stop once the run is no longer current (document closed). */
    let shown: ResultsState | undefined;
    const show = (state: ResultsState) => {
      if (!running.isCurrent(token)) return;
      shown = state;
      results.set(key, state);
    };
    try {
      if (assoc.kind === 'object' && document.isDirty && !(await document.save())) {
        void vscode.window.showWarningMessage('MSSQL-MCP: the document was not saved, so it was not run.');
        return;
      }
      const selection = editor?.selection;
      const wholeDocument = !selection || selection.isEmpty;
      const fullText = document.getText();
      const request = buildRunRequest(fullText, wholeDocument
        ? undefined
        : { text: document.getText(selection), startLine: selection.start.line });
      if (!request.script.trim()) {
        void vscode.window.showInformationMessage('MSSQL-MCP: nothing to run.');
        return;
      }
      const maxRows = clampMaxRows(vscode.workspace.getConfiguration('msSqlMcp').get('query.maxRows'));
      show({ kind: 'running', connection, startedAt: Date.now() });
      if (editor) void results.reveal(editor).catch(err => log.debug('query', `Revealing the results view failed: ${String(err)}`));
      log.info('query', `run_script on '${connection}' (${request.script.length} chars, maxRows ${maxRows})`);
      try {
        const payload = await deps.runner.callResult(connection, 'run_script', { script: request.script, maxRows },
          { timeoutMs: null, signal: token.controller.signal });
        const result = parseRunScriptResult(payload);
        show({ kind: 'done', connection, result, lineOffset: request.lineOffset });
        log.info('query', `run_script on '${connection}': ${result.resultSets.length} result set(s), `
          + `${result.messages.length} message(s), hadErrors=${result.hadErrors}, ${result.elapsedMs} ms`);
        // "Applied" only when a batch actually ran without errors (a comment-only script applies nothing).
        if (assoc.kind === 'object' && appliedSuccessfully(result)) {
          void vscode.window.showInformationMessage(`Applied to '${connection}'.`);
          deps.refreshTree();
          // The applied text is the new base, so a reopen from the tree no longer asks about it. Only a whole-document
          // run: after a selection run the rest may still be unapplied.
          const addr = SqlDocFileSystem.address(document.uri);
          if (wholeDocument && addr?.kind === 'object') {
            void deps.sqlDocs.writeBase(addr.id, fullText)
              .catch(err => log.warn('query', `Updating the base copy failed: ${err instanceof Error ? err.message : String(err)}`));
          }
        }
      } catch (err) {
        if (token.controller.signal.aborted || (err instanceof McpToolError && err.cancelled)) {
          show({ kind: 'cancelled', connection });
        } else {
          log.error('query', `run_script on '${connection}' failed`, err);
          show({ kind: 'failed', connection, error: err instanceof Error ? err.message : String(err) });
        }
      }
    } finally {
      running.finish(token);
      // Closed while running: its results must not outlive it (close() already dropped the guard).
      if (document.isClosed && !openDocumentKeys().has(key) && shown !== undefined && results.stateOf(key) === shown) {
        results.forget(key);
      }
      updateRunning();
    }
  });

  // Editor title (arg = the editor's uri) and the palette; the results view's Cancel button calls `cancel` directly.
  reg('cancelQuery', async arg => {
    const key = arg instanceof vscode.Uri ? arg.toString() : vscode.window.activeTextEditor?.document.uri.toString();
    if (key) cancel(key);
  });

  // The disabled Run button of object documents on a read-only connection ("enablement": "false"): its title is the
  // explanation. Should it ever be invoked anyway, it shows the same warning as F5 (for the uri's document).
  reg('runQueryReadOnly', async arg => {
    const target = commandTarget(arg);
    const reason = target ? tracker.contextOf(target.document).state.reason : undefined;
    void vscode.window.showWarningMessage(`MSSQL-MCP: ${reason ?? 'apply changes on a read-write connection.'}`);
  });

  return { docs, tracker, results };
}
