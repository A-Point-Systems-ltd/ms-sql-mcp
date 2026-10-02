// Which scripted objects open as editable, connection-bound documents, and the id of each object's document.
// No 'vscode' import: unit-testable with plain Node.
import { createHash } from 'node:crypto';
import type { ObjectRef } from './catalog';

/** Script types whose DDL opens as an editable document that Run applies to the server. */
export const EDITABLE_TYPES = ['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction'] as const;

export function isEditable(scriptType: string | undefined): boolean {
  return !!scriptType && (EDITABLE_TYPES as readonly string[]).includes(scriptType);
}

/**
 * The `mssql-sql:/object/<id>/...` id of an object's editable document: 16 lower-case hex characters of sha1 over the
 * exact connection, script type, schema (or none) and name, encoded as a JSON array so no split of the parts can
 * collide. Same object, same id (and backing file), so reopening reuses the document; case differences give
 * different ids, and the lower-case hex is safe on case-insensitive file systems.
 */
export function objectDocId(ref: ObjectRef): string {
  const key = JSON.stringify([ref.connection, ref.scriptType, ref.schema ?? null, ref.name]);
  return createHash('sha1').update(key, 'utf8').digest('hex').slice(0, 16);
}

/** What the base copy and backing file of an object document read as (see sqlDocFs `readBacking` / `readBase`). */
export type StoredText = { kind: 'text'; text: string } | { kind: 'missing' } | { kind: 'unknown' };

/** The modal question when an object document holds saved edits that were never applied. */
export const unappliedEditsPrompt = (obj: string): string =>
  `You have saved, unapplied edits to ${obj}. Replace them with the current server version?`;
export const REPLACE_BUTTON = 'Replace';
export const KEEP_EDITS_BUTTON = 'Keep my edits';

const sameText = (a: string, b: string): boolean => a.replace(/\r\n/g, '\n') === b.replace(/\r\n/g, '\n');

/**
 * Reopening an object document (not dirty) from the tree: `load` replaces its backing file with the server script;
 * `ask` first asks whether to replace saved edits that were never applied. The base is the last script loaded from the
 * server (or applied by Run). It asks when the backing file differs from the base (line endings ignored), when the
 * backing file exists but has no base (written by a build without base copies), and when either cannot be read: an
 * unreadable file is never overwritten without asking. A missing backing file just loads.
 */
export function reopenObjectDecision(backing: StoredText, base: StoredText): 'load' | 'ask' {
  if (backing.kind === 'missing') return 'load';
  if (backing.kind === 'unknown' || base.kind !== 'text') return 'ask';
  return sameText(backing.text, base.text) ? 'load' : 'ask';
}
