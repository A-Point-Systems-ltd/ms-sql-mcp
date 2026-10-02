import * as vscode from 'vscode';
import { McpToolError } from '../client/parse';
import type { ServerProcessClient } from '../client/serverProcessClient';
import type { ConnectionProfile } from '../connections/profile';
import type { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';
import { findProfile } from './editorState';
import {
  COMPLETION_TRIGGER_CHARACTERS, CompletionTrigger, INTELLISENSE_SETTING, LANGUAGE_SERVICE_TIMEOUT_MS, LANGUAGE_SERVICE_TOOL,
  SIGNATURE_RETRIGGER_CHARACTERS, SIGNATURE_TRIGGER_CHARACTERS, WarmTracker, buildPositionRequest, completionKindName,
  intellisenseDocs, intellisenseEnabled, isExplicitCompletion, loadingMessage, parseCompletionResult, parseHoverResult,
  parseSignatureHelpResult, refreshedMessage, replaceEnd, replaceStart, requestDelayMs, warmKey,
} from './intellisense';
import { QueryDocuments, isNeverBound } from './queryDocuments';
import { contextSetter } from './queryCommands';

/** Per-document key for the editor title's Refresh IntelliSense Cache button. */
const DOCS_KEY = 'msSqlMcp.intellisenseDocs';

export interface IntelliSenseDeps {
  runner: ServerProcessClient;
  docs: QueryDocuments;
  store: ConnectionStore;
  log: Logger;
}

/** Waits `ms`; false when the token was cancelled first (a further keystroke). */
function delay(ms: number, token: vscode.CancellationToken): Promise<boolean> {
  if (ms <= 0) return Promise.resolve(!token.isCancellationRequested);
  return new Promise(resolve => {
    const timer = setTimeout(() => { sub.dispose(); resolve(!token.isCancellationRequested); }, ms);
    const sub = token.onCancellationRequested(() => { clearTimeout(timer); sub.dispose(); resolve(false); });
  });
}

const TRIGGERS: Record<vscode.CompletionTriggerKind, CompletionTrigger> = {
  [vscode.CompletionTriggerKind.Invoke]: 'invoke',
  [vscode.CompletionTriggerKind.TriggerCharacter]: 'triggerCharacter',
  [vscode.CompletionTriggerKind.TriggerForIncompleteCompletions]: 'incomplete',
};

/**
 * Registers SQL completion, hover and signature help for documents bound to an open connection (any scheme), the
 * warm-up on bind, and `msSqlMcp.refreshIntelliSense`. Other SQL extensions' providers stay active: VS Code merges
 * the lists.
 */
export function registerIntelliSense(context: vscode.ExtensionContext, deps: IntelliSenseDeps): void {
  const { runner, docs, store, log } = deps;
  const enabled = () => intellisenseEnabled(vscode.workspace.getConfiguration('msSqlMcp').get(INTELLISENSE_SETTING));
  const warmed = new WarmTracker();
  /** Last edit time per document (`uri.toString()`), to tell quick suggestions while typing from Ctrl+Space. */
  const lastEdit = new Map<string, number>();
  let loadingNote: vscode.Disposable | undefined;
  const setKey = contextSetter();

  /** The open profile a document is bound to, or undefined (unbound, removed, closed, or IntelliSense off). */
  const profileOf = (document: vscode.TextDocument): ConnectionProfile | undefined => {
    if (!enabled() || isNeverBound(document.uri.scheme)) return undefined;
    const assoc = docs.get(document.uri);
    const profile = assoc ? findProfile(store.list(), assoc.connection) : undefined;
    return profile?.open ? profile : undefined;
  };

  /** One language_service call; the token's cancellation aborts it (notifications/cancelled). */
  const call = async (profile: ConnectionProfile, args: Record<string, unknown>, token?: vscode.CancellationToken): Promise<unknown> => {
    const controller = new AbortController();
    const sub = token?.onCancellationRequested(() => controller.abort());
    if (token?.isCancellationRequested) controller.abort();
    try {
      return await runner.callResult(profile.name, LANGUAGE_SERVICE_TOOL, args,
        { timeoutMs: LANGUAGE_SERVICE_TIMEOUT_MS, signal: controller.signal });
    } finally {
      sub?.dispose();
    }
  };

  /** Runs a provider request; a cancellation or failure gives no result (failures are logged at debug). */
  const quietly = async <T>(what: string, fn: () => Promise<T | undefined>): Promise<T | undefined> => {
    try {
      return await fn();
    } catch (err) {
      if (!(err instanceof McpToolError && err.cancelled)) {
        log.debug('intellisense', `${what} failed: ${err instanceof Error ? err.message : String(err)}`);
      }
      return undefined;
    }
  };

  const warm = (profile: ConnectionProfile) => {
    if (!enabled() || !profile.open || !warmed.shouldWarm(warmKey(profile), Date.now())) return;
    void call(profile, { action: 'warm' })
      .catch(err => log.debug('intellisense', `warm failed: ${err instanceof Error ? err.message : String(err)}`));
  };

  const warmDocument = (key: string) => {
    const assoc = docs.get(key);
    const profile = assoc ? findProfile(store.list(), assoc.connection) : undefined;
    if (profile) warm(profile);
  };

  const showLoading = (profile: ConnectionProfile) => {
    loadingNote?.dispose();
    loadingNote = vscode.window.setStatusBarMessage(loadingMessage(profile), 4000);
  };

  const completion: vscode.CompletionItemProvider = {
    provideCompletionItems: (document, position, token, ctx) => quietly('completion', async () => {
      const profile = profileOf(document);
      if (!profile) return undefined;
      const since = lastEdit.has(document.uri.toString()) ? Date.now() - lastEdit.get(document.uri.toString())! : undefined;
      if (!(await delay(requestDelayMs(isExplicitCompletion(TRIGGERS[ctx.triggerKind] ?? 'invoke', since)), token))) return undefined;
      const payload = await call(profile, buildPositionRequest('completion', document.getText(), position), token);
      const list = parseCompletionResult(payload);
      if (!list || token.isCancellationRequested) return undefined;
      if (list.cacheState === 'loading') showLoading(profile);
      const lineText = document.lineAt(position.line).text;
      const start = replaceStart(lineText, position.character);
      const end = replaceEnd(lineText, position.character, start);
      // One plain range for both suggest insert modes: with an {inserting, replacing} pair VS Code's default
      // insertMode "insert" would use the caret-ending range and leave an auto-closed `]` behind ([T]]).
      // replaceEnd only extends over the `]` that closes the name being typed, so replacing it is always safe.
      const range = new vscode.Range(position.line, start, position.line, end);
      const items = list.items.map(i => {
        const item = new vscode.CompletionItem(i.label, vscode.CompletionItemKind[completionKindName(i.kind)]);
        item.detail = i.detail;
        item.sortText = i.sortText;
        item.insertText = i.insertText;
        // Matched against what is typed in the range, which includes a leading @ or [.
        item.filterText = i.insertText;
        item.range = range;
        return item;
      });
      return new vscode.CompletionList(items, list.isIncomplete);
    }),
  };

  const hover: vscode.HoverProvider = {
    provideHover: (document, position, token) => quietly('hover', async () => {
      const profile = profileOf(document);
      if (!profile) return undefined;
      const info = parseHoverResult(await call(profile, buildPositionRequest('hover', document.getText(), position), token));
      if (!info || token.isCancellationRequested) return undefined;
      // Plain text from the server: a code block, never interpreted as markdown.
      const contents = new vscode.MarkdownString().appendCodeblock(info.contents, 'sql');
      const r = info.range;
      return new vscode.Hover(contents, r ? new vscode.Range(r.startLine - 1, r.startColumn - 1, r.endLine - 1, r.endColumn - 1) : undefined);
    }),
  };

  const signatureHelp: vscode.SignatureHelpProvider = {
    provideSignatureHelp: (document, position, token, ctx) => quietly('signatureHelp', async () => {
      const profile = profileOf(document);
      if (!profile) return undefined;
      const explicit = ctx.triggerKind === vscode.SignatureHelpTriggerKind.Invoke;
      if (!(await delay(requestDelayMs(explicit), token))) return undefined;
      const info = parseSignatureHelpResult(await call(profile, buildPositionRequest('signatureHelp', document.getText(), position), token));
      if (!info || token.isCancellationRequested) return undefined;
      const help = new vscode.SignatureHelp();
      help.signatures = info.signatures.map(s => {
        const sig = new vscode.SignatureInformation(s.label, s.documentation);
        sig.parameters = s.parameters.map(p => new vscode.ParameterInformation(p.label, p.documentation));
        return sig;
      });
      help.activeSignature = info.activeSignature;
      help.activeParameter = info.activeParameter;
      return help;
    }),
  };

  const selector: vscode.DocumentSelector = { language: 'sql' };
  const updateDocsKey = () => setKey(DOCS_KEY, intellisenseDocs(docs.all(), store.list()));
  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(selector, completion, ...COMPLETION_TRIGGER_CHARACTERS),
    vscode.languages.registerHoverProvider(selector, hover),
    vscode.languages.registerSignatureHelpProvider(selector, signatureHelp, {
      triggerCharacters: [...SIGNATURE_TRIGGER_CHARACTERS],
      retriggerCharacters: [...SIGNATURE_RETRIGGER_CHARACTERS],
    }),
    vscode.workspace.onDidChangeTextDocument(e => {
      if (e.contentChanges.length && e.document.languageId === 'sql') lastEdit.set(e.document.uri.toString(), Date.now());
    }),
    vscode.workspace.onDidCloseTextDocument(doc => lastEdit.delete(doc.uri.toString())),
    // New Query, editable DDL, Change Connection (and a retitle, which rebinds the same connection) set a binding.
    docs.onDidChange(key => {
      updateDocsKey();
      warmDocument(key);
    }),
    store.onDidChange(updateDocsKey),
    // A restored or reopened bound tab warms when it becomes active.
    vscode.window.onDidChangeActiveTextEditor(editor => {
      if (editor) warmDocument(editor.document.uri.toString());
    }),
    // A restarted runner has empty caches.
    runner.onDidReset(() => warmed.clear()),
    { dispose: () => loadingNote?.dispose() },
    vscode.commands.registerCommand('msSqlMcp.refreshIntelliSense', (arg?: unknown) => refresh(arg)),
  );
  updateDocsKey();

  /** Refresh IntelliSense Cache: editor title (arg = that editor's uri), context menu, palette, Ctrl+Shift+R. */
  async function refresh(arg?: unknown): Promise<void> {
    const key = arg instanceof vscode.Uri ? arg.toString() : vscode.window.activeTextEditor?.document.uri.toString();
    const assoc = key ? docs.get(key) : undefined;
    const profile = assoc ? findProfile(store.list(), assoc.connection) : undefined;
    if (!assoc) {
      void vscode.window.showInformationMessage('APoint-ms-sql: open a SQL editor bound to a connection first (New Query or Change Connection).');
      return;
    }
    if (!profile?.open) {
      void vscode.window.showWarningMessage(`APoint-ms-sql: the connection '${assoc.connection}' is ${profile ? 'closed: open it first' : 'removed'}.`);
      return;
    }
    if (!enabled()) {
      void vscode.window.showInformationMessage('APoint-ms-sql: IntelliSense is turned off (setting msSqlMcp.intellisense.enabled).');
      return;
    }
    try {
      await call(profile, { action: 'refresh' });
      // refresh also starts the rebuild: no separate warm for a while.
      warmed.mark(warmKey(profile), Date.now());
      void vscode.window.showInformationMessage(refreshedMessage(profile));
    } catch (err) {
      log.error('intellisense', 'Refreshing the IntelliSense cache failed', err);
      void vscode.window.showErrorMessage(`APoint-ms-sql: refreshing the IntelliSense cache failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
}
