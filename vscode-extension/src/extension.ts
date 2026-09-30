import * as vscode from 'vscode';
import { registerConnectionCommands } from './connections/connectionCommands';
import { ConnectionStore } from './connections/store';
import { PROVIDER_ID } from './constants';
import { ExplorerClient } from './explorer/explorerClient';
import { Logger } from './logger';
import { MssqlMcpServerProvider } from './mcpProvider';

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
  const provider = new MssqlMcpServerProvider(context, store);
  context.subscriptions.push(
    provider,
    vscode.lm.registerMcpServerDefinitionProvider(PROVIDER_ID, provider),
    store.onDidChange(() => provider.refresh()),
    vscode.workspace.onDidChangeConfiguration(e => {
      if (e.affectsConfiguration('msSqlMcp.insights') || e.affectsConfiguration('msSqlMcp.allowAdhocConnections') || e.affectsConfiguration('msSqlMcp.serverPath')) provider.refresh();
      if (e.affectsConfiguration('msSqlMcp.serverPath')) explorer.reset();
    }),
  );
  log.info('activate', 'MSSQL-MCP activated');
}

export function deactivate(): void {}
