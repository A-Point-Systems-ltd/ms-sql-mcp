import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import { Logger } from '../logger';
import type { QueryAssociation, QueryDocuments } from '../query/queryDocuments';
import { SqlDocFileSystem, openSqlDocs } from '../query/sqlDocFs';
import { LEGACY_EDITS_DIR, isLegacyEditPath, legacyEditsBackupName, objectDisplayName, profileTarget } from '../query/sqlDocNames';
import { ScriptTarget, targetOf } from '../query/targetGuard';
import type { ObjectRef } from './catalog';
import type { ExplorerClient } from './explorerClient';
import { KEEP_EDITS_BUTTON, REPLACE_BUTTON, objectDocId, reopenObjectDecision, unappliedEditsPrompt } from './objectEdit';
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
 * Opens a view / procedure / function as an editable `mssql-sql:/object/<id>/<schema.name - server - db>` document
 * bound to its connection, so Run / F5 applies it. The script comes from the read-only explorer process (form 'alter':
 * CREATE OR ALTER or ALTER by server version). The binding records `profile`'s server/database now, so Run can warn when
 * the connection later points elsewhere. A clean open document gets the new script through the file system provider
 * (never a programmatic text edit); a dirty one is only revealed. Saved edits that were never applied (the backing file
 * differs from its base copy, the last script loaded or applied) are replaced only after a modal "Replace"; "Keep my
 * edits" (or Escape) reveals them unchanged.
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
  // An open document of this object keeps its uri whatever its title (the title is display-only).
  const uri = openSqlDocs('object').get(id) ?? SqlDocFileSystem.uri('object', id, objectDisplayName(ref), profileTarget(profile, ref.connection));
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

  // Saved but unapplied edits are never replaced without asking.
  if (reopenObjectDecision(await sqlDocs.readBacking('object', id), await sqlDocs.readBase(id)) === 'ask') {
    const choice = await vscode.window.showWarningMessage(unappliedEditsPrompt(objectDisplayName(ref)), { modal: true }, REPLACE_BUTTON, KEEP_EDITS_BUTTON);
    if (choice !== REPLACE_BUTTON) {
      // The edits' origin is known only while the binding lasts; without it, Run asks before applying them.
      await docs.set(uri, objectBinding(ref, docs.get(uri)?.target));
      await vscode.window.showTextDocument(await sqlDocs.sqlDocument(await vscode.workspace.openTextDocument(uri)), { preview: false });
      return true;
    }
  }

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
    // The base copy follows the server script; if it cannot be written, the next reopen asks (never silently replaces).
    await sqlDocs.writeBase(id, ddl).catch(err => log.warn('ddl', `${label}: the base copy was not written (${err instanceof Error ? err.message : String(err)}).`));
  } catch (err) {
    log.warn('ddl', `Editable script for ${label} failed: ${err instanceof Error ? err.message : String(err)}`);
    return false;
  }

  // Bind before showing, so the Run button and status bar are right on the first paint.
  await docs.set(uri, objectBinding(ref, scriptedFrom));
  const doc = await vscode.workspace.openTextDocument(uri);
  // The one language path for mssql-sql documents (shared with the open listener: never changed twice).
  const sqlDoc = await sqlDocs.sqlDocument(doc);
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
 * `<globalStorage>/edits/` and renames that folder to `edits.old-<yyyyMMddHHmmss>` (never deleted, so saved but
 * unapplied legacy edits stay on disk; the new path is logged at info). Best-effort (a debug log line on failure).
 */
export async function removeLegacyEdits(storageRoot: string, docs: QueryDocuments, log: Logger): Promise<void> {
  try {
    await docs.prune(key => {
      const uri = vscode.Uri.parse(key);
      return uri.scheme !== 'file' || !isLegacyEditPath(uri.fsPath, storageRoot);
    });
    const legacy = path.join(storageRoot, LEGACY_EDITS_DIR);
    try {
      await fs.access(legacy);
    } catch {
      return;
    }
    const kept = path.join(storageRoot, legacyEditsBackupName(new Date()));
    await fs.rename(legacy, kept);
    log.info('ddl', `The edits folder of an earlier build was kept as ${kept}.`);
  } catch (err) {
    log.debug('ddl', `Removing the legacy edits folder failed: ${err instanceof Error ? err.message : String(err)}`);
  }
}
