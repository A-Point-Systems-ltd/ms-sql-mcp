// When an open `mssql-sql:` document's tab title must change, and to what. No 'vscode' import: unit-testable with plain
// Node; sqlDocLifecycle.ts does the save / close / reopen.
import type { ConnectionProfile } from '../connections/profile';
import type { QueryAssociation } from './queryDocuments';
import { SqlDocAddress, docTitle, objectDisplayName, profileTarget, queryNumberOf, queryObjectName } from './sqlDocNames';

/** The info message for a dirty document whose title is out of date: it is renamed after the next save. */
export const TITLE_AFTER_SAVE_MESSAGE = 'The tab title updates after you save.';

/**
 * What to do with one open document's title:
 * - `none`: the title is right, or there is nothing to compute it from (no binding, a removed profile, an object
 *   document rebound with Change Connection, a title that is not `Query N - ...`);
 * - `defer`: the title is out of date, but the document has unsaved edits (or a run): only the status bar changes now,
 *   and the rename happens after the next save;
 * - `retitle`: save, close the tab and reopen the same id under `title`, with the binding kept.
 */
export type TitleAction = { kind: 'none' } | { kind: 'defer'; title: string } | { kind: 'retitle'; title: string };

export interface TitleInput {
  address: SqlDocAddress;
  assoc: QueryAssociation | undefined;
  /** The profile of `assoc.connection`; undefined when it was removed. */
  profile: ConnectionProfile | undefined;
  isDirty: boolean;
  /** A query is running in the document (closing its tab would cancel it). */
  running?: boolean;
}

/**
 * The title an open document should have now. Query documents follow their binding (Change Connection and profile
 * edits): `Query N - <server> - <database>` of the bound profile. Object documents follow profile edits of their own
 * connection: `<schema.name> - <server> - <database>`; one rebound with Change Connection keeps its title (the
 * wrong-target guard asks before it runs elsewhere).
 */
export function expectedTitle(input: Pick<TitleInput, 'address' | 'assoc' | 'profile'>): string | undefined {
  const { address, assoc, profile } = input;
  if (!assoc || !profile || assoc.kind !== address.kind) return undefined;
  if (address.kind === 'query') {
    const n = queryNumberOf(address.title);
    return n === undefined ? undefined : docTitle(queryObjectName(n), profileTarget(profile));
  }
  if (assoc.rebound || !assoc.object) return undefined;
  return docTitle(objectDisplayName(assoc.object), profileTarget(profile, assoc.connection));
}

export function titleAction(input: TitleInput): TitleAction {
  const title = expectedTitle(input);
  if (title === undefined || title === input.address.title) return { kind: 'none' };
  return input.isDirty || input.running ? { kind: 'defer', title } : { kind: 'retitle', title };
}

/**
 * The results state a document reopened under a new title takes over from its old uri (so its Results / Messages stay
 * visible after a retitle right after a run): the old one, unless the new uri already has its own (then undefined:
 * nothing is copied).
 */
export function resultsToCarry<T>(previous: T | undefined, current: T | undefined): T | undefined {
  return current === undefined ? previous : undefined;
}
