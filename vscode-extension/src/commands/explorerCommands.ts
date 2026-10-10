import * as vscode from 'vscode';
import type { ServerProcessClient } from '../client/serverProcessClient';
import { refreshDataViewColors, showDataView } from '../dataPanel';
import type { CellViewer } from '../grid/cellViewer';
import type { ObjectRef } from '../explorer/catalog';
import { openEditableDdl } from '../explorer/editableDdl';
import { copyNameText, dependentsSql, renameSql, renameUnsupported, renameWarning, validateNewName } from '../explorer/objectRename';
import { parseRunScriptResult } from '../query/runScript';
import { isEditable } from '../explorer/objectEdit';
import type { ConnectionStore } from '../connections/store';
import { findProfile } from '../query/editorState';
import type { QueryDocuments } from '../query/queryDocuments';
import type { SqlDocFileSystem } from '../query/sqlDocFs';
import type { DdlDocumentProvider } from '../explorer/ddlDocuments';
import type { ExplorerClient } from '../explorer/explorerClient';
import type { ExplorerNode, ExplorerTreeProvider } from '../explorer/explorerTree';
import { DDL_SCHEME, ddlUri } from '../explorer/sqlText';
import { DEFAULT_TOP, clampTop } from '../grid/gridModel';
import { Logger } from '../logger';

/** The filter is applied after this pause in typing (or at once with Enter). */
const FILTER_DEBOUNCE_MS = 200;

/** The object an explorer command acts on (object and child nodes only). */
function refOf(node: unknown): ObjectRef | undefined {
  const n = node as ExplorerNode | undefined;
  return n && (n.kind === 'object' || n.kind === 'child') ? n.ref : undefined;
}

/** The `msSqlMcp.dataViewRows` setting: Data View's initial TOP (1..10000, default 200). */
function dataViewRows(): number {
  return clampTop(vscode.workspace.getConfiguration('msSqlMcp').get<number>('dataViewRows', DEFAULT_TOP));
}

export function registerExplorerCommands(
  context: vscode.ExtensionContext,
  tree: ExplorerTreeProvider,
  explorer: ExplorerClient,
  treeView: vscode.TreeView<ExplorerNode>,
  ddl: DdlDocumentProvider,
  docs: QueryDocuments,
  store: ConnectionStore,
  log: Logger,
  sqlDocs: SqlDocFileSystem,
  runner: ServerProcessClient,
  viewer: CellViewer,
): void {
  const reg = (id: string, fn: (arg?: unknown) => Promise<void> | void) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (arg?: unknown) => {
      try { await fn(arg); }
      catch (err) {
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  const showDdl = async (arg: unknown, newTab: boolean): Promise<void> => {
    const ref = refOf(arg);
    if (!ref) {
      void vscode.window.showInformationMessage('APoint-ms-sql: select an object in the APoint-ms-sql tree.');
      return;
    }
    // Views, procedures and functions open as editable mssql-sql: documents bound to their connection (and to its
    // server/database at this moment, for the wrong-target guard). A failure, or a module with no definition to apply
    // (CLR, WITH ENCRYPTION), falls back to the read-only document (which shows the error or warning and offers Refresh).
    if (isEditable(ref.scriptType)
      && await openEditableDdl(sqlDocs, explorer, docs, log, ref, findProfile(store.list(), ref.connection))) return;
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.parse(ddlUri(ref)));
    const sqlDoc = await vscode.languages.setTextDocumentLanguage(doc, 'sql');
    // A preview tab is replaced by the next Show DDL; "New Tab" keeps it open.
    await vscode.window.showTextDocument(sqlDoc, { preview: !newTab });
  };
  reg('showDdl', arg => showDdl(arg, false));
  reg('showDdlNewTab', arg => showDdl(arg, true));

  const dataView = async (arg: unknown, replace: boolean): Promise<void> => {
    const ref = refOf(arg);
    if (!ref || (ref.scriptType !== 'Table' && ref.scriptType !== 'View')) {
      void vscode.window.showInformationMessage('APoint-ms-sql: select a table or view in the APoint-ms-sql tree.');
      return;
    }
    // The runner's run_script (a single generated SELECT, allowed on read-only connections too) returns column
    // names and types even for zero rows, and exact value encodings. Errors show in the panel.
    await showDataView(ref, dataViewRows(), {
      runner, explorer, viewer, log, extensionUri: context.extensionUri, colorOf: name => findProfile(store.list(), name)?.color,
      isReadOnly: name => findProfile(store.list(), name)?.readOnly ?? true,
    }, { replace });
  };
  // Show Data opens a new tab (or the one already showing the object); Replace Current Tab reuses the last active one.
  reg('dataView', arg => dataView(arg, false));
  reg('dataViewReplace', arg => dataView(arg, true));
  context.subscriptions.push(store.onDidChange(refreshDataViewColors));

  // Refresh: from a DDL editor's title (arg = its Uri) it re-scripts that document; from the tree it clears
  // the node's cache. Without an argument (tree title bar, palette) it refreshes the tree and an active DDL editor.
  reg('refresh', arg => {
    if (arg instanceof vscode.Uri) {
      ddl.reload(arg);
      return;
    }
    tree.refresh(arg as ExplorerNode | undefined);
    const active = vscode.window.activeTextEditor?.document.uri;
    if (!arg && active?.scheme === DDL_SCHEME) ddl.reload(active);
  });

  // The object filter: a title-bar button (or Ctrl+F in the tree) opens an input that filters as you type. The tree
  // view shows the active term in its title, and Clear Filter appears while one is set.
  const showFilter = () => {
    const term = tree.filter;
    treeView.description = term ? `filter: ${term}` : undefined;
    void vscode.commands.executeCommand('setContext', 'msSqlMcp.filterActive', term.length > 0);
  };
  showFilter();

  reg('filter', () => {
    const box = vscode.window.createInputBox();
    box.title = 'Filter objects by name';
    box.placeholder = 'Part of a name, e.g. Customer or dbo.Order (Esc keeps the filter, empty clears it)';
    box.value = tree.filter;
    let timer: NodeJS.Timeout | undefined;
    const apply = () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
      tree.setFilter(box.value);
      showFilter();
    };
    box.onDidChangeValue(() => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(apply, FILTER_DEBOUNCE_MS);
    });
    box.onDidAccept(() => { apply(); box.hide(); });
    box.onDidHide(() => { if (timer) apply(); box.dispose(); });
    box.show();
  });

  // Copy Name / Rename act on the clicked item, or on the selected one (F2 in the tree passes no argument).
  const targetRef = (arg: unknown): ObjectRef | undefined => refOf(arg) ?? refOf(treeView.selection[0]);

  reg('copyName', async arg => {
    const ref = targetRef(arg);
    if (!ref) return;
    const text = copyNameText(ref);
    await vscode.env.clipboard.writeText(text);
    vscode.window.setStatusBarMessage(`APoint-ms-sql: copied ${text}`, 2500);
  });

  reg('renameObject', async arg => {
    const ref = targetRef(arg);
    if (!ref) {
      void vscode.window.showInformationMessage('APoint-ms-sql: select an object in the APoint-ms-sql tree.');
      return;
    }
    const unsupported = renameUnsupported(ref);
    if (unsupported) {
      void vscode.window.showInformationMessage(`APoint-ms-sql: ${unsupported}`);
      return;
    }
    const profile = findProfile(store.list(), ref.connection);
    if (!profile?.open) {
      void vscode.window.showWarningMessage(`APoint-ms-sql: open the connection '${ref.connection}' first.`);
      return;
    }
    if (profile.readOnly) {
      void vscode.window.showWarningMessage(`APoint-ms-sql: '${ref.connection}' is read-only; objects cannot be renamed through it.`);
      return;
    }
    // A tree item cannot be edited in place (VS Code has no API for it): an input box prefilled with the name.
    const newName = await vscode.window.showInputBox({
      title: `Rename ${copyNameText(ref)}`,
      prompt: ref.schema ? `New name (the schema ${ref.schema} stays)` : 'New name',
      value: ref.name,
      valueSelection: [0, ref.name.length],
      validateInput: value => validateNewName(ref, value),
    });
    if (newName === undefined || validateNewName(ref, newName)) return;

    const dependents: string[] = [];
    const check = dependentsSql(ref);
    if (check) {
      try {
        const result = parseRunScriptResult(await runner.callResult(ref.connection, 'run_script', { script: check, maxRows: 20 }));
        for (const row of result.resultSets[0]?.rows ?? []) if (typeof row[0] === 'string') dependents.push(row[0]);
      } catch (err) {
        log.warn('renameObject', `Dependency check failed: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    const answer = await vscode.window.showWarningMessage(renameWarning(ref, newName, dependents), { modal: true }, 'Rename');
    if (answer !== 'Rename') return;

    const result = parseRunScriptResult(await runner.callResult(ref.connection, 'run_script', { script: renameSql(ref, newName), maxRows: 1 }));
    const error = result.messages.find(m => m.kind === 'error');
    if (result.hadErrors || error) {
      void vscode.window.showErrorMessage(`APoint-ms-sql: rename failed: ${error?.text ?? 'see the Output channel.'}`);
      return;
    }
    log.info('renameObject', `Renamed ${ref.scriptType} ${copyNameText(ref)} to ${newName} on '${ref.connection}'.`);
    vscode.window.setStatusBarMessage(`APoint-ms-sql: renamed to ${newName}`, 3000);
    tree.refresh({ kind: 'connection', profile });
  });

  reg('clearFilter', () => {
    tree.setFilter('');
    showFilter();
  });
}
