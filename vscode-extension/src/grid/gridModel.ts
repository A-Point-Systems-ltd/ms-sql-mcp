// The data grid's model, shared by Data View and the Results panel: column types, cell text, the sort comparator,
// the Data View query and webview message validation. No 'vscode' import (unit-testable).
//
// BUILD NOTE: the functions marked "Inlined." (compareGridValues, gridSortOrder, tooltipText, cellMatches,
// countMatches, filterMatches, numericStats; the list is gridHtml's INLINED map) are also copied into the webview
// script with Function.prototype.toString. They must stay self-contained function declarations: no imports, no module
// constants, no helpers outside that list, nothing tsc would rewrite to `exports.x` (test/grid.test.mjs runs every
// inlined copy in isolation to guard this).
import { bracket, qualified, sqlString } from '../explorer/sqlText';

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

/** A cell tooltip: the value, cut to {@link TOOLTIP_MAX} characters (the last one an ellipsis) when longer. Inlined. */
export function tooltipText(text: string): string {
  const max = 2000; // = TOOLTIP_MAX (kept literal: this function is inlined into the webview)
  return text.length > max ? text.slice(0, max - 1) + '…' : text;
}

/**
 * Orders two cell values for the local sort: NULL (null) first, then numbers by value when `numeric` (exact for
 * plain decimal strings, so `decimal(38)` and big `bigint` text compare correctly), otherwise localeCompare.
 * Values of a numeric column that are not numbers fall back to the text compare. `compare` replaces localeCompare
 * for text (the webview passes a shared Intl.Collator's compare). Inlined.
 */
export function compareGridValues(a: string | null, b: string | null, numeric: boolean, compare?: (x: string, y: string) => number): number {
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
  return compare ? compare(a, b) : a.localeCompare(b);
}

/**
 * The row order of a stable sort of `values` (one per row, null = NULL): the original row indexes in display
 * order. Ascending puts NULLs first; descending reverses the order of the values but keeps equal values in their
 * original order. `compare` is passed on to compareGridValues. Inlined.
 */
export function gridSortOrder(values: (string | null)[], numeric: boolean, dir: SortDir, compare?: (x: string, y: string) => number): number[] {
  const order = values.map((_, i) => i);
  order.sort((x, y) => {
    const c = compareGridValues(values[x], values[y], numeric, compare);
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

// --- Filters -----------------------------------------------------------------------------------------------------

export type FilterOp = 'contains' | 'eq' | 'starts' | 'null' | 'notnull' | 'period';
export const FILTER_OPS: readonly FilterOp[] = ['contains', 'eq', 'starts', 'period', 'null', 'notnull'];
export const FILTER_OP_LABELS: Readonly<Record<FilterOp, string>> = {
  contains: 'contains', eq: '=', starts: 'starts with', null: 'is null', notnull: 'is not null', period: 'from / to',
};

/** Date and time types: they offer the period (from / to date) filter. */
const DATE_TYPES = new Set(['date', 'datetime', 'datetime2', 'smalldatetime', 'datetimeoffset']);
/** Types whose filter defaults to = (contains is rarely what is meant for a number or a flag). */
const EQ_DEFAULT_TYPES = new Set(['bigint', 'int', 'smallint', 'tinyint', 'bit']);

export function isDateType(type: string): boolean {
  return DATE_TYPES.has(typeKey(type));
}

/** The operator a column's filter starts with: = for integers and bit, from / to for dates, else contains. */
export function defaultFilterOp(type: string): FilterOp {
  const key = typeKey(type);
  if (EQ_DEFAULT_TYPES.has(key)) return 'eq';
  if (DATE_TYPES.has(key)) return 'period';
  return 'contains';
}

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** A real calendar date in YYYY-MM-DD form (years 1..9999), else undefined. */
function isoDate(text: string): { y: number; m: number; d: number } | undefined {
  const m = ISO_DATE.exec(text);
  if (!m) return undefined;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const t = new Date(0);
  t.setUTCFullYear(y, mo - 1, d);
  return y >= 1 && t.getUTCFullYear() === y && t.getUTCMonth() === mo - 1 && t.getUTCDate() === d ? { y, m: mo, d } : undefined;
}

/**
 * A period filter value, `from..to` (both YYYY-MM-DD, either may be empty, both inclusive): the parts, or undefined
 * when malformed. `{}` (both empty) is an inactive filter.
 */
export function parsePeriod(value: string): { from?: string; to?: string } | undefined {
  const sep = value.indexOf('..');
  if (sep < 0 || value.indexOf('..', sep + 2) >= 0) return undefined;
  const from = value.slice(0, sep);
  const to = value.slice(sep + 2);
  if ((from && !isoDate(from)) || (to && !isoDate(to))) return undefined;
  return { ...(from ? { from } : {}), ...(to ? { to } : {}) };
}

/** YYYYMMDD of a YYYY-MM-DD date plus `days` (the unseparated form SQL Server reads the same in every language). */
function compactDate(iso: string, days = 0): string | undefined {
  const p = isoDate(iso);
  if (!p) return undefined;
  const t = new Date(0);
  t.setUTCFullYear(p.y, p.m - 1, p.d + days);
  const y = t.getUTCFullYear();
  if (y < 1 || y > 9999) return undefined;
  return String(y).padStart(4, '0') + String(t.getUTCMonth() + 1).padStart(2, '0') + String(t.getUTCDate()).padStart(2, '0');
}
/** Longest filter value accepted from the webview. */
export const MAX_FILTER_VALUE = 4000;

/** A Data View filter as stored: the column (name and type from the result it was set on), operator and value. */
export interface DataViewFilter { column: string; type: string; op: FilterOp; value: string }

/** Types whose text cannot be compared server-side (binary, spatial, xml, ...): they offer only is null / is not null. */
const NO_TEXT_FILTER_TYPES = new Set(['image', 'binary', 'varbinary', 'timestamp', 'rowversion', 'xml', 'geography', 'geometry', 'hierarchyid', 'sql_variant']);

/** Whether contains / = / starts with can be applied server-side to a column of `type` (CAST to NVARCHAR(MAX)). */
export function isTextFilterable(type: string): boolean {
  const key = typeKey(type);
  return key.length > 0 && !key.includes('.') && !NO_TEXT_FILTER_TYPES.has(key);
}

/** Escapes a value for LIKE ... ESCAPE N'\': the escape character itself, then %, _ and [. */
export function likeEscape(value: string): string {
  return value.replace(/[\\%_[]/g, m => `\\${m}`);
}

/**
 * The WHERE predicate of one filter, or undefined when it is inactive (a value operator with an empty value) or
 * not allowed for the column type. The column goes through bracket(), the value only through sqlString().
 */
export function filterPredicate(f: DataViewFilter): string | undefined {
  const col = bracket(f.column);
  switch (f.op) {
    case 'null': return `${col} IS NULL`;
    case 'notnull': return `${col} IS NOT NULL`;
    case 'period': {
      // Inclusive dates: col >= from AND col < the day after `to`, as date literals (sargable, no CAST of the column).
      const p = isDateType(f.type) ? parsePeriod(f.value) : undefined;
      if (!p) return undefined;
      const parts: string[] = [];
      const from = p.from ? compactDate(p.from) : undefined;
      if (from) parts.push(`${col} >= '${from}'`);
      if (p.to) {
        const next = compactDate(p.to, 1);
        // The day after 9999-12-31 does not exist: `to` is then no bound at all.
        if (next) parts.push(`${col} < '${next}'`);
      }
      return parts.length ? parts.join(' AND ') : undefined;
    }
    case 'contains':
    case 'starts':
    case 'eq': {
      if (f.value === '' || !isTextFilterable(f.type)) return undefined;
      const text = `CAST(${col} AS NVARCHAR(MAX))`;
      if (f.op === 'eq') {
        // A bit column reads as true / false in the grid but casts to 1 / 0.
        const bit = typeKey(f.type) === 'bit' ? ({ true: '1', false: '0' } as Record<string, string>)[f.value.trim().toLowerCase()] : undefined;
        return `${text} = ${sqlString(bit ?? f.value)}`;
      }
      const pattern = f.op === 'contains' ? `%${likeEscape(f.value)}%` : `${likeEscape(f.value)}%`;
      return `${text} LIKE ${sqlString(pattern)} ESCAPE ${sqlString('\\')}`;
    }
    default:
      return undefined;
  }
}

/** ` WHERE p1 AND p2 ...` for the active filters, or '' when none is active. */
export function whereSql(filters: readonly DataViewFilter[] | undefined): string {
  const parts = (filters ?? []).map(filterPredicate).filter((p): p is string => p !== undefined);
  return parts.length ? ` WHERE ${parts.join(' AND ')}` : '';
}

/** Stored filters checked against a new result: kept with the current type when the column is still there. */
export function reconcileFilters(filters: readonly DataViewFilter[], columns: readonly GridColumn[]): DataViewFilter[] {
  return filters.flatMap(f => {
    const c = columns.find(col => col.name === f.column);
    return c ? [{ ...f, type: c.type }] : [];
  });
}

// --- Data View queries -------------------------------------------------------------------------------------------

/** The helper column of the paging query; stripped from every result before it is shown, copied or exported. */
export const ROW_NUMBER_COLUMN = '__apms_rn';
/** At most this many rows are loaded into one Data View (first page plus Load more). */
export const MAX_LOADED_ROWS = 10_000;
export const NO_ORDER_NOTE = 'Row order is not guaranteed without a primary key or sort; pages may overlap.';
/** Shown instead of NO_ORDER_NOTE when there is a sort but no primary key to break ties. */
export const NO_PK_NOTE = 'Without a primary key, rows with equal sort values may repeat or be skipped between pages.';
export const LOAD_CAP_NOTE = 'Refine the filter to see more.';

/** The paging note for a Data View without a primary key (undefined when it has one). */
export function pagingNote(order: DataViewOrder | undefined, pk: readonly string[]): string | undefined {
  if (pk.length) return undefined;
  return orderKeys(order, []).length ? NO_PK_NOTE : NO_ORDER_NOTE;
}

/** The parameters the loaded rows of a Data View were queried with. Load more pages only with these. */
export interface LoadedWith { sort?: DataViewOrder; filters: DataViewFilter[]; pk: string[]; top: number }

/**
 * The sort and filters to show (sort indicator, filter row) after a query: the requested ones when it succeeded,
 * those of the rows still on screen (`loadedWith`) when it failed. Without loaded rows the requested ones stay.
 */
export function displayedParams(
  requested: { sort?: DataViewOrder; filters: DataViewFilter[] }, loadedWith: LoadedWith | undefined, failed: boolean,
): { sort?: DataViewOrder; filters: DataViewFilter[] } {
  if (!failed || !loadedWith) return { ...(requested.sort ? { sort: requested.sort } : {}), filters: [...requested.filters] };
  return { ...(loadedWith.sort ? { sort: loadedWith.sort } : {}), filters: [...loadedWith.filters] };
}

/** The sort indicator (column index and direction) of `sort` over `columns`, or undefined. */
export function sortIndicator(sort: DataViewOrder | undefined, columns: readonly GridColumn[]): { col: number; dir: SortDir } | undefined {
  if (!sort) return undefined;
  const col = columns.findIndex(c => c.name === sort.column);
  return col >= 0 ? { col, dir: sort.dir } : undefined;
}

/**
 * The next Load more page for `loaded` rows already on screen, built only from `loadedWith`: rows loaded+1 ..
 * loaded+size+1 (one extra row tells whether more exist; run with maxRows = size). Undefined at the load cap.
 */
export function nextPageRequest(object: { schema?: string; name: string }, loadedWith: LoadedWith, loaded: number): { script: string; maxRows: number; from: number; to: number } | undefined {
  const size = Math.min(loadedWith.top, MAX_LOADED_ROWS - loaded);
  if (size <= 0) return undefined;
  const from = loaded + 1;
  const to = loaded + size + 1;
  return { script: dataViewPageSql(object, from, to, loadedWith.sort, { pk: loadedWith.pk, filters: loadedWith.filters }), maxRows: size, from, to };
}

/**
 * Whether a query error names one of the primary-key columns (then it is worth retrying without the key).
 * SQL Server quotes column names in these messages ("Invalid column name 'Id'."), so only a quoted match counts:
 * a bare substring would match "Id" inside "Invalid" or "Process ID".
 */
export function errorNamesKey(error: string, pk: readonly string[]): boolean {
  const e = error.toLowerCase();
  return pk.some(k => k.length > 0 && e.includes(`'${k.toLowerCase()}'`));
}

/** Extra parts of a Data View query: primary-key tie-breakers and filters. */
export interface DataViewQueryOptions { pk?: readonly string[]; filters?: readonly DataViewFilter[] }

/**
 * ORDER BY items: the user's sort column (when sortable), then the primary-key columns (not already the sort
 * column) ascending as tie-breakers. Every name goes through bracket().
 */
export function orderKeys(order: DataViewOrder | undefined, pk: readonly string[] = []): string[] {
  const keys: string[] = [];
  const sorted = order && isSortableType(order.type) && (order.dir === 'asc' || order.dir === 'desc') ? order : undefined;
  if (sorted) keys.push(`${bracket(sorted.column)} ${sorted.dir === 'asc' ? 'ASC' : 'DESC'}`);
  for (const k of pk) if (!sorted || k !== sorted.column) keys.push(`${bracket(k)} ASC`);
  return keys;
}

const assertTop = (top: unknown): void => {
  if (typeof top !== 'number' || parseTop(top) === undefined) throw new RangeError(`TOP must be an integer from ${MIN_TOP} to ${MAX_TOP}.`);
};

/**
 * The Data View query (first page). Only bracket-quoted identifiers (from the tree's object ref and the last
 * result's columns), sqlString() literals and the validated integer reach the SQL. It asks for TOP (top + 1) rows;
 * run with `maxRows: top`, the server keeps `top` and reports truncated=true when the object has more. ORDER BY is
 * the sort column (when sortable) and the primary key. Throws RangeError for a TOP outside MIN_TOP..MAX_TOP.
 */
export function dataViewSql(object: { schema?: string; name: string }, top: number, order?: DataViewOrder, opts: DataViewQueryOptions = {}): string {
  assertTop(top);
  const keys = orderKeys(order, opts.pk);
  return `SELECT TOP (${top + 1}) * FROM ${qualified(object.schema, object.name)}${whereSql(opts.filters)}`
    + (keys.length ? ` ORDER BY ${keys.join(', ')}` : '');
}

/**
 * A Load more page: rows `from`..`to` (1-based, inclusive) of the same filtered, ordered set, numbered with
 * ROW_NUMBER() (SQL Server 2005+, so 2008 R2 is fine). Without sort or primary key the numbering uses (SELECT NULL)
 * and the order is not guaranteed. The bounds must be integers with 1 <= from <= to <= MAX_LOADED_ROWS + 1.
 */
export function dataViewPageSql(object: { schema?: string; name: string }, from: number, to: number, order?: DataViewOrder, opts: DataViewQueryOptions = {}): string {
  const ok = (n: unknown): n is number => typeof n === 'number' && Number.isInteger(n) && n >= 1 && n <= MAX_LOADED_ROWS + 1;
  if (!ok(from) || !ok(to) || from > to) throw new RangeError(`Page bounds must be integers with 1 <= from <= to <= ${MAX_LOADED_ROWS + 1}.`);
  const keys = orderKeys(order, opts.pk);
  const rn = bracket(ROW_NUMBER_COLUMN);
  return `SELECT * FROM (SELECT *, ROW_NUMBER() OVER (ORDER BY ${keys.length ? keys.join(', ') : '(SELECT NULL)'}) AS ${rn}`
    + ` FROM ${qualified(object.schema, object.name)}${whereSql(opts.filters)}) AS q WHERE ${rn} BETWEEN ${from} AND ${to} ORDER BY ${rn}`;
}

/** A result set without the paging helper column (when it is the last column), rows trimmed to match. */
export function stripRowNumber<T extends { columns: GridColumn[]; rows: unknown[][] }>(set: T): T {
  const last = set.columns.length - 1;
  if (last < 0 || set.columns[last].name !== ROW_NUMBER_COLUMN) return set;
  return { ...set, columns: set.columns.slice(0, last), rows: set.rows.map(r => r.slice(0, last)) };
}

/**
 * Primary-key column names from a describe_table `data` payload (`constraints[]` with `type` PRIMARY_KEY_CONSTRAINT
 * and comma-joined `keys`, in key order), or [] when there is none.
 */
export function parsePrimaryKey(data: unknown): string[] {
  const constraints = isRecord(data) ? (data.constraints ?? data.Constraints) : undefined;
  if (!Array.isArray(constraints)) return [];
  for (const c of constraints) {
    if (!isRecord(c)) continue;
    const type = String(c.type ?? c.Type ?? '');
    const keys = c.keys ?? c.Keys;
    if (/PRIMARY_KEY/i.test(type) && typeof keys === 'string' && keys.length) return keys.split(',');
  }
  return [];
}

/**
 * The primary key when every key column is a result column (a name holding ',' cannot be split back reliably, so
 * such a key, or one that no longer matches, is not used), else [].
 */
export function usablePrimaryKey(pk: readonly string[], columns: readonly GridColumn[]): string[] {
  const names = new Set(columns.map(c => c.name));
  return pk.length && pk.every(k => names.has(k)) ? [...pk] : [];
}

/** The note shown when a reload no longer has the sorted column (the sort is then cleared). */
export const SORT_COLUMN_GONE = 'Sort column no longer exists';

/**
 * The stored Data View sort checked against a new result's columns: kept (with the column's current type) when the
 * column is still there and sortable, otherwise cleared with {@link SORT_COLUMN_GONE}. `col` is its index or -1.
 */
export function reconcileSort(sort: DataViewOrder | undefined, columns: readonly GridColumn[]): { sort?: DataViewOrder; col: number; note?: string } {
  if (!sort) return { col: -1 };
  const col = columns.findIndex(c => c.name === sort.column);
  if (col < 0 || !isSortableType(columns[col].type)) return { col: -1, note: SORT_COLUMN_GONE };
  return { sort: { ...sort, type: columns[col].type }, col };
}

/** The status bar text after a copy: it says so when the value was cut by the server's cell cap. */
export function copiedMessage(text: string): string {
  return TRUNCATED_SUFFIX.test(text) ? 'Copied (value truncated by the server)' : 'Copied';
}

// --- Inlined client helpers (search, local filter, selection stats) ----------------------------------------------

/** Case-insensitive substring match of the display text (quick search). An empty query matches nothing. Inlined. */
export function cellMatches(text: string, query: string): boolean {
  return query !== '' && text.toLowerCase().indexOf(query.toLowerCase()) !== -1;
}

/** How many of `texts` match `query` (quick search count). Inlined. */
export function countMatches(texts: readonly string[], query: string): number {
  let n = 0;
  for (const t of texts) if (cellMatches(t, query)) n++;
  return n;
}

/**
 * The local (Results panel) filter test for one cell: `value` is the display text or null for NULL. contains /
 * starts with / = compare case-insensitively; an empty operand makes them inactive (match). `period` takes
 * `from..to` dates (YYYY-MM-DD, inclusive, either may be empty) and compares the value's leading date. Inlined.
 */
export function filterMatches(value: string | null, op: string, operand: string): boolean {
  if (op === 'null') return value === null;
  if (op === 'notnull') return value !== null;
  if (operand === '') return true;
  if (op === 'period') {
    // from..to (YYYY-MM-DD, inclusive) against the date part of an ISO date / time text.
    const sep = operand.indexOf('..');
    const from = sep < 0 ? '' : operand.slice(0, sep);
    const to = sep < 0 ? '' : operand.slice(sep + 2);
    if (!from && !to) return true;
    if (value === null) return false;
    const day = value.trim().slice(0, 10);
    if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) return false;
    return (!from || day >= from) && (!to || day <= to);
  }
  if (value === null) return false;
  const v = value.toLowerCase();
  const o = operand.toLowerCase();
  if (op === 'eq') return v === o;
  if (op === 'starts') return v.indexOf(o) === 0;
  return v.indexOf(o) !== -1;
}

/**
 * Selection stats of numeric cell texts: count, sum, min, max and avg. Plain decimals (the server's exact decimal /
 * money / bigint text, and ints) are summed exactly with BigInt scaling; if any value is in exponent form (float /
 * real) the stats fall back to doubles and `exact` is false. Blank or non-numeric texts are skipped; undefined when
 * nothing is left. avg keeps 6 more decimals than the inputs (rounded half away from zero, trailing zeros dropped).
 * Inlined.
 */
export function numericStats(values: readonly string[]): { count: number; sum: string; min: string; max: string; avg: string; exact: boolean } | undefined {
  const plain = /^\s*([-+]?)(\d+)(?:\.(\d*))?\s*$/;
  const items: { text: string; m: RegExpExecArray | null }[] = [];
  let exact = true;
  let scale = 0;
  for (const raw of values) {
    const text = raw.trim();
    const m = plain.exec(text);
    if (m) {
      if ((m[3] || '').length > scale) scale = (m[3] || '').length;
      items.push({ text, m });
    } else if (text !== '' && !Number.isNaN(Number(text))) {
      exact = false;
      items.push({ text, m: null });
    }
  }
  if (!items.length) return undefined;
  if (!exact) {
    let sum = 0;
    let min = Infinity;
    let max = -Infinity;
    let minText = '';
    let maxText = '';
    for (const it of items) {
      const n = Number(it.text);
      sum += n;
      if (n < min) { min = n; minText = it.text; }
      if (n > max) { max = n; maxText = it.text; }
    }
    return { count: items.length, sum: String(sum), min: minText, max: maxText, avg: String(sum / items.length), exact: false };
  }
  const toBig = (m: RegExpExecArray): bigint => {
    const digits = m[2] + (m[3] || '').padEnd(scale, '0');
    const b = BigInt(digits);
    return m[1] === '-' ? -b : b;
  };
  const format = (b: bigint, sc: number, trim: boolean): string => {
    const neg = b < BigInt(0);
    let s = (neg ? -b : b).toString();
    if (sc > 0) {
      s = s.padStart(sc + 1, '0');
      let frac = s.slice(s.length - sc);
      if (trim) frac = frac.replace(/0+$/, '');
      s = s.slice(0, s.length - sc) + (frac ? '.' + frac : '');
    }
    return (neg && /[1-9]/.test(s) ? '-' : '') + s;
  };
  let sum = BigInt(0);
  let min: bigint | undefined;
  let max: bigint | undefined;
  let minText = '';
  let maxText = '';
  for (const it of items) {
    const b = toBig(it.m as RegExpExecArray);
    sum += b;
    if (min === undefined || b < min) { min = b; minText = it.text; }
    if (max === undefined || b > max) { max = b; maxText = it.text; }
  }
  const extra = 6;
  const count = BigInt(items.length);
  const scaled = sum * BigInt(10) ** BigInt(extra);
  const negAvg = scaled < BigInt(0);
  const absScaled = negAvg ? -scaled : scaled;
  let q = absScaled / count;
  if ((absScaled % count) * BigInt(2) >= count) q += BigInt(1);
  const avg = format(negAvg ? -q : q, scale + extra, true);
  return { count: items.length, sum: format(sum, scale, false), min: minText, max: maxText, avg, exact: true };
}

// --- Webview messages ------------------------------------------------------------------------------------------

/** The client view of one grid, kept by the extension so a re-render (load more, reload, editor switch) restores it. */
export interface GridViewState {
  /** Display order: a permutation of the column indexes. */
  order: number[];
  /** Hidden column indexes (never all of them). */
  hidden: number[];
  /** How many leading displayed columns are frozen (sticky left). */
  freeze: number;
  wrap: boolean;
  stripe: boolean;
  /** Column widths in px, when the user resized or fitted them. */
  widths?: number[];
}

export const DEFAULT_FREEZE = 1;

/** The default view of a grid with `cols` columns: natural order, nothing hidden, first column frozen, striped. */
export function defaultViewState(cols: number): GridViewState {
  return { order: Array.from({ length: cols }, (_, i) => i), hidden: [], freeze: Math.min(DEFAULT_FREEZE, cols), wrap: false, stripe: true };
}

/** Grid actions shared by Data View and the Results panel. Copy, selection and export carry only indexes. */
export type GridAction =
  | { type: 'copy'; row: number; col: number }
  | { type: 'copyRow'; row: number; cols: number[]; format: 'tsv' | 'json' }
  | { type: 'copySelection'; rows: number[]; cols: number[]; r1: number; c1: number; r2: number; c2: number }
  | { type: 'openCell'; row: number; col: number }
  | { type: 'export'; rows: number[]; cols: number[] }
  | { type: 'viewState'; view: GridViewState };

export interface DataViewFilterInput { col: number; op: FilterOp; value: string }

export type DataViewMessage =
  | GridAction
  | { type: 'sort'; col: number; dir: SortDir | 'none' }
  | { type: 'reload'; top: number; scroll?: [number, number] }
  | { type: 'loadMore'; scroll: [number, number] }
  | { type: 'filter'; filters: DataViewFilterInput[]; focus?: number };

export type ResultsMessage =
  | (GridAction & { set: number })
  | { type: 'reveal'; line: number }
  | { type: 'cancel' };

/**
 * Rows and columns of one rendered grid, for index validation. Every grid action echoes the render counter
 * (`data-gen`) of the page it came from; an action whose gen is not the current render's is ignored.
 */
export interface GridDims { rows: number; cols: number }

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const isGen = (v: unknown, gen: number): v is number => typeof v === 'number' && Number.isInteger(v) && v === gen;
const index = (v: unknown, length: number): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 0 && v < length;

/** Distinct in-range integer indexes (at most `length` of them, at least `min`), or undefined. */
function indexList(v: unknown, length: number, min: number): number[] | undefined {
  if (!Array.isArray(v) || v.length < min || v.length > length) return undefined;
  const seen = new Set<number>();
  for (const x of v) {
    if (!index(x, length) || seen.has(x)) return undefined;
    seen.add(x);
  }
  return v as number[];
}

/** A client view state for a grid of `cols` columns, or undefined when anything is off. */
export function parseViewState(raw: unknown, cols: number): GridViewState | undefined {
  if (!isRecord(raw)) return undefined;
  const order = indexList(raw.order, cols, cols);
  const hidden = indexList(raw.hidden, cols, 0);
  if (!order || !hidden || (cols > 0 && hidden.length >= cols)) return undefined;
  if (typeof raw.freeze !== 'number' || !Number.isInteger(raw.freeze) || raw.freeze < 0 || raw.freeze > cols) return undefined;
  if (typeof raw.wrap !== 'boolean' || typeof raw.stripe !== 'boolean') return undefined;
  let widths: number[] | undefined;
  if (raw.widths !== undefined) {
    if (!Array.isArray(raw.widths) || raw.widths.length !== cols
      || !raw.widths.every(w => typeof w === 'number' && Number.isInteger(w) && w >= 20 && w <= 4000)) return undefined;
    widths = raw.widths as number[];
  }
  return { order, hidden, freeze: raw.freeze, wrap: raw.wrap, stripe: raw.stripe, ...(widths ? { widths } : {}) };
}

/** One of the shared grid actions, validated against the grid on screen and the current gen; undefined when off. */
export function parseGridAction(raw: unknown, dims: GridDims, gen: number): GridAction | undefined {
  if (!isRecord(raw) || !isGen(raw.gen, gen)) return undefined;
  switch (raw.type) {
    case 'copy':
    case 'openCell':
      return index(raw.row, dims.rows) && index(raw.col, dims.cols) ? { type: raw.type, row: raw.row, col: raw.col } : undefined;
    case 'copyRow': {
      const cols = indexList(raw.cols, dims.cols, 1);
      return index(raw.row, dims.rows) && cols && (raw.format === 'tsv' || raw.format === 'json')
        ? { type: 'copyRow', row: raw.row, cols, format: raw.format }
        : undefined;
    }
    case 'copySelection': {
      // rows / cols: the selected rows and columns in display order; r1..r2 / c1..c2 the rectangle in display positions.
      const rows = indexList(raw.rows, dims.rows, 1);
      const cols = indexList(raw.cols, dims.cols, 1);
      const ints = [raw.r1, raw.c1, raw.r2, raw.c2];
      if (!rows || !cols || !ints.every(n => typeof n === 'number' && Number.isInteger(n) && n >= 0)) return undefined;
      const [r1, c1, r2, c2] = ints as number[];
      if (r2 < r1 || c2 < c1 || r2 - r1 + 1 !== rows.length || c2 - c1 + 1 !== cols.length || r2 >= dims.rows || c2 >= dims.cols) return undefined;
      return { type: 'copySelection', rows, cols, r1, c1, r2, c2 };
    }
    case 'export': {
      const rows = indexList(raw.rows, dims.rows, 0);
      const cols = indexList(raw.cols, dims.cols, 1);
      return rows && cols ? { type: 'export', rows, cols } : undefined;
    }
    case 'viewState': {
      const view = parseViewState(raw.view, dims.cols);
      return view ? { type: 'viewState', view } : undefined;
    }
    default:
      return undefined;
  }
}

/**
 * A Data View webview message, validated against the grid on screen; undefined when anything is off. `types` are the
 * column types, so a filter cannot ask for a text operator on a binary / spatial / xml column.
 */
export function parseDataViewMessage(raw: unknown, dims: GridDims, gen: number, types: readonly string[] = []): DataViewMessage | undefined {
  if (!isRecord(raw)) return undefined;
  switch (raw.type) {
    case 'sort':
      return isGen(raw.gen, gen) && index(raw.col, dims.cols) && (raw.dir === 'asc' || raw.dir === 'desc' || raw.dir === 'none')
        ? { type: 'sort', col: raw.col, dir: raw.dir }
        : undefined;
    case 'reload': {
      if (!isGen(raw.gen, gen)) return undefined;
      const top = typeof raw.top === 'number' ? parseTop(raw.top) : undefined;
      const scroll = scrollPair(raw.scroll);
      return top === undefined ? undefined : { type: 'reload', top, ...(scroll ? { scroll } : {}) };
    }
    case 'loadMore': {
      if (!isGen(raw.gen, gen)) return undefined;
      return { type: 'loadMore', scroll: scrollPair(raw.scroll) ?? [0, 0] };
    }
    case 'filter': {
      if (!isGen(raw.gen, gen) || !Array.isArray(raw.filters) || raw.filters.length > dims.cols) return undefined;
      const seen = new Set<number>();
      const filters: DataViewFilterInput[] = [];
      for (const f of raw.filters) {
        if (!isRecord(f) || !index(f.col, dims.cols) || seen.has(f.col)) return undefined;
        if (typeof f.op !== 'string' || !(FILTER_OPS as readonly string[]).includes(f.op)) return undefined;
        if (typeof f.value !== 'string' || f.value.length > MAX_FILTER_VALUE) return undefined;
        const op = f.op as FilterOp;
        const textOp = op === 'contains' || op === 'eq' || op === 'starts';
        if (textOp && f.value !== '' && types[f.col] !== undefined && !isTextFilterable(types[f.col])) return undefined;
        if (op === 'period' && ((types[f.col] !== undefined && !isDateType(types[f.col])) || !parsePeriod(f.value))) return undefined;
        seen.add(f.col);
        filters.push({ col: f.col, op, value: textOp || op === 'period' ? f.value : '' });
      }
      const focus = raw.focus === undefined ? undefined : index(raw.focus, dims.cols) ? raw.focus : undefined;
      return { type: 'filter', filters, ...(focus !== undefined ? { focus } : {}) };
    }
    default:
      return parseGridAction(raw, dims, gen);
  }
}

/** A [top, left] scroll offset pair from the webview, or undefined. */
function scrollPair(s: unknown): [number, number] | undefined {
  return Array.isArray(s) && s.length === 2 && s.every(n => typeof n === 'number' && Number.isInteger(n) && n >= 0 && n <= 10_000_000)
    ? [s[0] as number, s[1] as number] : undefined;
}

/** A Results webview message, validated against the result sets on screen; undefined when anything is off. */
export function parseResultsMessage(raw: unknown, sets: readonly GridDims[], gen: number): ResultsMessage | undefined {
  if (!isRecord(raw)) return undefined;
  switch (raw.type) {
    case 'reveal':
      return typeof raw.line === 'number' && Number.isInteger(raw.line) ? { type: 'reveal', line: raw.line } : undefined;
    case 'cancel':
      return { type: 'cancel' };
    default: {
      if (!index(raw.set, sets.length)) return undefined;
      const action = parseGridAction(raw, sets[raw.set], gen);
      return action ? { ...action, set: raw.set } : undefined;
    }
  }
}

/** The value at (row, col) of `rows` (a missing cell of a short row is undefined, i.e. copies as NULL). */
export function cellAt(rows: readonly (readonly unknown[])[], row: number, col: number): unknown {
  return rows[row]?.[col];
}
