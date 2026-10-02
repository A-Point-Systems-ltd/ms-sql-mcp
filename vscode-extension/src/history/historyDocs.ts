import * as vscode from 'vscode';
import type { ServerProcessClient } from '../client/serverProcessClient';
import { pick } from '../client/parse';
import { parseReadData } from '../dataTable';
import { errorMessage } from '../errorFormat';
import type { ExplorerClient } from '../explorer/explorerClient';
import { Logger } from '../logger';
import {
  CURRENT_UNAVAILABLE_TEXT, HISTORY_TOOL, NOT_LOADED_TEXT, NO_EARLIER_TEXT, currentDefinitionSql, parseHistoryCommand, parseHistoryUri,
} from './historyModel';

/**
 * Read-only `mssql-history:` documents, the two sides of a history diff. Content is fetched when the editor asks:
 * an audit entry's command text (runner `ddl_history get`), an empty side, or a module's current definition (the
 * explorer's read-only `read_data`). The text is never logged (trace logging records only its length).
 */
export class HistoryDocumentProvider implements vscode.TextDocumentContentProvider {
  constructor(private readonly runner: ServerProcessClient, private readonly explorer: ExplorerClient, private readonly log: Logger) {}

  async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
    const ref = parseHistoryUri(uri.toString());
    if (!ref) return '-- APoint-ms-sql: this DDL history link is not valid.';
    try {
      switch (ref.kind) {
        case 'empty':
          return ref.more ? NOT_LOADED_TEXT : NO_EARLIER_TEXT;
        case 'entry':
          return parseHistoryCommand(await this.runner.call(ref.connection, HISTORY_TOOL, { action: 'get', id: ref.id })).commandText;
        case 'current': {
          const payload = await this.explorer.callResult(ref.connection, 'read_data', { sql: currentDefinitionSql(ref.object), maxRows: 1 });
          const d = pick(parseReadData(payload).rows[0], 'd');
          return typeof d === 'string' ? d : CURRENT_UNAVAILABLE_TEXT;
        }
      }
    } catch (err) {
      const message = errorMessage(err);
      this.log.warn('history', `Loading DDL history text (${ref.kind}) on '${'connection' in ref ? ref.connection : ''}' failed: ${message}`);
      return ['-- APoint-ms-sql could not load this DDL history text:', ...message.split(/\r?\n/).map(l => `-- ${l}`)].join('\n');
    }
  }
}
