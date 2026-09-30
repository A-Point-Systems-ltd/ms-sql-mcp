import * as vscode from 'vscode';
import { Logger } from '../logger';
import type { ExplorerClient } from './explorerClient';
import { parseDdlUri } from './sqlText';
import { ddlText, scriptArgs } from './treeModel';

/** Read-only `mssql-ddl:` documents: the content is script_object's DDL for the object in the URI query. */
export class DdlDocumentProvider implements vscode.TextDocumentContentProvider {
  constructor(private readonly explorer: ExplorerClient, private readonly log: Logger) {}

  async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
    const ref = parseDdlUri(uri.toString());
    try {
      const result = await this.explorer.call(ref.connection, 'script_object', scriptArgs(ref));
      return ddlText(result, ref.connection);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      this.log.warn('ddl', `script_object ${ref.scriptType} '${ref.name}' on '${ref.connection}' failed: ${message}`);
      // An error document instead of a failed open; close and reopen to retry.
      return [`-- MSSQL-MCP could not script ${ref.scriptType} ${ref.schema ? `${ref.schema}.` : ''}${ref.name} from ${ref.connection}:`,
        ...message.split(/\r?\n/).map(l => `-- ${l}`)].join('\n');
    }
  }
}
