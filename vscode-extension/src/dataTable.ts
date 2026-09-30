// read_data payload -> renderable grid. No 'vscode' import (unit-testable).

import { pick } from './client/parse';

export interface DataTable {
  columns: string[];
  rows: Record<string, unknown>[];
  /** True when the server likely had more rows than the requested limit. */
  truncated: boolean;
  limit: number;
}

/** Parse a read_data payload (Columns[] + Data[]) into a renderable table. */
export function parseDataTable(payload: unknown, limit: number): DataTable {
  const columnDefs = pick(payload, 'columns');
  let columns = Array.isArray(columnDefs)
    ? columnDefs.map((c) => String(pick(c, 'name') ?? '')).filter((n) => n.length > 0)
    : [];

  const data = pick(payload, 'data');
  const rows = (Array.isArray(data) ? data : []).map((r) =>
    r && typeof r === 'object' ? (r as Record<string, unknown>) : {},
  );

  if (columns.length === 0 && rows.length > 0) {
    columns = Object.keys(rows[0]);
  }
  return { columns, rows, truncated: rows.length >= limit, limit };
}
