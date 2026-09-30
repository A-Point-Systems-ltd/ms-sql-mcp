import * as vscode from 'vscode';
import { Logger } from './logger';

export function activate(context: vscode.ExtensionContext): void {
  const channel = vscode.window.createOutputChannel('MSSQL-MCP');
  context.subscriptions.push(channel);
  new Logger(channel, context.extensionMode).info('activate', 'MSSQL-MCP activated');
}

export function deactivate(): void {}
