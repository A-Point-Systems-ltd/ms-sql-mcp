import type { ObjectRef } from './catalog';

export const DDL_SCHEME = 'mssql-ddl';

/** Quotes an identifier as a T-SQL bracketed name, doubling closing brackets. */
export const bracket = (id: string): string => `[${id.replace(/]/g, ']]')}]`;

export const qualified = (schema: string | undefined, name: string): string => (schema ? `${bracket(schema)}.${bracket(name)}` : bracket(name));

export const previewSql = (schema: string | undefined, name: string, rows: number): string =>
  `SELECT TOP (${Math.max(1, Math.floor(rows))}) * FROM ${qualified(schema, name)}`;

const enc = encodeURIComponent;

/**
 * Query values use only URI-unreserved characters ('%' is replaced by '~', a literal '~' is pre-escaped), because
 * VS Code's URI class decodes the query once (URI.query) and re-encodes it in toString(); an ordinary
 * percent-encoded '&', '=', '%' or '#' would not survive that round trip.
 */
const qEnc = (v: string): string => enc(v).replace(/~/g, '%7E').replace(/%/g, '~');
const qDec = (v: string): string => decodeURIComponent(v.replace(/~/g, '%'));

/**
 * mssql-ddl:/<conn>/<type>/<schema.name>.sql?c=..&t=..&n=..[&s=..][&p=..]
 * Path segments are for display only (encodeURIComponent); the query is the source of truth.
 */
export function ddlUri(ref: ObjectRef): string {
  const display = ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
  let q = `c=${qEnc(ref.connection)}&t=${qEnc(ref.scriptType)}&n=${qEnc(ref.name)}`;
  if (ref.schema) q += `&s=${qEnc(ref.schema)}`;
  if (ref.parent) q += `&p=${qEnc(ref.parent)}`;
  return `${DDL_SCHEME}:/${enc(ref.connection)}/${enc(ref.scriptType)}/${enc(display)}.sql?${q}`;
}

/** Accepts the ddlUri() string, URI.toString(), URI.toString(true), or a bare URI.query. */
export function parseDdlUri(uri: string): ObjectRef {
  const qStart = uri.indexOf('?');
  let query = qStart >= 0 ? uri.slice(qStart + 1) : uri;
  const hash = query.indexOf('#');
  if (hash >= 0) query = query.slice(0, hash);
  // URI.toString() escapes the separators themselves ("c%3D...%26t%3D..."): undo that one layer.
  if (!query.includes('=')) query = decodeURIComponent(query);
  const params = new Map<string, string>();
  for (const part of query.split('&')) {
    const eq = part.indexOf('=');
    if (eq > 0) params.set(part.slice(0, eq), qDec(part.slice(eq + 1)));
  }
  const ref: ObjectRef = { connection: params.get('c') ?? '', scriptType: params.get('t') ?? '', name: params.get('n') ?? '' };
  if (params.has('s')) ref.schema = params.get('s')!;
  if (params.has('p')) ref.parent = params.get('p')!;
  return ref;
}
