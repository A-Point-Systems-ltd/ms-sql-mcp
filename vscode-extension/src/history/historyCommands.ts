import * as vscode from 'vscode';
import type { ServerProcessClient } from '../client/serverProcessClient';
import type { ConnectionProfile } from '../connections/profile';
import type { ConnectionStore } from '../connections/store';
import { errorMessage } from '../errorFormat';
import type { ObjectRef } from '../explorer/catalog';
import type { ExplorerClient } from '../explorer/explorerClient';
import type { ExplorerNode } from '../explorer/treeModel';
import { DDL_SCHEME, parseDdlUri } from '../explorer/sqlText';
import { Logger } from '../logger';
import { findProfile } from '../query/editorState';
import type { QueryDocuments } from '../query/queryDocuments';
import { openDocumentUris } from '../query/sqlDocFs';
import { SQL_DOC_SCHEME, objectDisplayName } from '../query/sqlDocNames';
import { HistoryDocumentProvider } from './historyDocs';
import {
  HISTORY_SCHEME, HISTORY_TOOL, HistoryEntry, LIST_TOP, LOGGING_SUPPRESSED_WARNING, SET_UP_BUTTON, currentDiffTitle, disabledWarning, emptyHistoryOutcome,
  entryDiffTitle, filterEntries, historyDocKeys, historyPickItems, historyUri, isModuleType, isNotInstalledError,
  latestDefinitionEntry, moreNotLoaded, noHistoryMessage, parseHistoryEntries, parseHistoryStatus, previousEntry, statusTargetText,
  supportsHistory, targetText, triggerMissingWarning,
} from './historyModel';

/** Per-document key for the editor title's Show DDL History button (`resource in msSqlMcp.historyDocs`). */
const HISTORY_DOCS_KEY = 'msSqlMcp.historyDocs';

/** The object of a tree node (object and child nodes only). */
function refOfNode(arg: unknown): ObjectRef | undefined {
  const n = arg as ExplorerNode | undefined;
  return n && typeof n === 'object' && (n.kind === 'object' || n.kind === 'child') ? n.ref : undefined;
}

/** The object of an editable object document (through its binding) or of a read-only `mssql-ddl:` document. */
function refOfUri(uri: vscode.Uri, docs: QueryDocuments): ObjectRef | undefined {
  if (uri.scheme === SQL_DOC_SCHEME) {
    const assoc = docs.get(uri);
    // A rebound document (Change Connection) shows the history of the connection it now runs on.
    return assoc?.kind === 'object' && assoc.object ? { ...assoc.object, connection: assoc.connection } : undefined;
  }
  if (uri.scheme === DDL_SCHEME) {
    try { return parseDdlUri(uri.toString()); } catch { return undefined; }
  }
  return undefined;
}

export interface HistoryDeps {
  store: ConnectionStore;
  docs: QueryDocuments;
  /** Serves `ddl_history` (only the runner process has it). */
  runner: ServerProcessClient;
  /** Reads the current module definition (read-only `read_data`). */
  explorer: ExplorerClient;
  log: Logger;
}

/**
 * Registers the `mssql-history:` content provider, `msSqlMcp.showHistory`, and keeps the `msSqlMcp.historyDocs` key
 * (open object / DDL documents whose connection has DDL history) current.
 */
export function registerHistoryCommands(context: vscode.ExtensionContext, deps: HistoryDeps): void {
  const { store, docs, runner, explorer, log } = deps;
  context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider(HISTORY_SCHEME, new HistoryDocumentProvider(runner, explorer, log)));

  let last = '';
  const updateKey = () => {
    const ddlDocs: [string, ObjectRef][] = [];
    for (const uri of openDocumentUris()) {
      if (uri.scheme !== DDL_SCHEME) continue;
      const ref = refOfUri(uri, docs);
      if (ref) ddlDocs.push([uri.toString(), ref]);
    }
    const keys = historyDocKeys(docs.all(), ddlDocs, store.list());
    const json = JSON.stringify(keys);
    if (json === last) return;
    last = json;
    void vscode.commands.executeCommand('setContext', HISTORY_DOCS_KEY, keys);
  };
  context.subscriptions.push(
    docs.onDidChange(updateKey),
    store.onDidChange(updateKey),
    vscode.window.onDidChangeActiveTextEditor(updateKey),
    vscode.workspace.onDidOpenTextDocument(updateKey),
    vscode.workspace.onDidCloseTextDocument(updateKey),
    vscode.window.tabGroups.onDidChangeTabs(updateKey),
  );
  updateKey();

  context.subscriptions.push(vscode.commands.registerCommand('msSqlMcp.showHistory', async (arg?: unknown) => {
    try {
      await showHistory(arg);
    } catch (err) {
      log.error('showHistory', 'Command failed', err);
      void vscode.window.showErrorMessage(`APoint-ms-sql: ${errorMessage(err)}`);
    }
  }));

  async function showHistory(arg: unknown): Promise<void> {
    const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
    const ref = refOfNode(arg) ?? (uri ? refOfUri(uri, docs) : undefined);
    if (!ref || !supportsHistory(ref.scriptType)) {
      void vscode.window.showInformationMessage('APoint-ms-sql: select a table, view, procedure, function, trigger or type in the tree, or open its SQL document.');
      return;
    }
    const profile = findProfile(store.list(), ref.connection);
    if (!profile) {
      void vscode.window.showErrorMessage(`APoint-ms-sql: connection '${ref.connection}' was not found.`);
      return;
    }
    if (profile.ddlHistory !== true) {
      void vscode.window.showInformationMessage(`APoint-ms-sql: DDL history is off for '${profile.name}'. Turn it on in Edit Connection.`);
      return;
    }
    if (!profile.open) {
      void vscode.window.showWarningMessage(`APoint-ms-sql: open the connection '${profile.name}' first.`);
      return;
    }
    const obj = objectDisplayName(ref);
    const connection = profile.name;

    const where = targetText(profile);
    const offerSetUp = async (message: string) => {
      const choice = await vscode.window.showWarningMessage(message, SET_UP_BUTTON);
      if (choice === SET_UP_BUTTON) await vscode.commands.executeCommand('msSqlMcp.editConnection', { name: connection, setUpHistory: true });
    };

    let entries: HistoryEntry[];
    let listed: number;
    try {
      entries = parseHistoryEntries(await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: `Loading DDL history of ${obj}` },
        () => runner.call(connection, HISTORY_TOOL, { action: 'list', name: ref.name, ...(ref.schema ? { schema: ref.schema } : {}), top: LIST_TOP })));
      listed = entries.length;
      // The server matches by name and schema only: keep this object type's entries.
      entries = filterEntries(entries, ref.scriptType);
    } catch (err) {
      const message = errorMessage(err);
      if (isNotInstalledError(message)) {
        await offerSetUp(`APoint-ms-sql '${connection}': ${message}`);
        return;
      }
      log.warn('history', `ddl_history list for ${obj} on '${connection}' failed: ${message}`);
      void vscode.window.showErrorMessage(`APoint-ms-sql: DDL history of ${obj} on '${connection}' failed: ${message}`);
      return;
    }
    log.debug('history', `ddl_history list for ${obj} on '${connection}': ${entries.length} of ${listed} entries`);
    if (!entries.length) {
      await explainEmpty(profile, obj, where, offerSetUp);
      return;
    }

    const items = historyPickItems(entries, isModuleType(ref.scriptType));
    const picked = await vscode.window.showQuickPick(items, {
      placeHolder: `DDL history of ${obj} on '${connection}', newest first`, matchOnDescription: true, matchOnDetail: true,
    });
    if (!picked) return;

    // The target is part of the uri: an edited connection (another server/db) never reuses cached text.
    const entryUri = (e: HistoryEntry) => vscode.Uri.parse(historyUri({ kind: 'entry', connection, target: where, id: e.id, label: `${obj} #${e.id}` }));
    if (picked.action.kind === 'current') {
      const latest = latestDefinitionEntry(entries);
      if (!latest) return;
      const current = vscode.Uri.parse(historyUri({
        kind: 'current', connection, target: where, object: { ...ref, connection }, label: `${obj} (current)`,
        nonce: `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`,
      }));
      await vscode.commands.executeCommand('vscode.diff', entryUri(latest), current, currentDiffTitle(obj, latest), { preview: true });
      return;
    }
    const index = picked.action.index;
    const prev = previousEntry(entries, index);
    const left = prev
      ? entryUri(prev)
      // The raw count: a full page may hide earlier entries even when the type filter dropped some of it.
      : vscode.Uri.parse(historyUri({ kind: 'empty', label: `${obj} (none)`, more: moreNotLoaded(listed) }));
    await vscode.commands.executeCommand('vscode.diff', left, entryUri(entries[index]), entryDiffTitle(obj, entries, index), { preview: true });
  }

  /**
   * No entry for the object: says why when the trigger is missing (with "Set up…") or disabled, else that nothing was
   * recorded. A failed status check falls back to the plain "no history" message.
   */
  async function explainEmpty(profile: ConnectionProfile, obj: string, where: string, offerSetUp: (message: string) => Promise<void>): Promise<void> {
    let outcome: ReturnType<typeof emptyHistoryOutcome> = 'noHistory';
    try {
      const status = parseHistoryStatus(await runner.call(profile.name, HISTORY_TOOL, { action: 'status' }));
      outcome = emptyHistoryOutcome(status);
      // Name the database the server reports, not the profile's idea of it.
      where = statusTargetText(status, profile);
    } catch (err) {
      log.warn('history', `ddl_history status on '${profile.name}' failed: ${errorMessage(err)}`);
    }
    if (outcome === 'triggerMissing') await offerSetUp(triggerMissingWarning(where));
    else if (outcome === 'triggerDisabled') void vscode.window.showWarningMessage(disabledWarning(where));
    else if (outcome === 'loggingSuppressed') void vscode.window.showWarningMessage(`DDL history on ${where}: ${LOGGING_SUPPRESSED_WARNING}`);
    else void vscode.window.showInformationMessage(noHistoryMessage(obj, profile.name));
  }
}
