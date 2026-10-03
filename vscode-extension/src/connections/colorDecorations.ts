import * as vscode from 'vscode';
import { CONNECTION_COLOR_SCHEME, connectionOfColorUri } from '../explorer/treeModel';
import { DDL_SCHEME, parseDdlUri } from '../explorer/sqlText';
import { findProfile } from '../query/editorState';
import type { QueryDocuments } from '../query/queryDocuments';
import { CONNECTION_COLORS } from './profile';
import type { ConnectionStore } from './store';

/**
 * Colors by connection (the profile's `color`): connection labels in the tree (their `mssql-conn:` resourceUri) and
 * the tab titles of documents tied to a connection (read-only DDL by its uri, query / object documents by their binding).
 */
export class ConnectionColorDecorations implements vscode.FileDecorationProvider, vscode.Disposable {
  private readonly emitter = new vscode.EventEmitter<vscode.Uri | vscode.Uri[] | undefined>();
  readonly onDidChangeFileDecorations = this.emitter.event;
  private readonly subs: vscode.Disposable[];

  constructor(private readonly store: ConnectionStore, private readonly docs: QueryDocuments) {
    this.subs = [
      store.onDidChange(() => this.emitter.fire(undefined)),
      docs.onDidChange(key => this.emitter.fire(vscode.Uri.parse(key))),
    ];
  }

  provideFileDecoration(uri: vscode.Uri): vscode.FileDecoration | undefined {
    const name = this.connectionOf(uri);
    const color = name === undefined ? undefined : findProfile(this.store.list(), name)?.color;
    if (!color || !CONNECTION_COLORS[color]) return undefined;
    return { color: new vscode.ThemeColor(CONNECTION_COLORS[color].themeColor), propagate: false };
  }

  private connectionOf(uri: vscode.Uri): string | undefined {
    if (uri.scheme === CONNECTION_COLOR_SCHEME) return connectionOfColorUri(uri.path);
    if (uri.scheme === DDL_SCHEME) {
      try { return parseDdlUri(uri.toString()).connection || undefined; } catch { return undefined; }
    }
    return this.docs.get(uri)?.connection;
  }

  dispose(): void {
    for (const s of this.subs) s.dispose();
    this.emitter.dispose();
  }
}
