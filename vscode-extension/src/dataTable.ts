// read_data payload -> renderable grid. No 'vscode' import (unit-testable).

import { pick } from './client/parse';

/** A grid: column names plus one value array per row, in column order. */
export interface GridTable {
  columns: string[];
  data: unknown[][];
}

/** The parts of a read_data result the data view needs. */
export interface ReadDataResult {
  rows: Record<string, unknown>[];
  /** True when the server cut the result at `maxRows` (DbOperationResult.Truncated). */
  truncated: boolean;
  maxRows?: number;
}

const isRow = (r: unknown): r is Record<string, unknown> => !!r && typeof r === 'object' && !Array.isArray(r);

/**
 * Row objects (read_data `data`) -> columns + data. Column order is the first row's key order;
 * keys that only appear in later rows are appended, and missing values become null.
 */
export function rowsToTable(rows: Record<string, unknown>[]): GridTable {
  const columns: string[] = [];
  const seen = new Set<string>();
  for (const row of rows) {
    for (const key of Object.keys(row)) {
      if (!seen.has(key)) {
        seen.add(key);
        columns.push(key);
      }
    }
  }
  return { columns, data: rows.map(row => columns.map(c => (Object.prototype.hasOwnProperty.call(row, c) ? row[c] : null))) };
}

/** Full read_data payload ({success, data, truncated?, maxRows?}, any casing) -> rows + truncation info. */
export function parseReadData(payload: unknown): ReadDataResult {
  const data = pick(payload, 'data');
  const rows = (Array.isArray(data) ? data : []).filter(isRow);
  const maxRows = pick(payload, 'maxRows');
  return { rows, truncated: pick(payload, 'truncated') === true, maxRows: typeof maxRows === 'number' ? maxRows : undefined };
}

export function rowCountLabel(count: number, truncated: boolean): string {
  const n = count.toLocaleString('en-US');
  return truncated ? `first ${n} rows (truncated)` : `${n} row${count === 1 ? '' : 's'}`;
}
