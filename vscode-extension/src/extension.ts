import * as vscode from 'vscode';
import { RUNNER_OPTIONS, ServerProcessClient } from './client/serverProcessClient';
import { registerExplorerCommands } from './commands/explorerCommands';
import { disposeDataPanel } from './dataPanel';
import { registerConnectionCommands } from './connections/connectionCommands';
import { ConnectionStore } from './connections/store';
import { PROVIDER_ID } from './constants';
import { DdlDocumentProvider } from './explorer/ddlDocuments';
import { ExplorerClient } from './explorer/explorerClient';
import { ExplorerTreeProvider } from './explorer/explorerTree';
import { DDL_SCHEME } from './explorer/sqlText';
import { registerHistoryCommands } from './history/historyCommands';
import { setUpDdlHistory } from './history/historySetup';
import { Logger } from './logger';
import { CursorMcpApi, CursorMcpRegistrar, cursorMcpApi, duplicateEntryAction, hasMsSqlEntry } from './cursorMcp';
import { resolveExePath } from './exe';
import { MssqlMcpServerProvider, agentSettings } from './mcpProvider';
import { registerQueryCommands } from './query/queryCommands';
import { SqlDocFileSystem } from './query/sqlDocFs';
import { SQL_DOC_SCHEME } from './query/sqlDocNames';
import { cursorConfigPath } from './register/clientPaths';
import { registerClientCommand } from './register/registerClients';
import { FILTER_VIEW_ID, ObjectFilterViewProvider } from './tree/filterView';

export function activate(context: vscode.ExtensionContext): void {
  const channel = vscode.window.createOutputChannel('APoint-ms-sql');
  context.subscriptions.push(channel);
  const log = new Logger(channel, context.extensionMode);
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
    if (e.affectsConfiguration('msSqlMcp.logLevel')) log.refresh();
  }));

  const store = new ConnectionStore(context.globalState, context.secrets);
  const explorer = new ExplorerClient(context, store, log);
  context.subscriptions.push(explorer);
  // Query windows run scripts in a third private process that keeps each profile's own read-only flag. It also serves
  // the extension-only ddl_history tool (the connection form's DDL history set-up, Show DDL History).
  const runner = new ServerProcessClient(context, store, log, RUNNER_OPTIONS);
  context.subscriptions.push(runner);
  registerConnectionCommands(context, store, log, explorer, profile => setUpDdlHistory(runner, profile, log));

  // Query windows and editable object scripts are mssql-sql: documents (titled tabs, no programmatic text edits),
  // registered before the commands that open them.
  const sqlDocs = new SqlDocFileSystem(context.globalStorageUri.fsPath, log);
  context.subscriptions.push(sqlDocs, vscode.workspace.registerFileSystemProvider(SQL_DOC_SCHEME, sqlDocs, { isCaseSensitive: true }));

  const tree = new ExplorerTreeProvider(store, explorer, log);
  const { docs: queryDocs } = registerQueryCommands(context, store, log, { runner, sqlDocs, refreshTree: () => tree.refresh() });
  const ddlProvider = new DdlDocumentProvider(explorer, log);
  const filterView = new ObjectFilterViewProvider(() => tree.filter, term => tree.setFilter(term));
  const updateHasConnections = () => void vscode.commands.executeCommand('setContext', 'msSqlMcp.hasConnections', store.list().length > 0);
  updateHasConnections();
  context.subscriptions.push(
    tree,
    vscode.window.createTreeView('msSqlMcp.explorer', { treeDataProvider: tree, showCollapseAll: true }),
    vscode.window.registerWebviewViewProvider(FILTER_VIEW_ID, filterView),
    ddlProvider,
    vscode.workspace.registerTextDocumentContentProvider(DDL_SCHEME, ddlProvider),
    store.onDidChange(updateHasConnections),
    { dispose: disposeDataPanel },
  );
  registerExplorerCommands(context, tree, explorer, filterView, ddlProvider, queryDocs, store, log, sqlDocs, runner);
  registerHistoryCommands(context, { store, docs: queryDocs, runner, explorer, log });

  // Every command is registered before the MCP provider, so a host without (or with a failing) MCP API keeps them all.
  registerClientCommand(context, store, log);
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
    // A reset never kills a running call: the old process is retired when its last call ends.
    if (e.affectsConfiguration('msSqlMcp.serverPath')) {
      explorer.reset();
      runner.reset();
    }
  }));

  // Cursor ignores vscode.lm MCP providers and has its own API, so it gets the registrar instead (never both).
  const cursorApi = cursorMcpApi(vscode);
  if (cursorApi) registerCursorServer(context, store, log, cursorApi);
  else registerMcpProvider(context, store, log);
  log.info('activate', 'APoint-ms-sql activated');
}

/** Registers the agent-facing MCP server definition provider when the host supports it (VS Code 1.101+, Cursor). */
function registerMcpProvider(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger): void {
  if (typeof vscode.lm?.registerMcpServerDefinitionProvider !== 'function') {
    log.warn('activate', 'This editor has no MCP server definition API (vscode.lm.registerMcpServerDefinitionProvider); ' +
      'the agent server is not offered here. Use "Register with Cursor / Claude" instead.');
    return;
  }
  const provider = new MssqlMcpServerProvider(context, store);
  context.subscriptions.push(
    provider,
    vscode.lm.registerMcpServerDefinitionProvider(PROVIDER_ID, provider),
    store.onDidChange(() => provider.refresh()),
    vscode.workspace.onDidChangeConfiguration(e => {
      if (e.affectsConfiguration('msSqlMcp.insights') || e.affectsConfiguration('msSqlMcp.allowAdhocConnections') || e.affectsConfiguration('msSqlMcp.serverPath')) provider.refresh();
    }),
  );
}

const CURSOR_DUPLICATE_FLAG = 'msSqlMcp.cursorMcpJsonDuplicateWarned';
const DONT_SHOW_AGAIN = "Don't show again";
const CURSOR_DEBOUNCE_MS = 300;

/** Registers the agent-facing MCP server with Cursor's own `cursor.mcp` API and keeps it in step with the profiles and settings. */
function registerCursorServer(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger, api: CursorMcpApi): void {
  log.info('activate', `Cursor MCP API found (${vscode.env.appName}); registering the agent server with Cursor instead of the vscode.lm provider.`);
  const registrar = new CursorMcpRegistrar(api, {
    exePath: () => (process.platform === 'win32' ? resolveExePath(context.extensionUri) : undefined),
    profiles: () => store.list(),
    passwords: () => store.passwords(),
    settings: agentSettings,
    log,
    warn: message => void vscode.window.showWarningMessage(message),
  });
  const sync = () => void registrar.sync();
  let timer: NodeJS.Timeout | undefined;
  context.subscriptions.push(
    registrar,
    store.onDidChange(() => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(sync, CURSOR_DEBOUNCE_MS);
    }),
    vscode.workspace.onDidChangeConfiguration(e => {
      if (e.affectsConfiguration('msSqlMcp.insights') || e.affectsConfiguration('msSqlMcp.allowAdhocConnections') || e.affectsConfiguration('msSqlMcp.serverPath')) sync();
    }),
    { dispose: () => { if (timer) clearTimeout(timer); } },
  );
  sync();
  warnAboutDuplicateEntry(context, log);
}

/**
 * Warns on every activation while ~/.cursor/mcp.json holds an `APoint-ms-sql` or legacy `ms-sql` entry (the extension never edits that file on
 * its own). Only "Don't show again" stops it; the choice is cleared once the entry is gone.
 */
function warnAboutDuplicateEntry(context: vscode.ExtensionContext, log: Logger): void {
  const action = duplicateEntryAction(hasMsSqlEntry(cursorConfigPath()), context.globalState.get<boolean>(CURSOR_DUPLICATE_FLAG, false));
  if (action === 'clear') void context.globalState.update(CURSOR_DUPLICATE_FLAG, undefined);
  if (action !== 'warn') return;
  log.warn('activate', "~/.cursor/mcp.json has an 'APoint-ms-sql' or 'ms-sql' entry that duplicates the server registered by the extension.");
  void vscode.window.showWarningMessage(
    "An 'APoint-ms-sql' or 'ms-sql' entry in ~/.cursor/mcp.json duplicates the server this extension now registers automatically. Remove that entry to avoid two APoint-ms-sql servers.",
    DONT_SHOW_AGAIN,
  ).then(choice => {
    if (choice === DONT_SHOW_AGAIN) void context.globalState.update(CURSOR_DUPLICATE_FLAG, true);
  });
}

export function deactivate(): void {}
