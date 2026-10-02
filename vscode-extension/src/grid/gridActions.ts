import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { viewerTitle } from './cellFormat';
import type { CellViewer } from './cellViewer';
import { buildCsv, buildJsonRow, buildTsv, exportConfirmText, exportFileName, exportedMessage, project } from './gridExport';
import { GridAction, GridColumn, cellAt, copiedMessage, copyText } from './gridModel';

/** The data a grid action works on: the extension's own copy of the rows on screen. */
export interface GridData {
  columns: readonly GridColumn[];
  rows: readonly (readonly unknown[])[];
  /** `schema.name` of a Data View, or "results" in the Results panel (viewer title, export file name). */
  objectName: string;
}

const EXPORT = 'Export';

async function toClipboard(text: string, status: string): Promise<void> {
  try {
    await vscode.env.clipboard.writeText(text);
    vscode.window.setStatusBarMessage(status, 2000);
  } catch (err) {
    void vscode.window.showWarningMessage(`APoint-ms-sql: copy failed: ${err instanceof Error ? err.message : String(err)}`);
  }
}

/**
 * Runs a validated copy / copy row / copy selection / viewer / export action. Values are resolved here from `data`;
 * the webview only sent indexes. Nothing is logged except counts, and nothing is sent anywhere: Export writes only
 * the file the user picks, after the modal confirmation.
 */
export async function runGridAction(action: Exclude<GridAction, { type: 'viewState' }>, data: GridData, viewer: CellViewer): Promise<void> {
  const cols = (idx: readonly number[]) => idx.map(i => data.columns[i]);
  switch (action.type) {
    case 'copy': {
      const text = copyText(cellAt(data.rows, action.row, action.col));
      return toClipboard(text, copiedMessage(text));
    }
    case 'copyRow': {
      const values = project(data.rows, [action.row], action.cols)[0];
      const text = action.format === 'json' ? buildJsonRow(cols(action.cols), values) : buildTsv(undefined, [values]);
      return toClipboard(text, 'Copied row');
    }
    case 'copySelection': {
      const text = buildTsv(cols(action.cols), project(data.rows, action.rows, action.cols));
      const cells = action.rows.length * action.cols.length;
      return toClipboard(text, `Copied ${cells.toLocaleString('en-US')} cell${cells === 1 ? '' : 's'}`);
    }
    case 'openCell': {
      const column = data.columns[action.col]?.name ?? '';
      return viewer.open(viewerTitle(column, action.row, data.objectName), cellAt(data.rows, action.row, action.col));
    }
    case 'export': {
      const n = action.rows.length;
      const ok = await vscode.window.showWarningMessage(exportConfirmText(n), { modal: true }, EXPORT);
      if (ok !== EXPORT) return;
      const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? os.homedir();
      const target = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.file(path.join(folder, exportFileName(data.objectName, new Date()))),
        filters: { 'CSV (UTF-8)': ['csv'] },
        saveLabel: 'Export',
      });
      if (!target) return;
      const csv = buildCsv(cols(action.cols), project(data.rows, action.rows, action.cols));
      await vscode.workspace.fs.writeFile(target, Buffer.from(csv, 'utf8'));
      void vscode.window.showInformationMessage(exportedMessage(n, target.fsPath));
      return;
    }
  }
}
