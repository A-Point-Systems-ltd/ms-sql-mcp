// Names, URIs and backing paths of `mssql-sql:` documents (query windows and editable object scripts).
// No 'vscode' import: unit-testable with plain Node; sqlDocFs.ts is the vscode glue.
import { randomBytes } from 'node:crypto';
import * as path from 'node:path';
import type { ObjectRef } from '../explorer/catalog';
import type { ConnectionProfile } from '../connections/profile';
import { targetOf } from './targetGuard';

export const SQL_DOC_SCHEME = 'mssql-sql';

export type SqlDocKind = 'query' | 'object';

/** What a `mssql-sql:/<kind>/<id>/<title>` path identifies. The title is display-only; kind + id find the content. */
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
 * One part of a tab title: `/` becomes `∕` (U+2215) and `\` becomes `∖` (U+2216), so neither adds a path segment
 * (Cursor builds the tab label after the last `/` or `\`: `dc\dev16` showed as `dev16`). C0/C1 control characters are
 * dropped, surrounding whitespace is trimmed, and an empty part becomes `_`. Only the URI title uses these look-alikes;
 * the UI (status bar, tooltips, modals) shows the real `\`.
 */
export function titlePart(value: string): string {
  const s = value.replace(/\//g, '∕').replace(/\\/g, '∖').replace(/[\u0000-\u001f\u007f-\u009f]/g, '').trim();
  return s || '_';
}

/**
 * A tab title as the UI shows it outside the tab (Open Recent Query, tooltips): the `∖` (U+2216) look-alike that
 * {@link titlePart} put in place of a backslash is shown as the real `\` again. `∕` stays (a `/` in a name is rare
 * and the look-alike is harmless there).
 */
export function displayTitle(title: string): string {
  return title.replace(/∖/g, '\\');
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

/**
 * First path segment of every document. package.json contributes a sql `filenamePatterns` glob matching any path with a
 * `~sql` folder, so the editor opens these documents as sql from the start (no extension needed, no language change).
 */
export const SQL_DOC_ROOT = '~sql';

/**
 * `/~sql/<kind>/<id>/<title>`: no extension, so the tab reads exactly the title. The title goes through
 * {@link titlePart}. Use with `Uri.from({ scheme, path })`.
 */
export function sqlDocPath(addr: SqlDocAddress): string {
  if (!isKind(addr.kind) || !isValidDocId(addr.id)) throw new Error(`Invalid SQL document address: ${addr.kind}/${addr.id}`);
  return `/${SQL_DOC_ROOT}/${addr.kind}/${addr.id}/${titlePart(addr.title)}`;
}

/** The address in a `mssql-sql:` path, or undefined when it is not exactly `/~sql/<kind>/<id>/<title>`. */
export function parseSqlDocPath(p: string): SqlDocAddress | undefined {
  const parts = p.split('/');
  if (parts.length !== 5 || parts[0] !== '' || parts[1] !== SQL_DOC_ROOT) return undefined;
  const [, , kind, id, title] = parts;
  if (!isKind(kind) || !isValidDocId(id) || !title || title === '.' || title === '..') return undefined;
  return { kind, id, title };
}

/** `/`, `/~sql`, `/~sql/<kind>` and `/~sql/<kind>/<id>` (optionally with a trailing `/`): the virtual directories. */
export function isSqlDocDirectory(p: string): boolean {
  return /^\/(?:~sql(?:\/(?:query|object)(?:\/[0-9a-f]{8,40})?)?)?\/?$/.test(p);
}

/** A `mssql-sql:` document that still opened as another language (e.g. another extension's association): the guard. */
export function needsSqlLanguage(scheme: string, languageId: string): boolean {
  return scheme === SQL_DOC_SCHEME && languageId !== 'sql';
}

/** FileSystemProvider.writeFile: whether the `create` / `overwrite` flags allow the write, given whether the file exists. */
export function writeFileCheck(
  exists: boolean, options: { readonly create: boolean; readonly overwrite: boolean },
): 'write' | 'FileNotFound' | 'FileExists' {
  if (!exists) return options.create ? 'write' : 'FileNotFound';
  return options.create && !options.overwrite ? 'FileExists' : 'write';
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

/**
 * `<root>/sqldocs/object/<id>.base.sql`: the last script loaded from the server (or applied by Run) for an object
 * document, next to its backing file. Never a valid backing file name (the id check rejects `<id>.base`).
 */
export function baseFile(root: string, id: string): string {
  if (!isValidDocId(id)) throw new Error(`Invalid SQL document address: object/${id}`);
  return path.join(root, SQL_DOCS_DIR, 'object', `${id}.base.sql`);
}

/** `edits.old-<yyyyMMddHHmmss>` (local time): the legacy `edits/` folder is renamed to this, never deleted. */
export function legacyEditsBackupName(now: Date): string {
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  const stamp = `${p(now.getFullYear(), 4)}${p(now.getMonth() + 1)}${p(now.getDate())}${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
  return `${LEGACY_EDITS_DIR}.old-${stamp}`;
}
