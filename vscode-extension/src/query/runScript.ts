// Building run_script requests and reading their results. No 'vscode' import (unit-testable).
import { errorText, pick } from '../client/parse';

export const DEFAULT_MAX_ROWS = 1000;
export const MAX_MAX_ROWS = 10_000;

export interface RunScriptColumn { name: string; type: string }

export interface RunScriptResultSet {
  /** 1-based batch number the set came from. */
  batch: number;
  columns: RunScriptColumn[];
  /** Kept rows, each in column order (at most maxRows). */
  rows: unknown[][];
  /** Rows the query produced, including those not kept. */
  rowCount: number;
  truncated: boolean;
}

export type RunScriptMessageKind = 'info' | 'rows' | 'error' | 'warning';

export interface RunScriptMessage {
  kind: RunScriptMessageKind;
  text: string;
  /** 1-based line within the submitted script, or null when unknown. */
  line: number | null;
}

export interface RunScriptResult {
  resultSets: RunScriptResultSet[];
  messages: RunScriptMessage[];
  hadErrors: boolean;
  batches: number;
  elapsedMs: number;
}

export interface RunRequest {
  script: string;
  /** 0-based editor line where the script starts. */
  lineOffset: number;
}

/** Runs the selection when it holds more than whitespace, otherwise the whole text. */
export function buildRunRequest(fullText: string, selection: { text: string; startLine: number } | undefined): RunRequest {
  if (selection && selection.text.trim().length > 0) return { script: selection.text, lineOffset: selection.startLine };
  return { script: fullText, lineOffset: 0 };
}

/** The 0-based editor line of a 1-based script line, or undefined when the message has no usable line. */
export function editorLine(messageLine: number | null, lineOffset: number): number | undefined {
  if (messageLine === null || !Number.isInteger(messageLine) || messageLine < 1) return undefined;
  return lineOffset + messageLine - 1;
}

/** The `msSqlMcp.query.maxRows` setting as the server accepts it: an integer in 1..10000, default 1000. */
export function clampMaxRows(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return DEFAULT_MAX_ROWS;
  return Math.min(MAX_MAX_ROWS, Math.max(1, Math.floor(value)));
}

const KINDS: readonly RunScriptMessageKind[] = ['info', 'rows', 'error', 'warning'];
const asArray = (value: unknown): unknown[] => (Array.isArray(value) ? value : []);
const isObject = (value: unknown): value is object => typeof value === 'object' && value !== null;
const num = (value: unknown, fallback: number): number => (typeof value === 'number' && Number.isFinite(value) ? value : fallback);
const str = (value: unknown): string => (typeof value === 'string' ? value : value === null || value === undefined ? '' : String(value));

/**
 * Validates and normalises a run_script payload (`{success, data: {...}}`, any key casing): missing arrays become
 * empty, odd entries are dropped or defaulted. Throws Error(server error text) on `success: false`.
 */
export function parseRunScriptResult(payload: unknown): RunScriptResult {
  if (!isObject(payload)) throw new Error('Unexpected run_script result.');
  if (pick(payload, 'success') === false) throw new Error(errorText(payload) ?? 'run_script failed.');
  const dataField = pick(payload, 'data');
  const data = dataField === undefined ? payload : dataField;
  if (!isObject(data)) throw new Error('Unexpected run_script result.');

  const resultSets = asArray(pick(data, 'resultSets')).filter(isObject).map((set, index): RunScriptResultSet => {
    const rows = asArray(pick(set, 'rows')).map(row => (Array.isArray(row) ? row : []));
    return {
      batch: num(pick(set, 'batch'), index + 1),
      columns: asArray(pick(set, 'columns')).map(c => ({ name: str(pick(c, 'name')), type: str(pick(c, 'type')) })),
      rows,
      rowCount: num(pick(set, 'rowCount'), rows.length),
      truncated: pick(set, 'truncated') === true,
    };
  });
  const messages = asArray(pick(data, 'messages')).filter(isObject).map((m): RunScriptMessage => {
    const kind = pick(m, 'kind');
    const line = pick(m, 'line');
    return {
      kind: KINDS.includes(kind as RunScriptMessageKind) ? (kind as RunScriptMessageKind) : 'info',
      text: str(pick(m, 'text')),
      line: typeof line === 'number' && Number.isInteger(line) && line >= 1 ? line : null,
    };
  });
  const hadErrors = pick(data, 'hadErrors');
  return {
    resultSets,
    messages,
    hadErrors: typeof hadErrors === 'boolean' ? hadErrors : messages.some(m => m.kind === 'error'),
    batches: num(pick(data, 'batches'), 0),
    elapsedMs: num(pick(data, 'elapsedMs'), 0),
  };
}
