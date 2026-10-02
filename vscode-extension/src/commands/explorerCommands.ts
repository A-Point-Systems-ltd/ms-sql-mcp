import * as vscode from 'vscode';
import type { ServerProcessClient } from '../client/serverProcessClient';
import { showDataView } from '../dataPanel';
import type { ObjectRef } from '../explorer/catalog';
import { openEditableDdl } from '../explorer/editableDdl';
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
import type { ObjectFilterViewProvider } from '../tree/filterView';

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
  filterView: ObjectFilterViewProvider,
  ddl: DdlDocumentProvider,
  docs: QueryDocuments,
  store: ConnectionStore,
  log: Logger,
  sqlDocs: SqlDocFileSystem,
  runner: ServerProcessClient,
): void {
  const reg = (id: string, fn: (arg?: unknown) => Promise<void> | void) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (arg?: unknown) => {
      try { await fn(arg); }
      catch (err) {
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  reg('showDdl', async arg => {
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
    await vscode.window.showTextDocument(sqlDoc, { preview: true });
  });

  reg('dataView', async arg => {
    const ref = refOf(arg);
    if (!ref || (ref.scriptType !== 'Table' && ref.scriptType !== 'View')) {
      void vscode.window.showInformationMessage('APoint-ms-sql: select a table or view in the APoint-ms-sql tree.');
      return;
    }
    // The runner's run_script (a single generated SELECT, allowed on read-only connections too) returns column
    // names and types even for zero rows, and exact value encodings. Errors show in the panel.
    await showDataView(ref, dataViewRows(), { runner, log });
  });

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

  reg('clearFilter', () => {
    tree.setFilter('');
    filterView.sync('');
  });
}
