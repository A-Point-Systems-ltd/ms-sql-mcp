import * as fs from 'fs';
import * as vscode from 'vscode';
import { pickProfile } from '../connections/connectionCommands';
import type { ConnectionProfile } from '../connections/profile';
import { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';
import { EditorRunState, editorRunState, findProfile } from './editorState';
import { QueryAssociation, QueryDocuments } from './queryDocuments';

/** Context keys describing the active editor (used by menus and keybindings). */
const CONTEXT_KEYS = {
  connected: 'msSqlMcp.editorConnected',
  canRun: 'msSqlMcp.editorCanRun',
  blockedReadOnly: 'msSqlMcp.editorRunBlockedReadOnly',
} as const;

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

  /** Association, profile and run state of `document`. */
  contextOf(document: vscode.TextDocument): DocumentRunContext {
    const assoc = this.docs.get(document.uri);
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
    const visible = !!active && (active.editor.document.languageId === 'sql' || !!active.assoc);
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
    if (uri.scheme === 'untitled') return open.has(key);
    if (uri.scheme === 'file') return fs.existsSync(uri.fsPath);
    return true;
  });
}

/** Keeps associations in step with document lifecycle: closed untitled documents and renamed files. */
function trackDocumentLifecycle(context: vscode.ExtensionContext, docs: QueryDocuments, log: Logger): void {
  const timers = new Set<NodeJS.Timeout>();
  context.subscriptions.push(
    vscode.workspace.onDidCloseTextDocument(doc => {
      if (doc.uri.scheme !== 'untitled' || !docs.get(doc.uri)) return;
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

/**
 * Creates the query-document association store and the editor tracker, and registers `msSqlMcp.newQuery` and
 * `msSqlMcp.changeConnection`.
 */
export function registerQueryCommands(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger): { docs: QueryDocuments; tracker: QueryEditorTracker } {
  const docs = new QueryDocuments(context.workspaceState);
  const tracker = new QueryEditorTracker(store, docs);
  context.subscriptions.push(docs, tracker);
  trackDocumentLifecycle(context, docs, log);

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

  return { docs, tracker };
}
