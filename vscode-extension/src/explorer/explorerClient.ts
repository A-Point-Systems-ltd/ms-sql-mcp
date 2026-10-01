import * as vscode from 'vscode';
import { ServerProcessClient } from '../client/serverProcessClient';
import { ConnectionStore } from '../connections/store';
import { Logger } from '../logger';

/**
 * The object tree's private, read-only MssqlMcp process (separate from the agent-facing one).
 * Every profile is forced read-only; see {@link ServerProcessClient} for the lifecycle.
 */
export class ExplorerClient extends ServerProcessClient {
  constructor(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger) {
    super(context, store, log, { label: 'explorer', forceReadOnly: true });
  }
}

