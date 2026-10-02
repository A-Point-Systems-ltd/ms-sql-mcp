import * as vscode from 'vscode';
import { Logger } from '../logger';
import type { ExplorerClient } from './explorerClient';
import { DDL_SCHEME, parseDdlUri } from './sqlText';
import { ddlText, scriptArgs } from './treeModel';

/** Read-only `mssql-ddl:` documents: the content is script_object's DDL for the object in the URI query. */
export class DdlDocumentProvider implements vscode.TextDocumentContentProvider, vscode.Disposable {
  private readonly emitter = new vscode.EventEmitter<vscode.Uri>();
  readonly onDidChange = this.emitter.event;

  constructor(private readonly explorer: ExplorerClient, private readonly log: Logger) {}

  /** Re-runs script_object for an open DDL document (Refresh on its editor), e.g. to retry after an error. */
  reload(uri: vscode.Uri): void {
    if (uri.scheme === DDL_SCHEME) this.emitter.fire(uri);
  }

  async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
    let label = uri.toString();
    try {
      const ref = parseDdlUri(uri.toString());
      label = `${ref.scriptType} ${ref.schema ? `${ref.schema}.` : ''}${ref.name} from ${ref.connection}`;
      const result = await this.explorer.call(ref.connection, 'script_object', scriptArgs(ref));
      return ddlText(result, ref.connection);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      this.log.warn('ddl', `script_object for ${label} failed: ${message}`);
      // An error document instead of a failed open; Refresh on the editor retries.
      return [`-- APoint-ms-sql could not script ${label}:`, ...message.split(/\r?\n/).map(l => `-- ${l}`),
        '-- Use Refresh (editor title) to retry.'].join('\n');
    }
  }

  dispose(): void {
    this.emitter.dispose();
  }
}
