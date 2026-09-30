import * as vscode from 'vscode';
import { registerConnectionCommands } from './connections/connectionCommands';
import { ConnectionStore } from './connections/store';
import { Logger } from './logger';

export function activate(context: vscode.ExtensionContext): void {
  const channel = vscode.window.createOutputChannel('MSSQL-MCP');
  context.subscriptions.push(channel);
  const log = new Logger(channel, context.extensionMode);
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
    if (e.affectsConfiguration('msSqlMcp.logLevel')) log.refresh();
  }));

  const store = new ConnectionStore(context.globalState, context.secrets);
  registerConnectionCommands(context, store, log);
  log.info('activate', 'MSSQL-MCP activated');
}

export function deactivate(): void {}
