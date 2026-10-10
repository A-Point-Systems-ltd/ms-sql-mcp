// Data View editing model: which columns can be edited, pending changes, value validation, T-SQL literals and the
// save script. No 'vscode' import (unit-testable).
//
// Safety: every identifier goes through bracket() / qualified(), every text value through sqlString(); numbers and bit
// are emitted unquoted only after a strict regex check. The save script runs in one transaction (XACT_ABORT ON):
// an UPDATE / DELETE that does not touch exactly one row (the row was changed or deleted by someone else) rolls
// everything back. RAISERROR + RETURN (not THROW) keeps it valid on SQL Server 2008 R2.
import { bracket, qualified, sqlString } from '../explorer/sqlText';
import { GridColumn, TRUNCATED_SUFFIX, cellText, isNumericType } from './gridModel';

/** Column facts from sys.columns that decide what can be written. */
export interface ColumnMeta { name: string; type: string; identity: boolean; computed: boolean; hasDefault: boolean; nullable: boolean }

/** Types the grid cannot edit as text (binary, spatial, row versions, variants, CLR types). */
// sql_variant is edited like nvarchar: the value is saved as N'...', so its base type becomes nvarchar.
const NOT_EDITABLE_TYPES = new Set(['image', 'binary', 'varbinary', 'timestamp', 'rowversion', 'geography', 'geometry', 'hierarchyid']);
const INTEGER_TYPES = new Set(['bigint', 'int', 'smallint', 'tinyint']);
const FLOAT_TYPES = new Set(['float', 'real']);
/** Date / time types: literals go through CAST(N'...' AS datetime2(7) / datetimeoffset(7) / time(7)). */
const DATETIME2_TYPES = new Set(['date', 'datetime', 'datetime2', 'smalldatetime']);
/** Types whose loaded text compares reliably with the stored value (optimistic concurrency check on edited columns). */
const NO_COMPARE_TYPES = new Set(['text', 'ntext', 'image', 'xml', 'float', 'real', 'datetime', 'smalldatetime', 'datetime2', 'datetimeoffset', 'time', 'date',
  'geography', 'geometry', 'hierarchyid', 'sql_variant', 'timestamp', 'rowversion', 'binary', 'varbinary']);

const key = (type: string): string => type.trim().toLowerCase();

/** The SELECT that reads {@link ColumnMeta} of one table (single read-only SELECT, so run_script allows it anywhere). */
export function columnMetaSql(object: { schema?: string; name: string }): string {
  return 'SELECT c.name, ty.name AS type, c.is_identity, c.is_computed, CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS has_default, c.is_nullable'
    + ' FROM sys.columns c INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id'
    + ` WHERE c.object_id = OBJECT_ID(${sqlString(qualified(object.schema, object.name))}) ORDER BY c.column_id`;
}

/** Rows of {@link columnMetaSql} (name, type, is_identity, is_computed, has_default, is_nullable) -> metadata. */
export function parseColumnMeta(rows: readonly (readonly unknown[])[]): ColumnMeta[] {
  const flag = (v: unknown) => v === true || v === 1 || v === '1';
  return rows.flatMap(r => (typeof r[0] === 'string' && r[0].length
    ? [{ name: r[0], type: String(r[1] ?? ''), identity: flag(r[2]), computed: flag(r[3]), hasDefault: flag(r[4]), nullable: flag(r[5]) }]
    : []));
}

/** Whether values of `type` can be typed into a cell. A CLR UDT (dotted name) or an unknown type cannot. */
export function isEditableType(type: string): boolean {
  const k = key(type);
  return k.length > 0 && !k.includes('.') && !NOT_EDITABLE_TYPES.has(k);
}

/** Per result column: can its values be written (exists in the table, editable type, not identity / computed)? */
export function writableColumns(columns: readonly GridColumn[], meta: readonly ColumnMeta[]): boolean[] {
  return columns.map(c => {
    const m = meta.find(x => x.name === c.name);
    return !!m && !m.identity && !m.computed && isEditableType(m.type);
  });
}

/** What the user may do in a Data View. */
export interface EditCapabilities {
  /** Change existing rows (needs a primary key whose columns are all in the result). */
  update: boolean;
  delete: boolean;
  insert: boolean;
  /** Why editing is off (shown as a hint), when it is. */
  reason?: string;
}

export function editCapabilities(o: { isTable: boolean; readOnly: boolean; pk: readonly string[]; metaLoaded: boolean }): EditCapabilities {
  if (!o.isTable) return { update: false, delete: false, insert: false, reason: 'Views are read-only here.' };
  if (o.readOnly) return { update: false, delete: false, insert: false, reason: 'Read-only connection.' };
  if (!o.metaLoaded) return { update: false, delete: false, insert: false, reason: 'Column information could not be read.' };
  if (!o.pk.length) return { update: false, delete: false, insert: true, reason: 'No primary key: rows can be added, not changed or deleted.' };
  return { update: true, delete: true, insert: true };
}

/**
 * Pending changes of one Data View. `edits`: loaded row -> column -> new value (null = NULL). `deletes`: loaded rows.
 * `inserts`: new rows; a cell is undefined while it keeps the column default.
 */
export interface PendingChanges {
  edits: Map<number, Map<number, string | null>>;
  deletes: Set<number>;
  inserts: (string | null | undefined)[][];
}

export const emptyPending = (): PendingChanges => ({ edits: new Map(), deletes: new Set(), inserts: [] });

/** Rows touched (edited rows not also deleted, deleted rows, new rows). */
export function pendingCount(p: PendingChanges): number {
  let n = p.deletes.size + p.inserts.length;
  for (const row of p.edits.keys()) if (!p.deletes.has(row)) n++;
  return n;
}

/** The text a loaded value shows (and is compared / re-sent as); null for NULL. */
export const valueText = (v: unknown): string | null => (v === null || v === undefined ? null : cellText(v));

/**
 * Records an edit of a loaded row (`row` < loaded) or a new row (`row` - loaded = insert index). An edit back to the
 * loaded value drops the entry. Returns false when the row is out of range.
 */
export function applyEdit(p: PendingChanges, rows: readonly (readonly unknown[])[], row: number, col: number, value: string | null): boolean {
  if (row >= rows.length) {
    const ins = p.inserts[row - rows.length];
    if (!ins) return false;
    ins[col] = value;
    return true;
  }
  const original = valueText(rows[row][col]);
  const cells = p.edits.get(row) ?? new Map<number, string | null>();
  if (original === value) cells.delete(col);
  else cells.set(col, value);
  if (cells.size) p.edits.set(row, cells);
  else p.edits.delete(row);
  return true;
}

/** Reverts rows: edits and delete marks of loaded rows; new rows are removed. */
export function revertRows(p: PendingChanges, loaded: number, rows: readonly number[]): void {
  const newRows = rows.filter(r => r >= loaded).map(r => r - loaded).sort((a, b) => b - a);
  for (const r of rows) if (r < loaded) { p.edits.delete(r); p.deletes.delete(r); }
  for (const i of newRows) if (i < p.inserts.length) p.inserts.splice(i, 1);
}

/** Marks loaded rows for deletion; new rows in the list are removed instead. */
export function deleteRows(p: PendingChanges, loaded: number, rows: readonly number[]): void {
  for (const r of rows) if (r < loaded) p.deletes.add(r);
  revertRows(p, loaded, rows.filter(r => r >= loaded));
}

/** `rows` with the pending edits applied (for copy, export and the viewer). */
export function effectiveRows(rows: readonly (readonly unknown[])[], p: PendingChanges): unknown[][] {
  return rows.map((r, i) => {
    const e = p.edits.get(i);
    if (!e) return r as unknown[];
    const copy = [...r];
    for (const [c, v] of e) copy[c] = v;
    return copy;
  });
}

const INT_RE = /^[-+]?\d+$/;
const DECIMAL_RE = /^[-+]?(\d+\.?\d*|\.\d+)$/;
const FLOAT_RE = /^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/;
const GUID_RE = /^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$/;
const BIT_VALUES: Record<string, string> = { true: '1', false: '0', '1': '1', '0': '0' };

/** Why `value` cannot be stored in a column of `type` (checked before saving), or undefined when it can. */
export function validateValue(type: string, value: string | null, nullable = true): string | undefined {
  if (value === null) return nullable ? undefined : 'This column does not allow NULL.';
  const k = key(type);
  const v = value.trim();
  if (TRUNCATED_SUFFIX.test(value)) return 'This value was cut by the server and cannot be saved.';
  if (INTEGER_TYPES.has(k)) return INT_RE.test(v) ? undefined : 'Enter a whole number.';
  if (k === 'bit') return BIT_VALUES[v.toLowerCase()] ? undefined : 'Enter true / false (or 1 / 0).';
  if (FLOAT_TYPES.has(k)) return FLOAT_RE.test(v) ? undefined : 'Enter a number.';
  if (isNumericType(k)) return DECIMAL_RE.test(v) ? undefined : 'Enter a number (use . for decimals).';
  if (k === 'uniqueidentifier') return GUID_RE.test(v) ? undefined : 'Enter a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).';
  if (DATETIME2_TYPES.has(k) || k === 'datetimeoffset' || k === 'time') return v ? undefined : 'Enter a date / time (e.g. 2024-05-01 13:45).';
  return undefined;
}

/**
 * The T-SQL literal of a value for a column of `type` (validate first: an invalid number becomes a quoted string and
 * fails the save, never SQL text). Numbers and bit are unquoted; date / time values are cast through the widest type
 * so ISO text with 7 fractional digits converts in every language; everything else is an N'...' string.
 */
export function sqlLiteral(type: string, value: string | null): string {
  if (value === null) return 'NULL';
  const k = key(type);
  const v = value.trim();
  if (k === 'bit' && BIT_VALUES[v.toLowerCase()]) return BIT_VALUES[v.toLowerCase()];
  if (INTEGER_TYPES.has(k) && INT_RE.test(v)) return v;
  if (FLOAT_TYPES.has(k) && FLOAT_RE.test(v)) return v;
  if (isNumericType(k) && DECIMAL_RE.test(v)) return v;
  if (DATETIME2_TYPES.has(k)) return `CAST(${sqlString(v)} AS datetime2(7))`;
  if (k === 'datetimeoffset') return `CAST(${sqlString(v)} AS datetimeoffset(7))`;
  if (k === 'time') return `CAST(${sqlString(v)} AS time(7))`;
  return sqlString(value);
}

/** `[col] = literal`, or `[col] IS NULL`. */
function equals(column: GridColumn, value: string | null): string {
  return value === null ? `${bracket(column.name)} IS NULL` : `${bracket(column.name)} = ${sqlLiteral(column.type, value)}`;
}

export interface SaveScript { script: string; updated: number; deleted: number; inserted: number }

/** A problem found before saving: the row (0-based, loaded rows then new rows), column and message. */
export interface CellProblem { row: number; col: number; message: string }

/** Every pending value that cannot be saved (type check, NULL in a NOT NULL column). */
export function pendingProblems(p: PendingChanges, columns: readonly GridColumn[], meta: readonly ColumnMeta[], loaded: number): CellProblem[] {
  const nullable = (c: number) => meta.find(m => m.name === columns[c].name)?.nullable ?? true;
  const out: CellProblem[] = [];
  for (const [row, cells] of p.edits) {
    if (p.deletes.has(row)) continue;
    for (const [col, value] of cells) {
      const message = validateValue(columns[col].type, value, nullable(col));
      if (message) out.push({ row, col, message });
    }
  }
  p.inserts.forEach((cells, i) => cells.forEach((value, col) => {
    if (value === undefined) return;
    const message = validateValue(columns[col].type, value, nullable(col));
    if (message) out.push({ row: loaded + i, col, message });
  }));
  return out;
}

/**
 * The save script: UPDATEs (by primary key, plus an equality check of every edited comparable column against its
 * loaded value), DELETEs (by primary key), then INSERTs, in one transaction. Each UPDATE / DELETE must affect exactly
 * one row, otherwise everything is rolled back with an error naming the row number shown in the grid.
 */
export function buildSaveScript(
  object: { schema?: string; name: string }, columns: readonly GridColumn[], rows: readonly (readonly unknown[])[], pk: readonly string[], p: PendingChanges,
): SaveScript {
  const table = qualified(object.schema, object.name);
  const pkCols = pk.map(name => columns.findIndex(c => c.name === name));
  if ((p.edits.size || p.deletes.size) && (!pk.length || pkCols.some(i => i < 0))) throw new Error('Rows cannot be changed or deleted without a primary key.');
  const where = (row: number): string[] => pkCols.map(i => equals(columns[i], valueText(rows[row][i])));
  const check = (row: number, action: string) =>
    `IF @@ROWCOUNT <> 1 BEGIN IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; RAISERROR(N'Row %d was changed or deleted by someone else since it was loaded, so it was not ${action}. Nothing was saved: reload and try again.', 16, 1, ${row + 1}); RETURN; END`;
  const lines = ['SET XACT_ABORT ON;', 'BEGIN TRANSACTION;'];
  let updated = 0;
  let deleted = 0;
  for (const [row, cells] of [...p.edits].sort((a, b) => a[0] - b[0])) {
    if (p.deletes.has(row) || !cells.size) continue;
    const sets = [...cells].map(([c, v]) => `${bracket(columns[c].name)} = ${sqlLiteral(columns[c].type, v)}`);
    const guards = [...cells.keys()]
      .filter(c => !pk.includes(columns[c].name) && !NO_COMPARE_TYPES.has(key(columns[c].type)) && isEditableType(columns[c].type))
      .map(c => equals(columns[c], valueText(rows[row][c])));
    lines.push(`UPDATE ${table} SET ${sets.join(', ')} WHERE ${[...where(row), ...guards].join(' AND ')};`, check(row, 'updated'));
    updated++;
  }
  for (const row of [...p.deletes].sort((a, b) => a - b)) {
    lines.push(`DELETE FROM ${table} WHERE ${where(row).join(' AND ')};`, check(row, 'deleted'));
    deleted++;
  }
  for (const cells of p.inserts) {
    const set = cells.map((v, c) => [c, v] as const).filter((e): e is readonly [number, string | null] => e[1] !== undefined);
    lines.push(set.length
      ? `INSERT INTO ${table} (${set.map(([c]) => bracket(columns[c].name)).join(', ')}) VALUES (${set.map(([c, v]) => sqlLiteral(columns[c].type, v)).join(', ')});`
      : `INSERT INTO ${table} DEFAULT VALUES;`);
  }
  lines.push('COMMIT TRANSACTION;');
  return { script: lines.join('\n'), updated, deleted, inserted: p.inserts.length };
}

/** Edit messages from the Data View webview. */
export type EditMessage =
  | { type: 'edit'; row: number; col: number; value: string | null }
  | { type: 'deleteRows'; rows: number[]; scroll?: [number, number] }
  | { type: 'revertRows'; rows: number[]; scroll?: [number, number] }
  | { type: 'addRow'; scroll?: [number, number] }
  | { type: 'save'; scroll?: [number, number] }
  | { type: 'discard'; scroll?: [number, number] };

/** Longest value accepted from a cell editor. */
export const MAX_EDIT_VALUE = 1_000_000;

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const intIn = (v: unknown, max: number): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 0 && v < max;
function scroll(v: unknown): [number, number] | undefined {
  return Array.isArray(v) && v.length === 2 && v.every(n => typeof n === 'number' && Number.isInteger(n) && n >= 0 && n <= 10_000_000)
    ? [v[0] as number, v[1] as number] : undefined;
}

/**
 * An edit message validated against the grid on screen (`rows` = loaded + new rows, `writable` per column, the
 * capabilities) and the current gen; undefined when anything is off.
 */
export function parseEditMessage(raw: unknown, o: { gen: number; loaded: number; inserted: number; writable: readonly boolean[]; caps: EditCapabilities }): EditMessage | undefined {
  if (!isRecord(raw) || raw.gen !== o.gen) return undefined;
  const total = o.loaded + o.inserted;
  const sc = scroll(raw.scroll);
  const withScroll = (m: EditMessage): EditMessage => (sc ? { ...m, scroll: sc } as EditMessage : m);
  const rowList = (v: unknown): number[] | undefined => {
    if (!Array.isArray(v) || !v.length || v.length > total) return undefined;
    const seen = new Set<number>();
    for (const r of v) { if (!intIn(r, total) || seen.has(r)) return undefined; seen.add(r); }
    return v as number[];
  };
  switch (raw.type) {
    case 'edit': {
      if (!intIn(raw.row, total) || !intIn(raw.col, o.writable.length) || !o.writable[raw.col]) return undefined;
      if (raw.row < o.loaded ? !o.caps.update : !o.caps.insert) return undefined;
      if (raw.value !== null && (typeof raw.value !== 'string' || raw.value.length > MAX_EDIT_VALUE)) return undefined;
      return { type: 'edit', row: raw.row, col: raw.col, value: raw.value as string | null };
    }
    case 'deleteRows': {
      const rows = rowList(raw.rows);
      if (!rows || (rows.some(r => r < o.loaded) && !o.caps.delete)) return undefined;
      return withScroll({ type: 'deleteRows', rows });
    }
    case 'revertRows': {
      const rows = rowList(raw.rows);
      return rows ? withScroll({ type: 'revertRows', rows }) : undefined;
    }
    case 'addRow':
      return o.caps.insert ? withScroll({ type: 'addRow' }) : undefined;
    case 'save':
    case 'discard':
      return withScroll({ type: raw.type });
    default:
      return undefined;
  }
}
