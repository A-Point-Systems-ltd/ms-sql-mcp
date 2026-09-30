import * as vscode from 'vscode';
import { registerExplorerCommands } from './commands/explorerCommands';
import { disposeDataPanel } from './dataPanel';
import { registerConnectionCommands } from './connections/connectionCommands';
import { ConnectionStore } from './connections/store';
import { PROVIDER_ID } from './constants';
import { DdlDocumentProvider } from './explorer/ddlDocuments';
import { ExplorerClient } from './explorer/explorerClient';
import { ExplorerTreeProvider } from './explorer/explorerTree';
import { DDL_SCHEME } from './explorer/sqlText';
import { Logger } from './logger';
import { MssqlMcpServerProvider } from './mcpProvider';
import { registerClientCommand } from './register/registerClients';
import { FILTER_VIEW_ID, ObjectFilterViewProvider } from './tree/filterView';

export function activate(context: vscode.ExtensionContext): void {
  const channel = vscode.window.createOutputChannel('MSSQL-MCP');
  context.subscriptions.push(channel);
  const log = new Logger(channel, context.extensionMode);
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
    if (e.affectsConfiguration('msSqlMcp.logLevel')) log.refresh();
  }));

  const store = new ConnectionStore(context.globalState, context.secrets);
  const explorer = new ExplorerClient(context, store, log);
  context.subscriptions.push(explorer);
  registerConnectionCommands(context, store, log, explorer);

  const tree = new ExplorerTreeProvider(store, explorer, log);
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
  registerExplorerCommands(context, tree, explorer, filterView, ddlProvider, log);

  // Every command is registered before the MCP provider, so a host without (or with a failing) MCP API keeps them all.
  registerClientCommand(context, store, log);
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
    if (e.affectsConfiguration('msSqlMcp.serverPath')) explorer.reset();
  }));

  registerMcpProvider(context, store, log);
  log.info('activate', 'MSSQL-MCP activated');
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

export function deactivate(): void {}
