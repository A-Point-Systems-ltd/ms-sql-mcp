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
