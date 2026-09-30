import type { ObjectRef } from './catalog';

export const DDL_SCHEME = 'mssql-ddl';

/** Quotes an identifier as a T-SQL bracketed name, doubling closing brackets. */
export const bracket = (id: string): string => `[${id.replace(/]/g, ']]')}]`;

export const qualified = (schema: string | undefined, name: string): string => (schema ? `${bracket(schema)}.${bracket(name)}` : bracket(name));

export const previewSql = (schema: string | undefined, name: string, rows: number): string =>
  `SELECT TOP (${Math.max(1, Math.floor(rows))}) * FROM ${qualified(schema, name)}`;

/** mssql-ddl:/<conn>/<type>/<schema.name>.sql?... — path components URI-encoded; the query is the source of truth. */
export function ddlUri(ref: ObjectRef): string {
  const display = ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
  const q = new URLSearchParams({ c: ref.connection, t: ref.scriptType, n: ref.name });
  if (ref.schema) q.set('s', ref.schema);
  if (ref.parent) q.set('p', ref.parent);
  return `${DDL_SCHEME}:/${encodeURIComponent(ref.connection)}/${encodeURIComponent(ref.scriptType)}/${encodeURIComponent(display)}.sql?${q.toString()}`;
}

export function parseDdlUri(uri: string): ObjectRef {
  const q = new URLSearchParams(uri.slice(uri.indexOf('?') + 1));
  const ref: ObjectRef = { connection: q.get('c') ?? '', scriptType: q.get('t') ?? '', name: q.get('n') ?? '' };
  if (q.has('s')) ref.schema = q.get('s')!;
  if (q.has('p')) ref.parent = q.get('p')!;
  return ref;
}
