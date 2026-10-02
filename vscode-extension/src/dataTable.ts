// read_data payload parsing and the row-count label. No 'vscode' import (unit-testable).

import { pick } from './client/parse';

/** The parts of a read_data result the data view needs. */
export interface ReadDataResult {
  rows: Record<string, unknown>[];
  /** True when the server cut the result at `maxRows` (DbOperationResult.Truncated). */
  truncated: boolean;
  maxRows?: number;
}

const isRow = (r: unknown): r is Record<string, unknown> => !!r && typeof r === 'object' && !Array.isArray(r);

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
