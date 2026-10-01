import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { pick } from '../client/parse';
import { Logger } from '../logger';
import type { QueryDocuments } from '../query/queryDocuments';
import type { ObjectRef } from './catalog';
import type { ExplorerClient } from './explorerClient';
import { editFilePath } from './objectEdit';
import { scriptArgs } from './treeModel';

const MAX_WARNING_CHARS = 600;

/**
 * Opens a view / procedure / function as an editable file bound to its connection, so Run / F5 applies it.
 * The script comes from the read-only explorer process (form 'alter': CREATE OR ALTER or ALTER by server version).
 * Returns false when scripting or writing failed; the caller then shows the read-only error document.
 */
export async function openEditableDdl(
  context: vscode.ExtensionContext,
  explorer: ExplorerClient,
  docs: QueryDocuments,
  log: Logger,
  ref: ObjectRef,
): Promise<boolean> {
  const label = `${ref.scriptType} ${ref.schema ? `${ref.schema}.` : ''}${ref.name} from ${ref.connection}`;
  const uri = vscode.Uri.file(editFilePath(context.globalStorageUri.fsPath, ref));
  const key = uri.toString();
  const open = vscode.workspace.textDocuments.find(d => d.uri.toString() === key);

  if (open?.isDirty) {
    await docs.set(uri, { connection: ref.connection, kind: 'object', object: ref });
    await vscode.window.showTextDocument(open, { preview: false });
    void vscode.window.showInformationMessage('MSSQL-MCP: Unsaved edits kept - close the editor to reload from the server.');
    return true;
  }

  let ddl: string;
  let warnings: string[];
  try {
    const result = await explorer.call(ref.connection, 'script_object', scriptArgs(ref, 'alter'));
    ddl = String(pick(result, 'ddl') ?? '');
    if (!ddl.trim()) throw new Error('The server returned an empty script.');
    const raw = pick(result, 'warnings');
    warnings = (Array.isArray(raw) ? raw : []).map(w => String(w)).filter(w => w.length);
    await fs.mkdir(path.dirname(uri.fsPath), { recursive: true });
    await fs.writeFile(uri.fsPath, ddl, 'utf8');
  } catch (err) {
    log.warn('ddl', `Editable script for ${label} failed: ${err instanceof Error ? err.message : String(err)}`);
    return false;
  }

  // Bind before showing, so the Run button and status bar are right on the first paint. A non-dirty open
  // document reloads from the file that was just written.
  await docs.set(uri, { connection: ref.connection, kind: 'object', object: ref });
  const doc = await vscode.workspace.openTextDocument(uri);
  const sqlDoc = doc.languageId === 'sql' ? doc : await vscode.languages.setTextDocumentLanguage(doc, 'sql');
  await vscode.window.showTextDocument(sqlDoc, { preview: false });
  if (warnings.length) {
    const text = warnings.join(' | ');
    void vscode.window.showInformationMessage(`MSSQL-MCP: ${label} - ${text.length > MAX_WARNING_CHARS ? `${text.slice(0, MAX_WARNING_CHARS - 1)}…` : text}`);
  }
  return true;
}
