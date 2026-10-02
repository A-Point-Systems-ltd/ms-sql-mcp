// The kept query documents (`<globalStorage>/sqldocs/query-index.json`) and the deferred prune decisions.
// No 'vscode' import: unit-testable with plain Node; queryIndexFile.ts reads and writes the file.
import { orphanQueryIds } from './queryDocuments';
import { isValidDocId, queryNumberOf, queryObjectName } from './sqlDocNames';

/** File name of the index, next to the `query/` and `object/` backing folders. */
export const QUERY_INDEX_FILE = 'query-index.json';

/** A closed kept query document is deleted this long after it was closed. */
export const RETENTION_MS = 30 * 24 * 60 * 60 * 1000;

/**
 * One kept query document: its last tab title, its connection, when it was last written or closed (epoch ms), and
 * `closedAt` while no tab shows it. An entry without `closedAt` may be open in some window and is never pruned.
 */
export interface QueryIndexEntry {
  title: string;
  connection: string;
  updatedAt: number;
  closedAt?: number;
}

/** Query document id → entry. */
export type QueryIndex = Record<string, QueryIndexEntry>;

/** The result of reading a backing file: its text, `missing` (ENOENT), or `unknown` (any other error: touch nothing). */
export type BackingRead = { kind: 'text'; text: string } | { kind: 'missing' } | { kind: 'unknown' };

/** Non-empty index text that is not an index. The message never contains the text. */
export class QueryIndexFormatError extends Error {
  constructor() {
    super('The query index file is not a valid index.');
    this.name = 'QueryIndexFormatError';
  }
}

const isTime = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);

function asEntry(v: unknown): QueryIndexEntry | undefined {
  const e = v as Partial<QueryIndexEntry> | null | undefined;
  if (!e || typeof e !== 'object' || typeof e.title !== 'string' || typeof e.connection !== 'string' || !isTime(e.updatedAt)) {
    return undefined;
  }
  return { title: e.title, connection: e.connection, updatedAt: e.updatedAt, ...(isTime(e.closedAt) ? { closedAt: e.closedAt } : {}) };
}

/**
 * The index in `text` (the file's content). No text (or whitespace) is an empty index; non-empty text that is not a
 * JSON object throws {@link QueryIndexFormatError}, so a damaged file is never overwritten. Malformed entries and ids are
 * left out.
 */
export function parseQueryIndex(text: string | undefined): QueryIndex {
  if (text === undefined || text.trim() === '') return {};
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    throw new QueryIndexFormatError();
  }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new QueryIndexFormatError();
  const index: QueryIndex = {};
  for (const [id, value] of Object.entries(raw)) {
    const entry = isValidDocId(id) ? asEntry(value) : undefined;
    if (entry) index[id] = entry;
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

/** The entry after a write or a reopen: no `closedAt` (it is open). The connection falls back to the old entry's. */
export function writtenEntry(prev: QueryIndexEntry | undefined, title: string, connection: string | undefined, now: number): QueryIndexEntry {
  return { title, connection: connection ?? prev?.connection ?? '', updatedAt: now };
}

/** The entry of a document closed with text: `closedAt` = `updatedAt` = now. */
export function closedEntry(title: string, connection: string | undefined, now: number, prev?: QueryIndexEntry): QueryIndexEntry {
  return { title, connection: connection ?? prev?.connection ?? '', updatedAt: now, closedAt: now };
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
 * `keep` (record it as closed) otherwise, `forget` when the file is gone, `leave` when it could not be read (a read
 * error is not "missing": nothing is changed). Text is never lost.
 */
export function closedQueryAction(read: BackingRead): 'delete' | 'keep' | 'forget' | 'leave' {
  if (read.kind === 'missing') return 'forget';
  if (read.kind === 'unknown') return 'leave';
  return isBlank(read.text) ? 'delete' : 'keep';
}

/**
 * The deferred activation prune of query documents. `remove` (delete the backing file, then drop the id from the index
 * and the owned list): entries closed more than {@link RETENTION_MS} ago (an entry without `closedAt` never), and this
 * workspace's own blank documents; never one that is open (or created this session: the caller adds those to `open`).
 * `forget` (drop from the index and the owned list only): owned ids that are malformed or whose file is missing.
 * `blank` / `missing` describe the owned ids that are not open and could be read; an unreadable one is in neither set
 * and is left alone.
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
    if (e.closedAt !== undefined && !input.open.has(id) && !gone.has(id) && input.now - e.closedAt > RETENTION_MS) remove.add(id);
  }
  for (const id of orphanQueryIds(input.owned, input.open)) {
    if (!gone.has(id) && input.blank.has(id)) remove.add(id);
  }
  return { remove: [...remove], forget };
}

/**
 * Index entries for non-blank backing files that have none (the index was lost or damaged and rewritten): the open
 * tab's title and no `closedAt` when this window shows it, else `Recovered query <id>` with `closedAt` = `now`, so a
 * recovery always starts a fresh 30-day clock (`updatedAt` is the file's mtime, for the list). Unreadable and missing
 * files are skipped.
 */
export function recoveredEntries(
  index: QueryIndex, files: readonly { id: string; mtimeMs: number; read: BackingRead }[], openTitles: ReadonlyMap<string, string>,
  now: number,
): QueryIndex {
  const recovered: QueryIndex = {};
  for (const f of files) {
    if (index[f.id] || !isValidDocId(f.id) || f.read.kind !== 'text' || isBlank(f.read.text)) continue;
    const title = openTitles.get(f.id);
    recovered[f.id] = title !== undefined
      ? { title, connection: '', updatedAt: f.mtimeMs }
      : { title: `Recovered query ${f.id}`, connection: '', updatedAt: f.mtimeMs, closedAt: now };
  }
  return recovered;
}

/**
 * `index` with `closedAt` cleared for the ids this window has open (also background tabs without a loaded document),
 * so the prune never removes them; the same object when there is nothing to clear (no write).
 */
export function withOpenCleared(index: QueryIndex, open: ReadonlySet<string>): QueryIndex {
  const ids = Object.keys(index).filter(id => open.has(id) && index[id].closedAt !== undefined);
  if (!ids.length) return index;
  const next = { ...index };
  for (const id of ids) {
    const { closedAt: _closed, ...rest } = index[id];
    next[id] = rest;
  }
  return next;
}

/** `index` plus the recovered entries whose id it does not have yet (an entry written meanwhile wins). */
export function mergeRecovered(index: QueryIndex, recovered: QueryIndex): QueryIndex {
  const merged = { ...index };
  for (const [id, e] of Object.entries(recovered)) if (!merged[id]) merged[id] = e;
  return merged;
}

/** The object name of a reopened query document: its old `Query N` unless an open tab has N, else the next number. */
export function reopenObjectName(storedTitle: string, taken: ReadonlySet<number>, next: () => number): string {
  const n = queryNumberOf(storedTitle);
  return queryObjectName(n !== undefined && !taken.has(n) ? n : next());
}
