// Which scripted objects open as editable, connection-bound files, and where those files live.
// No 'vscode' import: unit-testable with plain Node.
import * as path from 'node:path';
import type { ObjectRef } from './catalog';

/** Script types whose DDL opens as an editable file that Run applies to the server. */
export const EDITABLE_TYPES = ['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction'] as const;

export function isEditable(scriptType: string | undefined): boolean {
  return !!scriptType && (EDITABLE_TYPES as readonly string[]).includes(scriptType);
}

const MAX_SEGMENT = 100;
const RESERVED = /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i;

/**
 * One path segment that is valid on Windows: illegal and control characters become `_`, trailing dots and
 * spaces are dropped, a reserved device name (with or without an extension) gets a `_` prefix, and the
 * result is at most 100 characters. Never empty, never `.` or `..`.
 */
export function safeSegment(value: string): string {
  const trim = (s: string) => s.replace(/[. ]+$/, '');
  let s = trim(value.replace(/[<>:"/\\|?*\u0000-\u001f\u007f]/g, '_'));
  if (!s) return '_';
  if (RESERVED.test(s.split('.')[0].trimEnd())) s = `_${s}`;
  s = trim(s.slice(0, MAX_SEGMENT));
  return s || '_';
}

/** `<root>/edits/<connection>/<scriptType>/<schema.name>.sql`. Same object, same file, so reopening reuses it. */
export function editFilePath(root: string, ref: ObjectRef): string {
  const name = ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
  return path.join(root, 'edits', safeSegment(ref.connection), safeSegment(ref.scriptType), `${safeSegment(name)}.sql`);
}
