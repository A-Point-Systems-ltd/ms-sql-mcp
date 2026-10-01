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
const DIRTY_MESSAGE = 'MSSQL-MCP: Unsaved edits kept - close the editor to reload from the server.';

const sameRef = (a: ObjectRef | undefined, b: ObjectRef): boolean =>
  !!a && a.connection === b.connection && a.scriptType === b.scriptType && (a.schema ?? '') === (b.schema ?? '') && a.name === b.name;

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
  const findOpen = () => vscode.workspace.textDocuments.find(d => d.uri.toString() === key);

  // The path is injective, so a file bound to a different object is a bug or a hand-copied file: never rebind it.
  const bound = docs.get(uri);
  if (bound && bound.kind === 'object' && !sameRef(bound.object, ref)) {
    log.warn('ddl', `${label}: the edit file is bound to a different object; not rebinding.`);
    void vscode.window.showWarningMessage(`MSSQL-MCP: the edit file for ${label} is bound to a different object. Close it and delete the file to continue.`);
    return true;
  }

  const keepDirty = async (doc: vscode.TextDocument): Promise<true> => {
    await docs.set(uri, { connection: ref.connection, kind: 'object', object: ref });
    await vscode.window.showTextDocument(doc, { preview: false });
    void vscode.window.showInformationMessage(DIRTY_MESSAGE);
    return true;
  };

  const first = findOpen();
  if (first?.isDirty) return keepDirty(first);

  let ddl: string;
  let warnings: string[];
  try {
    const result = await explorer.call(ref.connection, 'script_object', scriptArgs(ref, 'alter'));
    ddl = String(pick(result, 'ddl') ?? '');
    if (!ddl.trim()) throw new Error('The server returned an empty script.');
    const raw = pick(result, 'warnings');
    warnings = (Array.isArray(raw) ? raw : []).map(w => String(w)).filter(w => w.length);

    // The user may have started typing while the server call ran: re-check before touching the document.
    const open = findOpen();
    if (open?.isDirty) return keepDirty(open);
    if (open) {
      // Replace the text in the editor and save, so the editor shows the new script at once (a file write alone
      // reloads it only after the file watcher fires). Fall back to a plain write when the edit is refused.
      const edit = new vscode.WorkspaceEdit();
      edit.replace(uri, open.validateRange(new vscode.Range(0, 0, open.lineCount, 0)), ddl);
      if (!(await vscode.workspace.applyEdit(edit)) || !(await open.save())) {
        await fs.mkdir(path.dirname(uri.fsPath), { recursive: true });
        await fs.writeFile(uri.fsPath, ddl, 'utf8');
      }
    } else {
      await fs.mkdir(path.dirname(uri.fsPath), { recursive: true });
      await fs.writeFile(uri.fsPath, ddl, 'utf8');
    }
  } catch (err) {
    log.warn('ddl', `Editable script for ${label} failed: ${err instanceof Error ? err.message : String(err)}`);
    return false;
  }

  // Bind before showing, so the Run button and status bar are right on the first paint.
  await docs.set(uri, { connection: ref.connection, kind: 'object', object: ref });
  const doc = await vscode.workspace.openTextDocument(uri);
  const sqlDoc = doc.languageId === 'sql' ? doc : await vscode.languages.setTextDocumentLanguage(doc, 'sql');
  await vscode.window.showTextDocument(sqlDoc, { preview: false });
  if (warnings.length) {
    const text = warnings.join(' | ');
    log.info('ddl', `script_object warnings for ${label}: ${text}`);
    void vscode.window.showInformationMessage(`MSSQL-MCP: ${label} - ${text.length > MAX_WARNING_CHARS ? `${text.slice(0, MAX_WARNING_CHARS - 1)}… (full text in the MSSQL-MCP Output)` : text}`);
  }
  return true;
}
