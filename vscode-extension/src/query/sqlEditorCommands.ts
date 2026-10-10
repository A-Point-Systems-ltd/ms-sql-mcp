import * as vscode from 'vscode';
import { McpToolError } from '../client/parse';
import type { ServerProcessClient } from '../client/serverProcessClient';
import type { ConnectionProfile } from '../connections/profile';
import type { ConnectionStore } from '../connections/store';
import { DEFAULT_TOP, clampTop } from '../grid/gridModel';
import { Logger } from '../logger';
import { findProfile } from './editorState';
import { LANGUAGE_SERVICE_TIMEOUT_MS, LANGUAGE_SERVICE_TOOL } from './intellisense';
import { QueryDocuments, isNeverBound } from './queryDocuments';
import {
  ENHANCED_CONTEXT_KEY, ENHANCED_SETTING, FORMAT_FILES_SETTING, FORMAT_TOOL, LsEdit, formatterSchemes, SQL_SNIPPETS, enhancedEnabled, expandWildcard,
  formatRequest, formatSettings, objectNameAt, objectRefOf, parseFormatEdits, parseObjectInfo, parseScope,
  pickerItems, snippetHint, snippetPreview, tvfQuery, wildcardAt,
} from './sqlEditorFeatures';

export interface SqlEditorDeps {
  runner: ServerProcessClient;
  docs: QueryDocuments;
  store: ConnectionStore;
  log: Logger;
}

/** Formatting waits longer than IntelliSense: a large script parses twice (format, then the safety check). */
const FORMAT_TIMEOUT_MS = 30_000;

/** Scope (column picker, * expansion) may wait for the catalog snapshot to load. */
const SCOPE_TIMEOUT_MS = 20_000;

const config = () => vscode.workspace.getConfiguration('msSqlMcp');
const enhanced = () => enhancedEnabled(config().get(ENHANCED_SETTING));

const toRange = (e: LsEdit) => new vscode.Range(e.startLine - 1, e.startColumn - 1, e.endLine - 1, e.endColumn - 1);

/**
 * The SQL editor enhancements: Format Document / Format Selection (and Ctrl+F2), the Enhanced Completions switch in
 * the editor tab's menu, quick snippets, the column picker, * expansion, Select Top Rows (Ctrl+3) and Go to Object
 * Definition (Ctrl+F12). Formatting and snippets need no connection; the rest need the editor bound to an open one.
 */
export function registerSqlEditorFeatures(context: vscode.ExtensionContext, deps: SqlEditorDeps): void {
  const { runner, docs, store, log } = deps;
  const selector: vscode.DocumentSelector = { language: 'sql' };

  const reg = (id: string, fn: (...args: unknown[]) => Promise<void> | void) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (...args: unknown[]) => {
      try { await fn(...args); }
      catch (err) {
        if (err instanceof McpToolError && err.cancelled) return;
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  /** The open profile a document is bound to, or undefined (with a message when `explain`). */
  const profileOf = (document: vscode.TextDocument, explain: boolean): ConnectionProfile | undefined => {
    const assoc = isNeverBound(document.uri.scheme) ? undefined : docs.get(document.uri);
    const profile = assoc ? findProfile(store.list(), assoc.connection) : undefined;
    if (profile?.open) return profile;
    if (explain) {
      void vscode.window.showInformationMessage(assoc
        ? `APoint-ms-sql: the connection '${assoc.connection}' is ${profile ? 'closed: open it first' : 'removed'}.`
        : 'APoint-ms-sql: this needs a SQL editor bound to a connection (New Query or Change Connection).');
    }
    return undefined;
  };

  const languageService = (profile: ConnectionProfile, args: Record<string, unknown>, timeoutMs: number) =>
    runner.callResult(profile.name, LANGUAGE_SERVICE_TOOL, args, { timeoutMs });

  // ----- Enhanced Completions switch (editor tab menu) -----

  const updateEnhancedKey = () => void vscode.commands.executeCommand('setContext', ENHANCED_CONTEXT_KEY, enhanced());
  updateEnhancedKey();
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
    if (e.affectsConfiguration(`msSqlMcp.${ENHANCED_SETTING}`)) updateEnhancedKey();
  }));
  const setEnhanced = async (on: boolean) => {
    await config().update(ENHANCED_SETTING, on, vscode.ConfigurationTarget.Global);
    vscode.window.setStatusBarMessage(`APoint-ms-sql: enhanced completions ${on ? 'on' : 'off'}`, 3000);
  };
  reg('enableEnhancedCompletions', () => setEnhanced(true));
  reg('disableEnhancedCompletions', () => setEnhanced(false));

  // ----- snippets -----

  context.subscriptions.push(vscode.languages.registerCompletionItemProvider(selector, {
    provideCompletionItems: (document, position) => {
      if (!enhanced() || document.uri.scheme === 'mssql-ddl') return undefined;
      const word = document.getWordRangeAtPosition(position, /[A-Za-z_]+/);
      return SQL_SNIPPETS.map(s => {
        const item = new vscode.CompletionItem({ label: s.prefix, description: snippetHint(s) }, vscode.CompletionItemKind.Snippet);
        item.insertText = new vscode.SnippetString(s.body);
        item.detail = `${s.description} (type ${s.prefix}, then Tab)`;
        item.documentation = new vscode.MarkdownString().appendCodeblock(snippetPreview(s.body), 'sql');
        // Ahead of the server's items when the prefix is typed exactly.
        item.sortText = `!${s.prefix}`;
        if (word) item.range = word;
        return item;
      });
    },
  }));

  // ----- formatting -----

  const formatEdits = async (document: vscode.TextDocument, options: vscode.FormattingOptions, range?: vscode.Range): Promise<vscode.TextEdit[]> => {
    const settings = formatSettings(
      { keywordCase: config().get('format.keywordCase'), maxItemsPerRow: config().get('format.maxItemsPerRow') },
      { tabSize: options.tabSize, insertSpaces: options.insertSpaces },
    );
    const payload = await runner.callUnbound(FORMAT_TOOL, formatRequest(document.getText(), settings, range), { timeoutMs: FORMAT_TIMEOUT_MS });
    const edits = parseFormatEdits(payload);
    if (!edits) throw new Error('the formatter returned no edits.');
    return edits.map(e => vscode.TextEdit.replace(toRange(e), e.newText));
  };

  /**
   * Provider requests (Format Document / Selection, format-on-save): a failure (often a script still being written)
   * only shows in the status bar, never as a dialog on every save. Ctrl+F2 shows the reason.
   */
  const quietly = async (document: vscode.TextDocument, options: vscode.FormattingOptions, range?: vscode.Range) => {
    try {
      return await formatEdits(document, options, range);
    } catch (err) {
      if (!(err instanceof McpToolError && err.cancelled)) {
        vscode.window.setStatusBarMessage('APoint-ms-sql: not formatted (the SQL does not parse); Ctrl+F2 shows why', 5000);
      }
      return [];
    }
  };
  // Query windows always; .sql files and untitled editors only when msSqlMcp.format.formatFiles is on, so
  // format-on-save never rewrites files in other repositories unasked.
  let providers: vscode.Disposable | undefined;
  const registerProviders = () => {
    providers?.dispose();
    const scoped = formatterSchemes(config().get(FORMAT_FILES_SETTING)).map(scheme => ({ language: 'sql', scheme }));
    providers = vscode.Disposable.from(
      vscode.languages.registerDocumentFormattingEditProvider(scoped, { provideDocumentFormattingEdits: (d, o) => quietly(d, o) }),
      vscode.languages.registerDocumentRangeFormattingEditProvider(scoped, { provideDocumentRangeFormattingEdits: (d, r, o) => quietly(d, o, r) }),
    );
  };
  registerProviders();
  context.subscriptions.push(
    { dispose: () => providers?.dispose() },
    vscode.workspace.onDidChangeConfiguration(e => {
      if (e.affectsConfiguration(`msSqlMcp.${FORMAT_FILES_SETTING}`)) registerProviders();
    }),
  );

  // Ctrl+F2: the selection, or the whole document when nothing is selected.
  reg('formatSql', async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== 'sql') {
      void vscode.window.showInformationMessage('APoint-ms-sql: open a SQL editor first.');
      return;
    }
    if (editor.document.uri.scheme === 'mssql-ddl') {
      void vscode.window.showInformationMessage('APoint-ms-sql: this DDL view is read-only.');
      return;
    }
    const { document, selection } = editor;
    const version = document.version;
    let edits: vscode.TextEdit[];
    try {
      edits = await formatEdits(document, { tabSize: Number(editor.options.tabSize) || 4, insertSpaces: editor.options.insertSpaces !== false },
        selection.isEmpty ? undefined : selection);
    } catch (err) {
      if (err instanceof McpToolError && err.cancelled) return;
      void vscode.window.showWarningMessage(`APoint-ms-sql: ${err instanceof Error ? err.message : String(err)}`);
      return;
    }
    if (!edits.length) {
      vscode.window.setStatusBarMessage('APoint-ms-sql: already formatted', 2000);
      return;
    }
    if (document.version !== version) {
      void vscode.window.showInformationMessage('APoint-ms-sql: the text changed while formatting; press Ctrl+F2 again.');
      return;
    }
    await editor.edit(builder => { for (const e of edits) builder.replace(e.range, e.newText); });
  });

  // ----- column picker -----

  reg('pickColumns', async (uriArg?: unknown, positionArg?: unknown) => {
    const editor = vscode.window.activeTextEditor;
    if (!editor || (uriArg instanceof vscode.Uri && editor.document.uri.toString() !== uriArg.toString())) return;
    const profile = profileOf(editor.document, true);
    if (!profile) return;
    const at = positionArg instanceof vscode.Position ? positionArg : editor.selection.active;
    const scope = parseScope(await languageService(profile,
      { action: 'scope', text: editor.document.getText(), line: at.line + 1, column: at.character + 1 }, SCOPE_TIMEOUT_MS));
    if (!scope || !scope.tables.length) {
      void vscode.window.showInformationMessage('APoint-ms-sql: no tables found in this statement (add FROM / JOIN first).');
      return;
    }
    const items = pickerItems(scope.tables);
    if (!items.length) {
      void vscode.window.showInformationMessage(scope.loading
        ? 'APoint-ms-sql: the table list is still loading; try again in a moment.'
        : 'APoint-ms-sql: the columns of these tables are unknown (temporary tables, table variables or derived tables).');
      return;
    }
    const picked = await vscode.window.showQuickPick(items, {
      canPickMany: true, matchOnDescription: true, title: 'Pick columns', placeHolder: 'Type to filter; Space checks a column; Enter inserts the checked ones',
    });
    if (!picked?.length) return;
    // Inserted where the caret is now (the picker may have been opened from a completion at an earlier spot).
    const text = picked.map(p => p.insert).join(', ');
    await editor.edit(b => b.insert(editor.selection.active, text));
  });

  // ----- * expansion -----

  context.subscriptions.push(vscode.languages.registerCodeActionsProvider(selector, {
    provideCodeActions: (document, range) => {
      if (!enhanced() || !profileOf(document, false)) return undefined;
      const line = document.lineAt(range.start.line).text;
      const star = wildcardAt(line, range.start.character);
      if (!star) return undefined;
      const action = new vscode.CodeAction(`Expand ${star.qualifier ? star.qualifier + '.' : ''}* to its columns`, vscode.CodeActionKind.RefactorRewrite);
      action.command = { title: action.title, command: 'msSqlMcp.expandWildcard', arguments: [document.uri, range.start.line, star.start] };
      return [action];
    },
  }, { providedCodeActionKinds: [vscode.CodeActionKind.RefactorRewrite] }));

  reg('expandWildcard', async (uriArg?: unknown, lineArg?: unknown, charArg?: unknown) => {
    const editor = vscode.window.activeTextEditor;
    if (!editor) return;
    if (uriArg instanceof vscode.Uri && editor.document.uri.toString() !== uriArg.toString()) return;
    const profile = profileOf(editor.document, true);
    if (!profile) return;
    const lineNo = typeof lineArg === 'number' ? lineArg : editor.selection.active.line;
    const character = typeof charArg === 'number' ? charArg : editor.selection.active.character;
    const version = editor.document.version;
    const star = wildcardAt(editor.document.lineAt(lineNo).text, character);
    if (!star) {
      void vscode.window.showInformationMessage('APoint-ms-sql: put the cursor on a * of a SELECT list.');
      return;
    }
    const scope = parseScope(await languageService(profile,
      { action: 'scope', text: editor.document.getText(), line: lineNo + 1, column: star.start + 1 }, SCOPE_TIMEOUT_MS));
    const columns = scope ? expandWildcard(scope.tables, star.qualifier) : undefined;
    if (!columns) {
      void vscode.window.showInformationMessage(scope?.loading
        ? 'APoint-ms-sql: the table list is still loading; try again in a moment.'
        : 'APoint-ms-sql: the columns behind this * are unknown here.');
      return;
    }
    if (editor.document.version !== version) return;
    await editor.edit(b => b.replace(new vscode.Range(lineNo, star.start, lineNo, star.end), columns));
  });

  // ----- Ctrl+3 and Ctrl+F12 -----

  /** The object under the cursor (or selected), resolved on the editor's connection. */
  const resolveObject = async () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== 'sql') return undefined;
    const profile = profileOf(editor.document, true);
    if (!profile) return undefined;
    const { selection, document } = editor;
    const name = objectNameAt(document.lineAt(selection.active.line).text, selection.active.character,
      selection.isEmpty ? undefined : document.getText(selection));
    if (!name) {
      void vscode.window.showInformationMessage('APoint-ms-sql: put the cursor on (or select) a table, view, procedure or function name.');
      return undefined;
    }
    const info = parseObjectInfo(await languageService(profile, { action: 'objectInfo', name }, LANGUAGE_SERVICE_TIMEOUT_MS * 2));
    if (!info?.found) {
      void vscode.window.showInformationMessage(`APoint-ms-sql: '${name}' was not found in ${profile.database ?? profile.name}.`);
      return undefined;
    }
    return { profile, info, name };
  };

  reg('selectTopRows', async () => {
    const resolved = await resolveObject();
    if (!resolved) return;
    const { profile, info, name } = resolved;
    const ref = objectRefOf(profile.name, info);
    const rows = clampTop(config().get<number>('dataViewRows', DEFAULT_TOP));
    if (ref && (ref.scriptType === 'Table' || ref.scriptType === 'View')) {
      // The explorer's Data View: TOP (rows), read-only, paged.
      await vscode.commands.executeCommand('msSqlMcp.dataView', { kind: 'object', ref });
      return;
    }
    if (info.type === 'IF' || info.type === 'TF') {
      await vscode.commands.executeCommand('msSqlMcp.newQueryWithText', {
        connection: profile.name, text: tvfQuery(info.schema, info.name ?? name, info.parameters, rows),
      });
      return;
    }
    void vscode.window.showInformationMessage(`APoint-ms-sql: '${name}' is not a table, view or table-valued function.`);
  });

  reg('goToObjectDefinition', async () => {
    const resolved = await resolveObject();
    if (!resolved) return;
    const ref = objectRefOf(resolved.profile.name, resolved.info);
    if (!ref) {
      void vscode.window.showInformationMessage(`APoint-ms-sql: scripting '${resolved.name}' (type ${resolved.info.type ?? '?'}) is not supported here.`);
      return;
    }
    await vscode.commands.executeCommand('msSqlMcp.showDdlNewTab', { kind: 'object', ref });
  });
}
