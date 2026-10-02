// The data grid's model, shared by Data View and the Results panel: column types, cell text, the sort comparator,
// the Data View query and webview message validation. No 'vscode' import (unit-testable).
//
// compareGridValues and gridSortOrder are also inlined into the webview script (Function.prototype.toString), so
// they must stay self-contained function declarations: no imports, no outer helpers.
import { qualified, bracket } from '../explorer/sqlText';

export type SortDir = 'asc' | 'desc';

export interface GridColumn { name: string; type: string }

/** The TOP of Data View: an integer in MIN_TOP..MAX_TOP, DEFAULT_TOP when the setting is unusable. */
export const MIN_TOP = 1;
export const MAX_TOP = 10_000;
export const DEFAULT_TOP = 200;

/** Cell tooltips show at most this many characters of the value. */
export const TOOLTIP_MAX = 2000;

/**
 * SQL Server types shown right-aligned and sorted as numbers. The server sends decimal / numeric / money /
 * smallmoney, and bigint values beyond +/-2^53, as exact strings, so this follows the column type, not the JS type.
 */
const NUMERIC_TYPES = new Set(['bigint', 'int', 'smallint', 'tinyint', 'decimal', 'numeric', 'money', 'smallmoney', 'float', 'real']);

/** Types SQL Server cannot ORDER BY (or that Data View does not offer to sort by). CLR UDTs are added by name shape. */
const UNSORTABLE_TYPES = new Set(['text', 'ntext', 'image', 'xml', 'geography', 'geometry', 'hierarchyid', 'sql_variant']);

/** The suffix the server appends to a string or binary value it cut at its cell cap (65536 chars / 32768 bytes). */
export const TRUNCATED_SUFFIX = /… \(truncated, (\d+) (chars|bytes)\)$/;

const typeKey = (type: string): string => type.trim().toLowerCase();

export function isNumericType(type: string): boolean {
  return NUMERIC_TYPES.has(typeKey(type));
}

/**
 * Whether Data View can ORDER BY a column of `type` (the run_script column type, i.e. SqlDataReader.GetDataTypeName).
 * A CLR UDT is reported with a qualified name (`db.schema.type`), so any dotted name counts as a UDT. An empty type
 * (unknown) is not sortable.
 */
export function isSortableType(type: string): boolean {
  const key = typeKey(type);
  return key.length > 0 && !key.includes('.') && !UNSORTABLE_TYPES.has(key);
}

/** The display text of a non-NULL value: strings as they are, objects as JSON, the rest via String(). */
export function cellText(value: unknown): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'object' && value !== null) return JSON.stringify(value);
  return String(value);
}

/**
 * What the copy button puts on the clipboard: NULL as an empty string, everything else (including `0x…` binary
 * text) as its full display text.
 */
export function copyText(value: unknown): string {
  return value === null || value === undefined ? '' : cellText(value);
}

/** A cell tooltip: the value, cut to {@link TOOLTIP_MAX} characters (the last one an ellipsis) when longer. */
export function tooltipText(text: string): string {
  return text.length > TOOLTIP_MAX ? `${text.slice(0, TOOLTIP_MAX - 1)}…` : text;
}

/**
 * Orders two cell values for the local sort: NULL (null) first, then numbers by value when `numeric` (exact for
 * plain decimal strings, so `decimal(38)` and big `bigint` text compare correctly), otherwise localeCompare.
 * Values of a numeric column that are not numbers fall back to localeCompare.
 */
export function compareGridValues(a: string | null, b: string | null, numeric: boolean): number {
  if (a === null || b === null) return a === b ? 0 : a === null ? -1 : 1;
  if (numeric) {
    const plain = /^\s*([-+]?)(\d+)(?:\.(\d*))?\s*$/;
    const ma = plain.exec(a);
    const mb = plain.exec(b);
    if (ma && mb) {
      const parts = (m: RegExpExecArray) => {
        const int = m[2].replace(/^0+(?=\d)/, '');
        const frac = (m[3] || '').replace(/0+$/, '');
        return { neg: m[1] === '-' && !(int === '0' && frac === ''), int, frac };
      };
      const x = parts(ma);
      const y = parts(mb);
      if (x.neg !== y.neg) return x.neg ? -1 : 1;
      let abs = 0;
      if (x.int.length !== y.int.length) abs = x.int.length < y.int.length ? -1 : 1;
      else if (x.int !== y.int) abs = x.int < y.int ? -1 : 1;
      else {
        const len = Math.max(x.frac.length, y.frac.length);
        const fx = x.frac.padEnd(len, '0');
        const fy = y.frac.padEnd(len, '0');
        abs = fx === fy ? 0 : fx < fy ? -1 : 1;
      }
      return x.neg ? -abs : abs;
    }
    const na = Number(a);
    const nb = Number(b);
    if (a.trim() !== '' && b.trim() !== '' && !Number.isNaN(na) && !Number.isNaN(nb)) return na < nb ? -1 : na > nb ? 1 : 0;
  }
  return a.localeCompare(b);
}

/**
 * The row order of a stable sort of `values` (one per row, null = NULL): the original row indexes in display
 * order. Ascending puts NULLs first; descending reverses the order of the values but keeps equal values in their
 * original order.
 */
export function gridSortOrder(values: (string | null)[], numeric: boolean, dir: SortDir): number[] {
  const order = values.map((_, i) => i);
  order.sort((x, y) => {
    const c = compareGridValues(values[x], values[y], numeric);
    return (dir === 'desc' ? -c : c) || x - y;
  });
  return order;
}

/** A Data View TOP from the webview or a setting: an integer (or digit string) in MIN_TOP..MAX_TOP, else undefined. */
export function parseTop(value: unknown): number | undefined {
  const n = typeof value === 'number' ? value : typeof value === 'string' && /^\s*\d+\s*$/.test(value) ? Number(value) : NaN;
  return Number.isInteger(n) && n >= MIN_TOP && n <= MAX_TOP ? n : undefined;
}

/** The `msSqlMcp.dataViewRows` setting as Data View uses it: clamped to MIN_TOP..MAX_TOP, DEFAULT_TOP when unusable. */
export function clampTop(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return DEFAULT_TOP;
  return Math.min(MAX_TOP, Math.max(MIN_TOP, Math.floor(value)));
}

/** A server-side Data View sort: the column (name and type, from the last result) and its direction. */
export interface DataViewOrder { column: string; type: string; dir: SortDir }

/**
 * The Data View query. Only bracket-quoted identifiers (from the tree's object ref and the last result's columns)
 * and the validated integer reach the SQL. It asks for TOP (top + 1) rows; run with `maxRows: top`, the server
 * keeps `top` and reports truncated=true when the object has more. ORDER BY is added only for a sortable column
 * type. Throws RangeError for a TOP outside MIN_TOP..MAX_TOP.
 */
export function dataViewSql(object: { schema?: string; name: string }, top: number, order?: DataViewOrder): string {
  if (typeof top !== 'number' || parseTop(top) === undefined) throw new RangeError(`TOP must be an integer from ${MIN_TOP} to ${MAX_TOP}.`);
  const orderBy = order && isSortableType(order.type) && (order.dir === 'asc' || order.dir === 'desc')
    ? ` ORDER BY ${bracket(order.column)} ${order.dir === 'asc' ? 'ASC' : 'DESC'}`
    : '';
  return `SELECT TOP (${top + 1}) * FROM ${qualified(object.schema, object.name)}${orderBy}`;
}

// --- Webview messages ------------------------------------------------------------------------------------------

export type DataViewMessage =
  | { type: 'copy'; row: number; col: number }
  | { type: 'sort'; col: number; dir: SortDir | 'none' }
  | { type: 'reload'; top: number };

export type ResultsMessage =
  | { type: 'copy'; set: number; row: number; col: number }
  | { type: 'reveal'; line: number }
  | { type: 'cancel' };

/** Rows and columns of one rendered grid, for index validation. */
export interface GridDims { rows: number; cols: number }

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const index = (v: unknown, length: number): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 0 && v < length;

/** A Data View webview message, validated against the grid on screen; undefined when anything is off. */
export function parseDataViewMessage(raw: unknown, dims: GridDims): DataViewMessage | undefined {
  if (!isRecord(raw)) return undefined;
  switch (raw.type) {
    case 'copy':
      return index(raw.row, dims.rows) && index(raw.col, dims.cols) ? { type: 'copy', row: raw.row, col: raw.col } : undefined;
    case 'sort':
      return index(raw.col, dims.cols) && (raw.dir === 'asc' || raw.dir === 'desc' || raw.dir === 'none')
        ? { type: 'sort', col: raw.col, dir: raw.dir }
        : undefined;
    case 'reload': {
      const top = typeof raw.top === 'number' ? parseTop(raw.top) : undefined;
      return top === undefined ? undefined : { type: 'reload', top };
    }
    default:
      return undefined;
  }
}

/** A Results webview message, validated against the result sets on screen; undefined when anything is off. */
export function parseResultsMessage(raw: unknown, sets: readonly GridDims[]): ResultsMessage | undefined {
  if (!isRecord(raw)) return undefined;
  switch (raw.type) {
    case 'copy': {
      if (!index(raw.set, sets.length)) return undefined;
      const dims = sets[raw.set];
      return index(raw.row, dims.rows) && index(raw.col, dims.cols) ? { type: 'copy', set: raw.set, row: raw.row, col: raw.col } : undefined;
    }
    case 'reveal':
      return typeof raw.line === 'number' && Number.isInteger(raw.line) ? { type: 'reveal', line: raw.line } : undefined;
    case 'cancel':
      return { type: 'cancel' };
    default:
      return undefined;
  }
}

/** The value at (row, col) of `rows` (a missing cell of a short row is undefined, i.e. copies as NULL). */
export function cellAt(rows: readonly (readonly unknown[])[], row: number, col: number): unknown {
  return rows[row]?.[col];
}
