// The kept query documents (`<globalStorage>/sqldocs/query-index.json`) and the deferred prune decisions.
// No 'vscode' import: unit-testable with plain Node; sqlDocFs.ts reads and writes the file.
import { orphanQueryIds } from './queryDocuments';
import { isValidDocId, queryNumberOf, queryObjectName } from './sqlDocNames';

/** File name of the index, next to the `query/` and `object/` backing folders. */
export const QUERY_INDEX_FILE = 'query-index.json';

/** Kept query documents and unused object documents are deleted after this long (by last write / close). */
export const RETENTION_MS = 30 * 24 * 60 * 60 * 1000;

/** One kept query document: its last tab title, its connection, and when it was last written or closed (epoch ms). */
export interface QueryIndexEntry {
  title: string;
  connection: string;
  updatedAt: number;
}

/** Query document id → entry. */
export type QueryIndex = Record<string, QueryIndexEntry>;

function isEntry(v: unknown): v is QueryIndexEntry {
  const e = v as Partial<QueryIndexEntry> | null | undefined;
  return !!e && typeof e === 'object' && typeof e.title === 'string' && typeof e.connection === 'string'
    && typeof e.updatedAt === 'number' && Number.isFinite(e.updatedAt);
}

/** The index in `text` (the file's content); malformed entries and ids are left out, and unreadable text is empty. */
export function parseQueryIndex(text: string | undefined): QueryIndex {
  let raw: unknown;
  try {
    raw = text ? JSON.parse(text) : undefined;
  } catch {
    return {};
  }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const index: QueryIndex = {};
  for (const [id, value] of Object.entries(raw)) {
    if (isValidDocId(id) && isEntry(value)) index[id] = { title: value.title, connection: value.connection, updatedAt: value.updatedAt };
  }
  return index;
}

export function serializeQueryIndex(index: QueryIndex): string {
  return JSON.stringify(index, undefined, 2);
}

export function withEntry(index: QueryIndex, id: string, entry: QueryIndexEntry): QueryIndex {
  return { ...index, [id]: { ...entry } };
}

export function withoutIds(index: QueryIndex, ids: readonly string[]): QueryIndex {
  const drop = new Set(ids);
  return Object.fromEntries(Object.entries(index).filter(([id]) => !drop.has(id)));
}

/** The entries, newest first. */
export function recentQueries(index: QueryIndex): (QueryIndexEntry & { id: string })[] {
  return Object.entries(index).map(([id, e]) => ({ id, ...e })).sort((a, b) => b.updatedAt - a.updatedAt);
}

const plural = (n: number, unit: string) => `${n} ${unit}${n === 1 ? '' : 's'} ago`;

/** `just now`, `5 minutes ago`, `1 hour ago`, `3 days ago`. */
export function relativeTime(then: number, now: number): string {
  const s = Math.floor((now - then) / 1000);
  if (s < 60) return 'just now';
  const m = Math.floor(s / 60);
  if (m < 60) return plural(m, 'minute');
  const h = Math.floor(m / 60);
  if (h < 24) return plural(h, 'hour');
  return plural(Math.floor(h / 24), 'day');
}

/** Empty or whitespace-only. */
export function isBlank(text: string): boolean {
  return text.trim() === '';
}

/**
 * What happens to a query document's backing file when its tab is closed (clean): `delete` when the text is blank,
 * `keep` (and record it in the index) otherwise, `forget` when the backing file is already gone. Text is never lost.
 */
export function closedQueryAction(backingText: string | undefined): 'delete' | 'keep' | 'forget' {
  if (backingText === undefined) return 'forget';
  return isBlank(backingText) ? 'delete' : 'keep';
}

/**
 * The deferred activation prune of query documents. `remove` (delete the backing file, then drop the id from the index
 * and the owned list): kept documents whose last write / close is older than {@link RETENTION_MS}, and this workspace's
 * own blank documents; never one that is open (or created this session: the caller adds those to `open`). `forget`
 * (drop from the index and the owned list only): owned ids that are malformed or whose backing file is gone.
 * `blank` / `missing` describe the owned ids that are not open.
 */
export function queryPrunePlan(input: {
  index: QueryIndex;
  owned: readonly string[];
  open: ReadonlySet<string>;
  blank: ReadonlySet<string>;
  missing: ReadonlySet<string>;
  now: number;
}): { remove: string[]; forget: string[] } {
  const forget = input.owned.filter(id => !isValidDocId(id) || (!input.open.has(id) && input.missing.has(id)));
  const gone = new Set(forget);
  const remove = new Set<string>();
  for (const [id, e] of Object.entries(input.index)) {
    if (!input.open.has(id) && !gone.has(id) && input.now - e.updatedAt > RETENTION_MS) remove.add(id);
  }
  for (const id of orphanQueryIds(input.owned, input.open)) {
    if (!gone.has(id) && input.blank.has(id)) remove.add(id);
  }
  return { remove: [...remove], forget };
}

/** Object backing files to delete: not open, not bound in this workspace, and not written for {@link RETENTION_MS}. */
export function objectPrunePlan(
  files: readonly { id: string; mtimeMs: number }[], open: ReadonlySet<string>, bound: ReadonlySet<string>, now: number,
): string[] {
  return files.filter(f => isValidDocId(f.id) && !open.has(f.id) && !bound.has(f.id) && now - f.mtimeMs > RETENTION_MS).map(f => f.id);
}

/** The object name of a reopened query document: its old `Query N` unless an open tab has N, else the next number. */
export function reopenObjectName(storedTitle: string, taken: ReadonlySet<number>, next: () => number): string {
  const n = queryNumberOf(storedTitle);
  return queryObjectName(n !== undefined && !taken.has(n) ? n : next());
}
