// Which connection each SQL document is bound to, persisted per workspace.
// No 'vscode' import — unit-testable with plain Node; queryCommands.ts wires the document events.
import type { ObjectRef } from '../explorer/catalog';
import { DDL_SCHEME } from '../explorer/sqlText';

export const QUERY_DOCUMENTS_KEY = 'msSqlMcp.queryDocuments';

/** A document bound to a connection: a free query, or the editable DDL of a database object. */
export interface QueryAssociation {
  connection: string;
  kind: 'query' | 'object';
  object?: ObjectRef;
}

/** The part of vscode.Memento this store needs (context.workspaceState). */
export interface AssociationMemento {
  get<T>(key: string, defaultValue: T): T;
  update(key: string, value: unknown): Thenable<void>;
}

/** Any vscode.Uri (or its string form): entries are keyed by `uri.toString()`. */
export type DocumentKey = string | { toString(): string };

export interface Disposable { dispose(): void }

const keyOf = (uri: DocumentKey): string => (typeof uri === 'string' ? uri : uri.toString());

/** Read-only DDL documents (`mssql-ddl:`) are never bound to a connection: no status item, no Run. */
export function isNeverBound(scheme: string): boolean {
  return scheme === DDL_SCHEME;
}

/** Whether closing a document drops its association: only `file:` documents keep it (they can be reopened). */
export function dropOnClose(scheme: string): boolean {
  return scheme !== 'file';
}

/**
 * Whether an association survives activation: a file must still exist, any other document must still be open
 * (an editor tab), and `mssql-ddl:` entries never stay.
 */
export function keepOnActivation(scheme: string, isOpen: boolean, fileExists: () => boolean): boolean {
  if (isNeverBound(scheme)) return false;
  return scheme === 'file' ? fileExists() : isOpen;
}

function isAssociation(v: unknown): v is QueryAssociation {
  const a = v as Partial<QueryAssociation> | undefined;
  return !!a && typeof a.connection === 'string' && a.connection.length > 0 && (a.kind === 'query' || a.kind === 'object')
    && (a.object === undefined || (typeof a.object === 'object' && a.object !== null && typeof a.object.name === 'string'));
}

/** Association `uri.toString() → { connection, kind, object? }`, persisted under {@link QUERY_DOCUMENTS_KEY}. */
export class QueryDocuments implements Disposable {
  private readonly entries = new Map<string, QueryAssociation>();
  private readonly listeners = new Set<(key: string) => void>();

  constructor(private readonly memento: AssociationMemento) {
    const saved = memento.get<Record<string, unknown>>(QUERY_DOCUMENTS_KEY, {});
    for (const [key, value] of Object.entries(saved && typeof saved === 'object' ? saved : {})) {
      if (isAssociation(value)) this.entries.set(key, value);
    }
  }

  /** Fires with the document key whenever its association is set, changed or removed. */
  readonly onDidChange = (listener: (key: string) => void): Disposable => {
    this.listeners.add(listener);
    return { dispose: () => { this.listeners.delete(listener); } };
  };

  get(uri: DocumentKey): QueryAssociation | undefined {
    return this.entries.get(keyOf(uri));
  }

  /** Every association (a snapshot). */
  all(): [string, QueryAssociation][] {
    return [...this.entries];
  }

  async set(uri: DocumentKey, assoc: QueryAssociation): Promise<void> {
    const key = keyOf(uri);
    this.entries.set(key, { ...assoc });
    await this.save();
    this.fire(key);
  }

  async delete(uri: DocumentKey): Promise<void> {
    const key = keyOf(uri);
    if (!this.entries.delete(key)) return;
    await this.save();
    this.fire(key);
  }

  /** Moves an association to a renamed document. */
  async rename(from: DocumentKey, to: DocumentKey): Promise<void> {
    const oldKey = keyOf(from);
    const assoc = this.entries.get(oldKey);
    if (!assoc) return;
    const newKey = keyOf(to);
    this.entries.delete(oldKey);
    this.entries.set(newKey, assoc);
    await this.save();
    this.fire(oldKey);
    this.fire(newKey);
  }

  /** Drops every entry for which `keep(key, assoc)` is false. */
  async prune(keep: (key: string, assoc: QueryAssociation) => boolean): Promise<void> {
    const dropped = [...this.entries].filter(([key, assoc]) => !keep(key, assoc)).map(([key]) => key);
    if (!dropped.length) return;
    for (const key of dropped) this.entries.delete(key);
    await this.save();
    for (const key of dropped) this.fire(key);
  }

  dispose(): void {
    this.listeners.clear();
  }

  private save(): Thenable<void> {
    return this.memento.update(QUERY_DOCUMENTS_KEY, Object.fromEntries(this.entries));
  }

  private fire(key: string): void {
    for (const listener of [...this.listeners]) listener(key);
  }
}
