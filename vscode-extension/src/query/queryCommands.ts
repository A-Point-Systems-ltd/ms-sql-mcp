import * as fs from 'fs';
import * as vscode from 'vscode';
import { McpToolError } from '../client/parse';
import type { ServerProcessClient } from '../client/serverProcessClient';
import { pickProfile } from '../connections/connectionCommands';
import type { ConnectionProfile } from '../connections/profile';
import { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';
import { EditorRunState, editorRunState, findProfile } from './editorState';
import { QueryAssociation, QueryDocuments, dropOnClose, isNeverBound, keepOnActivation } from './queryDocuments';
import { RESULTS_VIEW_ID, ResultsViewProvider } from './resultsView';
import type { ResultsState } from './resultsHtml';
import { RunRegistry } from './runRegistry';
import { buildRunRequest, clampMaxRows, parseRunScriptResult } from './runScript';

/** Context keys describing the active editor (used by menus and keybindings). */
const CONTEXT_KEYS = {
  connected: 'msSqlMcp.editorConnected',
  canRun: 'msSqlMcp.editorCanRun',
  blockedReadOnly: 'msSqlMcp.editorRunBlockedReadOnly',
} as const;

/** True while the active document's query runs (shows Cancel). */
const RUNNING_KEY = 'msSqlMcp.queryRunning';

/** A closed untitled document reopens at once when only its language changed; wait this long before forgetting it. */
const REOPEN_GRACE_MS = 200;

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
  private lastKeys: Record<string, boolean> = {};

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
    this.setKeys({
      [CONTEXT_KEYS.connected]: !!state?.connected,
      [CONTEXT_KEYS.canRun]: !!state?.canRun,
      [CONTEXT_KEYS.blockedReadOnly]: !!state?.blockedReadOnly,
    });
    this.emitter.fire();
  }

  dispose(): void {
    for (const s of this.subs) s.dispose();
  }

  private setKeys(keys: Record<string, boolean>): void {
    for (const [key, value] of Object.entries(keys)) {
      if (this.lastKeys[key] === value) continue;
      this.lastKeys[key] = value;
      void vscode.commands.executeCommand('setContext', key, value);
    }
  }
}

/** Untitled documents whose editor is gone and files that no longer exist lose their association. */
async function pruneStale(docs: QueryDocuments): Promise<void> {
  const open = new Set(vscode.workspace.textDocuments.map(d => d.uri.toString()));
  // Restored editors in background tabs may not have a loaded document yet.
  for (const group of vscode.window.tabGroups.all) {
    for (const tab of group.tabs) {
      if (tab.input instanceof vscode.TabInputText) open.add(tab.input.uri.toString());
    }
  }
  await docs.prune(key => {
    const uri = vscode.Uri.parse(key);
    return keepOnActivation(uri.scheme, open.has(key), () => fs.existsSync(uri.fsPath));
  });
}

/**
 * Keeps associations in step with document lifecycle: closed documents other than files (untitled and other
 * schemes) and renamed files.
 */
function trackDocumentLifecycle(context: vscode.ExtensionContext, docs: QueryDocuments, log: Logger): void {
  const timers = new Set<NodeJS.Timeout>();
  context.subscriptions.push(
    vscode.workspace.onDidCloseTextDocument(doc => {
      if (!dropOnClose(doc.uri.scheme) || !docs.get(doc.uri)) return;
      const key = doc.uri.toString();
      const timer = setTimeout(() => {
        timers.delete(timer);
        if (vscode.workspace.textDocuments.some(d => d.uri.toString() === key)) return;
        void docs.delete(key);
      }, REOPEN_GRACE_MS);
      timers.add(timer);
    }),
    vscode.workspace.onDidRenameFiles(e => {
      for (const { oldUri, newUri } of e.files) {
        const oldKey = oldUri.toString();
        const newKey = newUri.toString();
        for (const [key] of docs.all()) {
          // The file itself, or a file inside a renamed folder.
          if (key === oldKey) void docs.rename(key, newKey);
          else if (key.startsWith(`${oldKey}/`)) void docs.rename(key, newKey + key.slice(oldKey.length));
        }
      }
    }),
    { dispose: () => { for (const t of timers) clearTimeout(t); timers.clear(); } },
  );
  void pruneStale(docs).catch(err => log.error('query', 'Pruning stale query document associations failed', err));
}

/** What the run command needs beyond the store: the runner process and a way to refresh the object tree. */
export interface QueryRunDeps {
  runner: ServerProcessClient;
  /** Refreshes the object tree after an object document was applied. */
  refreshTree: () => void;
}

/**
 * Creates the query-document association store, the editor tracker and the results view, and registers
 * `msSqlMcp.newQuery`, `msSqlMcp.changeConnection`, `msSqlMcp.runQuery`, `msSqlMcp.cancelQuery` and
 * `msSqlMcp.runQueryReadOnly`.
 */
export function registerQueryCommands(
  context: vscode.ExtensionContext, store: ConnectionStore, log: Logger, deps: QueryRunDeps,
): { docs: QueryDocuments; tracker: QueryEditorTracker; results: ResultsViewProvider } {
  const docs = new QueryDocuments(context.workspaceState);
  const tracker = new QueryEditorTracker(store, docs);
  context.subscriptions.push(docs, tracker);
  trackDocumentLifecycle(context, docs, log);

  /** One run per document, each with its own token (see RunRegistry). */
  const running = new RunRegistry();
  const cancel = (key: string) => running.cancel(key);
  const results = new ResultsViewProvider(() => {
    const active = tracker.active();
    return active ? { key: active.editor.document.uri.toString(), bound: !!active.assoc } : undefined;
  }, cancel);
  let lastRunning: boolean | undefined;
  const updateRunning = () => {
    const editor = vscode.window.activeTextEditor;
    const value = !!editor && running.has(editor.document.uri.toString());
    if (value === lastRunning) return;
    lastRunning = value;
    void vscode.commands.executeCommand('setContext', RUNNING_KEY, value);
  };
  context.subscriptions.push(
    results,
    vscode.window.registerWebviewViewProvider(RESULTS_VIEW_ID, results, { webviewOptions: { retainContextWhenHidden: true } }),
    tracker.onDidChange(() => {
      updateRunning();
      results.update();
    }),
    // Results are kept in memory per document. Closing a document aborts its run and drops its results and guard at
    // once, so a new document reusing the uri (untitled) starts clean; the old run no longer updates anything.
    vscode.workspace.onDidCloseTextDocument(doc => {
      const key = doc.uri.toString();
      if (running.close(key)) {
        log.info('query', 'The document of a running query was closed: the run was cancelled.');
        updateRunning();
      }
      results.forget(key);
    }),
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
    const doc = await vscode.workspace.openTextDocument({ language: 'sql', content: '' });
    await docs.set(doc.uri, { connection: p.name, kind: 'query' });
    await vscode.window.showTextDocument(doc);
  });

  // The status bar item calls this without arguments; editor/title passes the uri, which is ignored on purpose.
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
    const next: QueryAssociation = current?.kind === 'object'
      ? { ...current, connection: p.name, ...(current.object ? { object: { ...current.object, connection: p.name } } : {}) }
      : { connection: p.name, kind: 'query' };
    await docs.set(editor.document.uri, next);
  });

  // F5 / the editor title's Run button.
  reg('runQuery', async () => {
    const active = tracker.active();
    if (!active?.assoc) {
      void vscode.window.showInformationMessage('MSSQL-MCP: open a SQL editor bound to a connection first (New Query or Change Connection).');
      return;
    }
    const { editor, assoc, profile, state } = active;
    if (!state.canRun || !profile) {
      void vscode.window.showWarningMessage(`MSSQL-MCP: ${state.reason ?? 'this editor cannot run now.'}`);
      return;
    }
    const document = editor.document;
    const key = document.uri.toString();
    if (running.has(key)) {
      void vscode.window.showInformationMessage('A query is already running in this window.');
      return;
    }
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
      const selection = editor.selection;
      const request = buildRunRequest(document.getText(), selection.isEmpty
        ? undefined
        : { text: document.getText(selection), startLine: selection.start.line });
      if (!request.script.trim()) {
        void vscode.window.showInformationMessage('MSSQL-MCP: nothing to run.');
        return;
      }
      const maxRows = clampMaxRows(vscode.workspace.getConfiguration('msSqlMcp').get('query.maxRows'));
      show({ kind: 'running', connection, startedAt: Date.now() });
      void results.reveal(editor).catch(err => log.debug('query', `Revealing the results view failed: ${String(err)}`));
      log.info('query', `run_script on '${connection}' (${request.script.length} chars, maxRows ${maxRows})`);
      try {
        const payload = await deps.runner.callResult(connection, 'run_script', { script: request.script, maxRows },
          { timeoutMs: null, signal: token.controller.signal });
        const result = parseRunScriptResult(payload);
        show({ kind: 'done', connection, result, lineOffset: request.lineOffset });
        log.info('query', `run_script on '${connection}': ${result.resultSets.length} result set(s), `
          + `${result.messages.length} message(s), hadErrors=${result.hadErrors}, ${result.elapsedMs} ms`);
        if (assoc.kind === 'object' && !result.hadErrors) {
          void vscode.window.showInformationMessage(`Applied to '${connection}'.`);
          deps.refreshTree();
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
      if (document.isClosed && shown !== undefined && results.stateOf(key) === shown) results.forget(key);
      updateRunning();
    }
  });

  // Editor title (arg = the editor's uri) and the palette; the results view's Cancel button calls `cancel` directly.
  reg('cancelQuery', async arg => {
    const key = arg instanceof vscode.Uri ? arg.toString() : vscode.window.activeTextEditor?.document.uri.toString();
    if (key) cancel(key);
  });

  // The disabled Run button of object documents on a read-only connection ("enablement": "false"): its title is the
  // explanation. Should it ever be invoked anyway, it shows the same warning as F5.
  reg('runQueryReadOnly', async () => {
    const reason = tracker.active()?.state.reason;
    void vscode.window.showWarningMessage(`MSSQL-MCP: ${reason ?? 'apply changes on a read-write connection.'}`);
  });

  return { docs, tracker, results };
}
