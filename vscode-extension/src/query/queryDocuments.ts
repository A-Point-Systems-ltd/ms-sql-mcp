// Which connection each SQL document is bound to, persisted per workspace.
// No 'vscode' import — unit-testable with plain Node; queryCommands.ts wires the document events.
import type { ObjectRef } from '../explorer/catalog';
import { DDL_SCHEME } from '../explorer/sqlText';
import { SQL_DOC_SCHEME, isValidDocId } from './sqlDocNames';
import { ScriptTarget, asScriptTarget } from './targetGuard';

export const QUERY_DOCUMENTS_KEY = 'msSqlMcp.queryDocuments';

/** A document bound to a connection: a free query, or the editable DDL of a database object. */
export interface QueryAssociation {
  connection: string;
  kind: 'query' | 'object';
  object?: ObjectRef;
  /** Object documents: the server/database the script was generated from (see targetGuard.ts). */
  target?: ScriptTarget;
  /** Object documents: set by Change Connection, so the next run asks before applying the script elsewhere. */
  rebound?: boolean;
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

/**
 * Whether closing a document drops its association: `file:` documents keep it (they can be reopened), and a
 * `mssql-sql:` document closed with unsaved edits keeps it (and its backing file) until the activation prune.
 */
export function dropOnClose(scheme: string, isDirty = false): boolean {
  if (scheme === SQL_DOC_SCHEME) return !isDirty;
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

/**
 * What a document close means once the reopen grace has passed. `reopened`: the uri is open again (a language change
 * closes and reopens the same document), so nothing is dropped, deleted or cancelled. `keep`: the binding stays
 * ({@link dropOnClose} is false). `drop`: the binding goes (and a query document's content is kept or deleted).
 */
export function afterCloseGrace(input: { scheme: string; isDirty: boolean; reopened: boolean }): 'reopened' | 'keep' | 'drop' {
  if (input.reopened) return 'reopened';
  return dropOnClose(input.scheme, input.isDirty) ? 'drop' : 'keep';
}

/**
 * Whether a closed document's run is cancelled and its results dropped only after the reopen grace (and not when it was
 * reopened): `mssql-sql:` uris are never reused by another document. Other schemes clean up at once, because a new
 * untitled document may reuse the uri immediately.
 */
export function defersRunCleanup(scheme: string): boolean {
  return scheme === SQL_DOC_SCHEME;
}

/**
 * The query document ids (from this workspace's own list) whose backing file may be deleted: no tab shows them.
 * Malformed ids are left out, so nothing read back from workspace state becomes a path.
 */
export function orphanQueryIds(owned: readonly string[], open: ReadonlySet<string>): string[] {
  return owned.filter(id => isValidDocId(id) && !open.has(id));
}

/** An open document that may be the source of a Save As: its key, text, binding and whether it is the active editor. */
export interface SaveAsCandidate { key: string; text: string; assoc: QueryAssociation | undefined; active: boolean }

/**
 * The binding a newly opened, unbound document inherits when it is the Save As copy of a bound one (VS Code reports
 * Save As only as a new document with the same text). The active bound document with the same (non-blank) text wins;
 * otherwise the only bound document with that text. Undefined when nothing (or more than one) matches. The copy is
 * always a plain query binding: an object script saved to a file is no longer the object's editable document.
 */
export function inheritedBinding(newKey: string, newText: string, candidates: readonly SaveAsCandidate[]): QueryAssociation | undefined {
  if (!newText.trim()) return undefined;
  const same = candidates.filter(c => c.key !== newKey && c.assoc && c.text === newText);
  const pick = same.find(c => c.active) ?? (same.length === 1 ? same[0] : undefined);
  return pick?.assoc ? { connection: pick.assoc.connection, kind: 'query' } : undefined;
}

function isAssociation(v: unknown): v is QueryAssociation {
  const a = v as Partial<QueryAssociation> | undefined;
  return !!a && typeof a.connection === 'string' && a.connection.length > 0 && (a.kind === 'query' || a.kind === 'object')
    && (a.object === undefined || (typeof a.object === 'object' && a.object !== null && typeof a.object.name === 'string'));
}

/** Drops a malformed `target` (the guard then asks before running) and a non-boolean `rebound`. */
function normalized(a: QueryAssociation): QueryAssociation {
  const { target, rebound, ...rest } = a;
  const t = asScriptTarget(target);
  return { ...rest, ...(t ? { target: t } : {}), ...(rebound === true ? { rebound: true } : {}) };
}

/** Association `uri.toString() → { connection, kind, object? }`, persisted under {@link QUERY_DOCUMENTS_KEY}. */
export class QueryDocuments implements Disposable {
  private readonly entries = new Map<string, QueryAssociation>();
  private readonly listeners = new Set<(key: string) => void>();

  constructor(private readonly memento: AssociationMemento) {
    const saved = memento.get<Record<string, unknown>>(QUERY_DOCUMENTS_KEY, {});
    for (const [key, value] of Object.entries(saved && typeof saved === 'object' ? saved : {})) {
      if (isAssociation(value)) this.entries.set(key, normalized(value));
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
