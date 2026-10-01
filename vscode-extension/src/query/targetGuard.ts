// Wrong-target guard for object documents: the server/database a script was generated from, compared with where
// it is about to run. No 'vscode' import — unit-testable with plain Node.
import type { ConnectionProfile } from '../connections/profile';

/** Where an object script was generated: the profile's server and database, or `raw` when they are unknown. */
export type ScriptTarget = { server: string; database: string } | { raw: true };

const SERVER_KEYS = new Set(['data source', 'server', 'address', 'addr', 'network address']);
const DATABASE_KEYS = new Set(['initial catalog', 'database']);

/**
 * Best-effort `Data Source` / `Initial Catalog` of a raw connection string (also `Server`, `Address`, `Database`;
 * keys case-insensitive, values may be quoted). `{ raw: true }` when no server is found.
 */
export function parseRawTarget(connectionString: string): ScriptTarget {
  let server: string | undefined;
  let database = '';
  // key=value pairs separated by ';'; a value in "..." or '...' may contain ';' (doubled quotes escape a quote).
  const re = /\s*([^=;]+?)\s*=\s*("(?:[^"]|"")*"|'(?:[^']|'')*'|[^;]*)\s*(?:;|$)/g;
  for (let m = re.exec(connectionString); m !== null && m[0].length > 0; m = re.exec(connectionString)) {
    const key = m[1].trim().toLowerCase().replace(/\s+/g, ' ');
    let value = m[2].trim();
    if (value.length >= 2 && (value[0] === '"' || value[0] === "'") && value.endsWith(value[0])) {
      const q = value[0];
      value = value.slice(1, -1).split(q + q).join(q);
    }
    if (SERVER_KEYS.has(key)) server = value;
    else if (DATABASE_KEYS.has(key)) database = value;
  }
  return server && server.trim() ? { server: server.trim(), database: database.trim() } : { raw: true };
}

/** The target of `profile` as it is now: its server/database, or for a raw profile the parsed connection string. */
export function targetOf(profile: ConnectionProfile): ScriptTarget {
  if (profile.auth === 'raw') return parseRawTarget(profile.rawConnectionString ?? '');
  return { server: profile.server.trim(), database: profile.database.trim() };
}

/** A stored target as read back from workspace state, or undefined when it is missing or malformed. */
export function asScriptTarget(value: unknown): ScriptTarget | undefined {
  const v = value as { server?: unknown; database?: unknown; raw?: unknown } | undefined;
  if (!v || typeof v !== 'object') return undefined;
  if (v.raw === true) return { raw: true };
  return typeof v.server === 'string' && typeof v.database === 'string' ? { server: v.server, database: v.database } : undefined;
}

const norm = (s: string): string => s.trim().toLowerCase();

/**
 * True when `profile` would run the script somewhere other than `stored` (server or database differ, case-insensitive
 * and trimmed). A missing stored target, or one side unknown (`raw`) and the other not, counts as a mismatch; two
 * unknown (`raw`) targets cannot be compared and do not.
 */
export function targetMismatch(stored: ScriptTarget | undefined, profile: ConnectionProfile): boolean {
  if (!stored) return true;
  const current = targetOf(profile);
  if ('raw' in stored || 'raw' in current) return !('raw' in stored && 'raw' in current);
  return norm(stored.server) !== norm(current.server) || norm(stored.database) !== norm(current.database);
}

/** `server/db` for messages; `an unknown server` for a raw or missing target. */
export function describeTarget(target: ScriptTarget | undefined): string {
  if (!target || 'raw' in target) return 'an unknown server';
  return `${target.server}/${target.database}`;
}

/** The fields of an object document's association the guard reads. */
export interface GuardedAssociation {
  kind: 'query' | 'object';
  target?: ScriptTarget;
  /** Set by Change Connection: the document now runs on another connection than it was scripted from. */
  rebound?: boolean;
}

/**
 * The modal confirmation text when an object document is about to run on another target than it was generated from
 * (or was rebound with Change Connection); undefined when it may run without asking. Free queries never ask.
 */
export function wrongTargetPrompt(assoc: GuardedAssociation, profile: ConnectionProfile): string | undefined {
  if (assoc.kind !== 'object') return undefined;
  if (!assoc.rebound && !targetMismatch(assoc.target, profile)) return undefined;
  return `This script was generated from ${describeTarget(assoc.target)} but will run on ${describeTarget(targetOf(profile))} `
    + `(connection '${profile.name}'). Run anyway?`;
}
