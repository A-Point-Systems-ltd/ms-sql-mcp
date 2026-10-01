import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import { Logger } from '../logger';
import type { QueryAssociation, QueryDocuments } from '../query/queryDocuments';
import { SqlDocFileSystem } from '../query/sqlDocFs';
import { LEGACY_EDITS_DIR, isLegacyEditPath, objectDisplayName, profileTarget } from '../query/sqlDocNames';
import { ScriptTarget, targetOf } from '../query/targetGuard';
import type { ObjectRef } from './catalog';
import type { ExplorerClient } from './explorerClient';
import { objectDocId } from './objectEdit';
import { definitionUnavailable } from './sqlText';
import { scriptArgs } from './treeModel';

const MAX_WARNING_CHARS = 600;
const DIRTY_MESSAGE = 'MSSQL-MCP: Unsaved edits kept - close the editor to reload from the server.';

const sameRef = (a: ObjectRef | undefined, b: ObjectRef): boolean =>
  !!a && a.connection === b.connection && a.scriptType === b.scriptType && (a.schema ?? '') === (b.schema ?? '') && a.name === b.name;

/** The binding of an object document scripted from `target` (undefined when the profile is gone: Run then asks). */
const objectBinding = (ref: ObjectRef, target: ScriptTarget | undefined): QueryAssociation =>
  ({ connection: ref.connection, kind: 'object', object: ref, ...(target ? { target } : {}) });

/**
 * The uri of an open document (or tab) of object document `id`, whatever its title: the title is display-only, so a
 * document opened before the profile's server/database changed is still the same document.
 */
function openObjectUri(id: string): vscode.Uri | undefined {
  const matches = (uri: vscode.Uri) => {
    const addr = SqlDocFileSystem.address(uri);
    return addr?.kind === 'object' && addr.id === id;
  };
  const doc = vscode.workspace.textDocuments.find(d => matches(d.uri));
  if (doc) return doc.uri;
  for (const group of vscode.window.tabGroups.all) {
    for (const tab of group.tabs) {
      if (tab.input instanceof vscode.TabInputText && matches(tab.input.uri)) return tab.input.uri;
    }
  }
  return undefined;
}

/**
 * Opens a view / procedure / function as an editable `mssql-sql:/object/<id>/<schema.name - server - db>.sql` document
 * bound to its connection, so Run / F5 applies it. The script comes from the read-only explorer process (form 'alter':
 * CREATE OR ALTER or ALTER by server version). The binding records `profile`'s server/database now, so Run can warn when
 * the connection later points elsewhere. A clean open document gets the new script through the file system provider
 * (never a programmatic text edit); a dirty one is only revealed.
 * Returns false when scripting or writing failed, or when there is no definition to apply (CLR, WITH ENCRYPTION:
 * a comment-only script); the caller then shows the read-only document with its error or warning.
 */
export async function openEditableDdl(
  sqlDocs: SqlDocFileSystem,
  explorer: ExplorerClient,
  docs: QueryDocuments,
  log: Logger,
  ref: ObjectRef,
  profile: ConnectionProfile | undefined,
): Promise<boolean> {
  const scriptedFrom = profile ? targetOf(profile) : undefined;
  const label = `${ref.scriptType} ${objectDisplayName(ref)} from ${ref.connection}`;
  const id = objectDocId(ref);
  const uri = openObjectUri(id) ?? SqlDocFileSystem.uri('object', id, objectDisplayName(ref), profileTarget(profile, ref.connection));
  const key = uri.toString();
  const findOpen = () => vscode.workspace.textDocuments.find(d => d.uri.toString() === key);

  // The id is injective, so a document bound to a different object is a bug: never rebind it.
  const bound = docs.get(uri);
  if (bound && bound.kind === 'object' && !sameRef(bound.object, ref)) {
    log.warn('ddl', `${label}: the object document is bound to a different object; not rebinding.`);
    void vscode.window.showWarningMessage(`MSSQL-MCP: the document for ${label} is bound to a different object. Close it to continue.`);
    return true;
  }

  const keepDirty = async (doc: vscode.TextDocument): Promise<true> => {
    // The unsaved text was scripted earlier: keep the target recorded then.
    await docs.set(uri, objectBinding(ref, docs.get(uri)?.target ?? scriptedFrom));
    await vscode.window.showTextDocument(doc, { preview: false });
    void vscode.window.showInformationMessage(DIRTY_MESSAGE);
    return true;
  };

  const first = findOpen();
  if (first?.isDirty) return keepDirty(first);

  let warnings: string[];
  try {
    const result = await explorer.call(ref.connection, 'script_object', scriptArgs(ref, 'alter'));
    const ddl = String(pick(result, 'ddl') ?? '');
    if (!ddl.trim()) throw new Error('The server returned an empty script.');
    const raw = pick(result, 'warnings');
    warnings = (Array.isArray(raw) ? raw : []).map(w => String(w)).filter(w => w.length);
    if (definitionUnavailable(ddl, warnings)) {
      log.info('ddl', `${label}: no definition to edit (${warnings.join(' | ') || 'comment-only script'}); opening it read-only.`);
      return false;
    }

    // The user may have started typing while the server call ran: re-check before touching the document.
    const open = findOpen();
    if (open?.isDirty) return keepDirty(open);
    // Through the provider: a clean open document reloads from the change event, as for a file changed on disk.
    await sqlDocs.writeContent(uri, ddl);
  } catch (err) {
    log.warn('ddl', `Editable script for ${label} failed: ${err instanceof Error ? err.message : String(err)}`);
    return false;
  }

  // Bind before showing, so the Run button and status bar are right on the first paint.
  await docs.set(uri, objectBinding(ref, scriptedFrom));
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

/**
 * Migration from the `file:` edit files of earlier builds: drops the associations of files under
 * `<globalStorage>/edits/` and deletes that folder. Best-effort and silent (a debug log line on failure).
 */
export async function removeLegacyEdits(storageRoot: string, docs: QueryDocuments, log: Logger): Promise<void> {
  try {
    await docs.prune(key => {
      const uri = vscode.Uri.parse(key);
      return uri.scheme !== 'file' || !isLegacyEditPath(uri.fsPath, storageRoot);
    });
    await fs.rm(path.join(storageRoot, LEGACY_EDITS_DIR), { recursive: true, force: true });
  } catch (err) {
    log.debug('ddl', `Removing the legacy edits folder failed: ${err instanceof Error ? err.message : String(err)}`);
  }
}
