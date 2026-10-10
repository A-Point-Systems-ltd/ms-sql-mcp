// Per-group name filters of the object tree: one term per (connection, category), e.g. Tables of 'dev' filtered by
// "Order" while Views of 'dev' and Tables of 'prod' are not. Pure (no vscode import) so it is unit-testable; the
// tree keeps it in globalState so filters survive a restart.
import { normalizeFilter } from './filter';

/** What is stored: connection (lower-case) -> category id -> term. */
export type CategoryFilterState = Record<string, Record<string, string>>;

export class CategoryFilters {
  private readonly terms = new Map<string, Map<string, string>>();

  constructor(state?: unknown) {
    if (!state || typeof state !== 'object') return;
    for (const [connection, categories] of Object.entries(state as Record<string, unknown>)) {
      if (!categories || typeof categories !== 'object') continue;
      for (const [category, term] of Object.entries(categories as Record<string, unknown>)) {
        if (typeof term === 'string') this.set(connection, category, term);
      }
    }
  }

  /** The term of a group ('' when none). Connection names are case-insensitive. */
  get(connection: string, category: string): string {
    return this.terms.get(connection.toLowerCase())?.get(category) ?? '';
  }

  /** Sets (or, with an empty term, clears) a group's filter; true when it changed. */
  set(connection: string, category: string, raw: string): boolean {
    const term = normalizeFilter(raw);
    const key = connection.toLowerCase();
    const groups = this.terms.get(key);
    if ((groups?.get(category) ?? '') === term) return false;
    if (term) {
      if (groups) groups.set(category, term);
      else this.terms.set(key, new Map([[category, term]]));
    } else if (groups) {
      groups.delete(category);
      if (!groups.size) this.terms.delete(key);
    }
    return true;
  }

  /** Drops the filters of connections that no longer exist; true when any was dropped. */
  retain(connections: readonly string[]): boolean {
    const keep = new Set(connections.map(c => c.toLowerCase()));
    let dropped = false;
    for (const key of [...this.terms.keys()]) {
      if (!keep.has(key)) {
        this.terms.delete(key);
        dropped = true;
      }
    }

    return dropped;
  }

  /** Clears every filter; true when there was one. */
  clearAll(): boolean {
    const had = this.terms.size > 0;
    this.terms.clear();
    return had;
  }

  /** How many groups are filtered. */
  get count(): number {
    let n = 0;
    for (const groups of this.terms.values()) n += groups.size;
    return n;
  }

  toState(): CategoryFilterState {
    const out: CategoryFilterState = {};
    for (const [connection, groups] of this.terms) out[connection] = Object.fromEntries(groups);
    return out;
  }
}
