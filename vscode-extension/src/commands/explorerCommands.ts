import * as vscode from 'vscode';
import { showDataPreview } from '../dataPanel';
import { parseReadData, rowsToTable } from '../dataTable';
import type { ObjectRef } from '../explorer/catalog';
import type { ExplorerClient } from '../explorer/explorerClient';
import type { ExplorerNode, ExplorerTreeProvider } from '../explorer/explorerTree';
import { ddlUri, previewSql } from '../explorer/sqlText';
import { Logger } from '../logger';
import type { ObjectFilterViewProvider } from '../tree/filterView';

const DEFAULT_ROWS = 500;
const MAX_ROWS = 10_000;

/** The object an explorer command acts on (object and child nodes only). */
function refOf(node: unknown): ObjectRef | undefined {
  const n = node as ExplorerNode | undefined;
  return n && (n.kind === 'object' || n.kind === 'child') ? n.ref : undefined;
}

function dataViewRows(): number {
  const v = vscode.workspace.getConfiguration('msSqlMcp').get<number>('dataViewRows', DEFAULT_ROWS);
  return Number.isFinite(v) ? Math.min(MAX_ROWS, Math.max(1, Math.floor(v))) : DEFAULT_ROWS;
}

export function registerExplorerCommands(
  context: vscode.ExtensionContext,
  tree: ExplorerTreeProvider,
  explorer: ExplorerClient,
  filterView: ObjectFilterViewProvider,
  log: Logger,
): void {
  const reg = (id: string, fn: (arg?: unknown) => Promise<void> | void) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (arg?: unknown) => {
      try { await fn(arg); }
      catch (err) {
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`MSSQL-MCP: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  reg('showDdl', async arg => {
    const ref = refOf(arg);
    if (!ref) {
      void vscode.window.showInformationMessage('MSSQL-MCP: select an object in the MSSQL-MCP tree.');
      return;
    }
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.parse(ddlUri(ref)));
    const sqlDoc = await vscode.languages.setTextDocumentLanguage(doc, 'sql');
    await vscode.window.showTextDocument(sqlDoc, { preview: true });
  });

  reg('dataView', async arg => {
    const ref = refOf(arg);
    if (!ref || (ref.scriptType !== 'Table' && ref.scriptType !== 'View')) {
      void vscode.window.showInformationMessage('MSSQL-MCP: select a table or view in the MSSQL-MCP tree.');
      return;
    }
    const rows = dataViewRows();
    const title = ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
    const payload = await vscode.window.withProgress(
      { location: { viewId: 'msSqlMcp.explorer' }, title: `Loading ${title}` },
      () => explorer.callResult(ref.connection, 'read_data', { sql: previewSql(ref.schema, ref.name, rows), maxRows: rows }));
    const result = parseReadData(payload);
    showDataPreview(title, ref.connection, rowsToTable(result.rows), result.truncated);
  });

  reg('refresh', arg => tree.refresh(arg as ExplorerNode | undefined));

  reg('clearFilter', () => {
    tree.setFilter('');
    filterView.sync('');
  });
}
