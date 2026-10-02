// Text the grid puts on the clipboard or in an exported file: TSV (copy row / copy selection), JSON (copy row) and
// CSV (Export). No 'vscode' import (unit-testable). Values come from the extension's own copy of the result; the
// webview only chooses rows and columns by index. Nothing here logs or sends anything.
import { copyText } from './gridModel';

/** A column as the builders need it (the type decides formula neutralization in CSV). */
export interface ExportColumn { name: string; type?: string }

/** Text types whose values a spreadsheet could read as a formula. */
const TEXT_TYPES = new Set(['char', 'varchar', 'nchar', 'nvarchar', 'text', 'ntext', 'sysname', 'xml', 'sql_variant']);

export function isTextColumn(type: string | undefined): boolean {
  return TEXT_TYPES.has((type ?? '').trim().toLowerCase());
}

/** Values starting with one of these could run as a spreadsheet formula (CSV injection, OWASP). */
const FORMULA_START = /^[=+\-@\t\r]/;

/** The UTF-8 byte order mark, so Excel opens the CSV as UTF-8 (Hebrew and other non-Latin text). */
export const UTF8_BOM = String.fromCharCode(0xfeff);

const NO_NAME = '(No column name)';
const headerName = (c: ExportColumn | undefined): string => (c && c.name ? c.name : NO_NAME);

/** The values of `rows` (by index) for `cols` (by index, in that order). A missing cell is undefined (NULL). */
export function project(rows: readonly (readonly unknown[])[], rowIdx: readonly number[], colIdx: readonly number[]): unknown[][] {
  return rowIdx.map(r => colIdx.map(c => rows[r]?.[c]));
}

/** One TSV field: the copy text with tabs and line breaks turned into spaces. */
export function tsvField(value: unknown): string {
  return copyText(value).replace(/\r\n|[\t\r\n]/g, ' ');
}

/** TSV lines (CRLF-separated, no trailing line break), with a header row of column names when `header` is given. */
export function buildTsv(header: readonly ExportColumn[] | undefined, rows: readonly (readonly unknown[])[]): string {
  const lines = rows.map(r => r.map(tsvField).join('\t'));
  if (header) lines.unshift(header.map(c => headerName(c).replace(/\r\n|[\t\r\n]/g, ' ')).join('\t'));
  return lines.join('\r\n');
}

/** One RFC 4180 field: quoted (with doubled quotes) when it holds a comma, quote, CR or LF. NULL is empty. */
export function csvField(value: unknown): string {
  const text = copyText(value);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/**
 * One CSV field of a text column with formula neutralization: a value starting with = + - @ tab or CR is written as
 * ' + value and always quoted, so a spreadsheet shows it as text instead of running it. Other values as csvField.
 */
export function neutralizedCsvField(value: unknown): string {
  const text = copyText(value);
  if (value === null || value === undefined || !FORMULA_START.test(text)) return csvField(value);
  return `"'${text.replace(/"/g, '""')}"`;
}

/**
 * A CSV file: BOM, a header row, then one record per row, every line ending in CRLF (RFC 4180). With `neutralize`
 * (the default), text-typed columns get formula neutralization; numeric, date and binary columns are unchanged.
 */
export function buildCsv(columns: readonly ExportColumn[], rows: readonly (readonly unknown[])[], neutralize = true): string {
  const field = columns.map(c => (neutralize && isTextColumn(c.type) ? neutralizedCsvField : csvField));
  const lines = [columns.map(c => csvField(headerName(c))), ...rows.map(r => r.map((v, i) => (field[i] ?? csvField)(v)))].map(f => f.join(','));
  return `${UTF8_BOM}${lines.join('\r\n')}\r\n`;
}

/**
 * One row as a JSON object (2-space indent): column names are the keys (an empty name becomes "(No column name)",
 * a repeated one gets " (2)", " (3)", ...), NULL is null, and values keep their JSON type (exact decimals stay text).
 */
export function buildJsonRow(columns: readonly ExportColumn[], values: readonly unknown[]): string {
  const obj: Record<string, unknown> = {};
  columns.forEach((c, i) => {
    const base = headerName(c);
    let key = base;
    for (let n = 2; Object.prototype.hasOwnProperty.call(obj, key); n++) key = `${base} (${n})`;
    const v = values[i];
    obj[key] = v === undefined ? null : v;
  });
  return JSON.stringify(obj, null, 2);
}

/** The sentence the export confirmation adds while formula neutralization is on. */
export const NEUTRALIZE_NOTE = "Text values that start with = + - @ are prefixed with ' so Excel does not run them as formulas.";

/** The confirmation every Export CSV asks first (modal). */
export function exportConfirmText(rows: number, neutralize = true): string {
  return `Export ${rows.toLocaleString('en-US')} ${rows === 1 ? 'row' : 'rows'} to a CSV file? The data may contain personal information; keep the file inside the company.`
    + (neutralize ? ` ${NEUTRALIZE_NOTE}` : '');
}

export function exportedMessage(rows: number, file: string): string {
  return `Exported ${rows.toLocaleString('en-US')} ${rows === 1 ? 'row' : 'rows'} to ${file}`;
}

/**
 * Default export file name: `<base>_<yyyyMMdd_HHmm>.csv` in local time. Characters Windows does not allow in file
 * names (and control characters) become '_'; an empty base becomes "results".
 */
export function exportFileName(base: string, now: Date): string {
  // eslint-disable-next-line no-control-regex
  const safe = base.replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').replace(/[. ]+$/, '').trim() || 'results';
  const p = (n: number) => String(n).padStart(2, '0');
  const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}_${p(now.getHours())}${p(now.getMinutes())}`;
  return `${safe}_${stamp}.csv`;
}
