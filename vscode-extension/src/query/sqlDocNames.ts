// Names, URIs and backing paths of `mssql-sql:` documents (query windows and editable object scripts).
// No 'vscode' import: unit-testable with plain Node; sqlDocFs.ts is the vscode glue.
import { randomBytes } from 'node:crypto';
import * as path from 'node:path';
import type { ObjectRef } from '../explorer/catalog';
import type { ConnectionProfile } from '../connections/profile';
import { targetOf } from './targetGuard';

export const SQL_DOC_SCHEME = 'mssql-sql';

export type SqlDocKind = 'query' | 'object';

/** What a `mssql-sql:/<kind>/<id>/<title>.sql` path identifies. The title is display-only; kind + id find the content. */
export interface SqlDocAddress {
  kind: SqlDocKind;
  id: string;
  title: string;
}

/** Folder under the extension's global storage that holds `<kind>/<id>.sql` backing files. */
export const SQL_DOCS_DIR = 'sqldocs';

/** Folder of the pre-`mssql-sql:` editable object files, removed on activation. */
export const LEGACY_EDITS_DIR = 'edits';

const ID_RE = /^[0-9a-f]{8,40}$/;
const isKind = (v: string): v is SqlDocKind => v === 'query' || v === 'object';

/** Lower-case hex, 8 to 40 characters (query ids are 8, object ids 16). Never a path separator or `..`. */
export function isValidDocId(id: string): boolean {
  return ID_RE.test(id);
}

/** A new random query document id: 8 lower-case hex characters. */
export function newQueryId(): string {
  return randomBytes(4).toString('hex');
}

/**
 * One part of a tab title: `/` becomes `∕` (U+2215, so it never adds a path segment), C0/C1 control characters are
 * dropped, surrounding whitespace is trimmed, and an empty part becomes `_`. A backslash is kept: vscode-uri keeps it
 * in `path`, `toString()` / `parse()` and `Utils.basename` (see test/sqlDocNames.test.mjs).
 */
export function titlePart(value: string): string {
  const s = value.replace(/\//g, '∕').replace(/[\u0000-\u001f\u007f-\u009f]/g, '').trim();
  return s || '_';
}

/** The server / database shown in a tab title. */
export interface TitleTarget {
  server: string;
  database: string;
}

const RAW_SERVER = 'connection string';
const DEFAULT_DATABASE = 'default';

/**
 * Server and database for a tab title: the profile's own fields, or for a raw profile its Data Source / Initial Catalog
 * (targetGuard's parser), falling back to `connection string` / `default`. A missing profile (removed) shows the
 * connection name as the server.
 */
export function profileTarget(profile: ConnectionProfile | undefined, connection = ''): TitleTarget {
  if (!profile) return { server: connection.trim() || RAW_SERVER, database: DEFAULT_DATABASE };
  const t = targetOf(profile);
  if ('raw' in t) return { server: RAW_SERVER, database: DEFAULT_DATABASE };
  return { server: t.server.trim() || RAW_SERVER, database: t.database.trim() || DEFAULT_DATABASE };
}

/** `<objectName> - <server> - <database>`, each part through {@link titlePart}. */
export function docTitle(objectName: string, target: TitleTarget): string {
  return [objectName, target.server, target.database].map(titlePart).join(' - ');
}

/** The object name of query window `n`: `Query n`. */
export function queryObjectName(n: number): string {
  return `Query ${n}`;
}

/** `schema.name`, or `name` when the object has no schema. */
export function objectDisplayName(ref: ObjectRef): string {
  return ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
}

/** The `n` of a title (or file name) that starts with `Query n - `; undefined otherwise. */
export function queryNumberOf(title: string): number | undefined {
  const m = /^Query ([1-9]\d*) - /.exec(title);
  return m ? Number(m[1]) : undefined;
}

/** Hands out `Query N` numbers for one extension session: from 1 upwards, skipping numbers whose tabs are open. */
export class QueryCounter {
  private nextNumber = 1;

  /** The next number at or above the counter that is not in `taken`; the counter moves past it. */
  next(taken: ReadonlySet<number>): number {
    let n = this.nextNumber;
    while (taken.has(n)) n++;
    this.nextNumber = n + 1;
    return n;
  }
}

/** `/<kind>/<id>/<title>.sql`; the title goes through {@link titlePart}. Use with `Uri.from({ scheme, path })`. */
export function sqlDocPath(addr: SqlDocAddress): string {
  if (!isKind(addr.kind) || !isValidDocId(addr.id)) throw new Error(`Invalid SQL document address: ${addr.kind}/${addr.id}`);
  return `/${addr.kind}/${addr.id}/${titlePart(addr.title)}.sql`;
}

/** The address in a `mssql-sql:` path, or undefined when it is not exactly `/<kind>/<id>/<title>.sql`. */
export function parseSqlDocPath(p: string): SqlDocAddress | undefined {
  const parts = p.split('/');
  if (parts.length !== 4 || parts[0] !== '') return undefined;
  const [, kind, id, file] = parts;
  if (!isKind(kind) || !isValidDocId(id) || !file.endsWith('.sql')) return undefined;
  const title = file.slice(0, -'.sql'.length);
  return title ? { kind, id, title } : undefined;
}

/** `<root>/sqldocs/<kind>/<id>.sql`. Throws for an unknown kind or a malformed id, so no input escapes the folder. */
export function backingFile(root: string, kind: SqlDocKind, id: string): string {
  if (!isKind(kind) || !isValidDocId(id)) throw new Error(`Invalid SQL document address: ${String(kind)}/${id}`);
  return path.join(root, SQL_DOCS_DIR, kind, `${id}.sql`);
}

/** True when `fsPath` is inside `<storageRoot>/edits` (the pre-`mssql-sql:` object files), compared case-insensitively. */
export function isLegacyEditPath(fsPath: string, storageRoot: string): boolean {
  const dir = path.join(storageRoot, LEGACY_EDITS_DIR).toLowerCase();
  const p = path.normalize(fsPath).toLowerCase();
  return p.startsWith(dir + path.sep);
}
