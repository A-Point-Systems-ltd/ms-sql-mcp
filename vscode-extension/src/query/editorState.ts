// What a SQL editor bound to a connection may do, and how its status bar item reads.
// No 'vscode' import — unit-testable with plain Node.
import type { ConnectionProfile } from '../connections/profile';
import type { QueryAssociation } from './queryDocuments';

export interface EditorRunState {
  /** The document is associated with a connection (even a removed or closed one). */
  connected: boolean;
  /** Run (F5) may execute the document now. */
  canRun: boolean;
  /** An object document on a read-only connection: Run is shown disabled. */
  blockedReadOnly: boolean;
  /** Status bar text (codicons allowed). */
  statusText: string;
  /** Status bar tooltip. */
  tooltip: string;
  /** Why `canRun` is false (for a warning on F5); undefined when it can run or is not connected. */
  reason?: string;
}

const CLICK = 'Click to change.';

/** The profile named `name` (case-insensitive, like the server), if any. */
export function findProfile(profiles: readonly ConnectionProfile[], name: string | undefined): ConnectionProfile | undefined {
  if (name === undefined) return undefined;
  const wanted = name.toLowerCase();
  return profiles.find(p => p.name.toLowerCase() === wanted);
}

/** Run state of an editor from its association and the matching profile (undefined when it was removed). */
export function editorRunState(assoc: QueryAssociation | undefined, profile: ConnectionProfile | undefined): EditorRunState {
  if (!assoc) {
    return {
      connected: false, canRun: false, blockedReadOnly: false,
      statusText: '$(database) Connect',
      tooltip: 'APoint-ms-sql: no connection. Click to choose one.',
    };
  }
  if (!profile) {
    const reason = `'${assoc.connection}': the connection was removed. Choose another connection.`;
    return {
      connected: true, canRun: false, blockedReadOnly: false,
      statusText: `$(database) ${assoc.connection}`,
      tooltip: `APoint-ms-sql: ${assoc.connection} - connection was removed. ${CLICK}`,
      reason,
    };
  }
  const target = profile.auth === 'raw' ? 'connection string' : `${profile.server}\\${profile.database}`;
  const mode = profile.readOnly ? 'read-only' : 'read-write';
  const statusText = `$(database) ${profile.name}${profile.readOnly ? ' $(lock)' : ''}`;
  const head = `APoint-ms-sql: ${profile.name} - ${target} - ${mode}`;
  if (!profile.open) {
    return {
      connected: true, canRun: false, blockedReadOnly: false, statusText,
      tooltip: `${head}, closed: open the connection first. ${CLICK}`,
      reason: `The connection '${profile.name}' is closed: open the connection first.`,
    };
  }
  if (assoc.kind === 'object' && profile.readOnly) {
    const reason = `'${profile.name}' is a read-only connection: object changes can only be applied on a read-write connection.`;
    return {
      connected: true, canRun: false, blockedReadOnly: true, statusText,
      tooltip: `${head}. Object changes can only be applied on a read-write connection. ${CLICK}`,
      reason,
    };
  }
  return { connected: true, canRun: true, blockedReadOnly: false, statusText, tooltip: `${head}. ${CLICK}` };
}

/**
 * Per-document context-key values for the editor title buttons (`resource in msSqlMcp.runnableDocs` ...): the keys of
 * every bound document that can run now, and of those shown with the disabled read-only Run button.
 */
export function runContextDocs(
  entries: readonly (readonly [string, QueryAssociation])[], profiles: readonly ConnectionProfile[],
): { runnable: string[]; blocked: string[] } {
  const runnable: string[] = [];
  const blocked: string[] = [];
  for (const [key, assoc] of entries) {
    const state = editorRunState(assoc, findProfile(profiles, assoc.connection));
    if (state.canRun) runnable.push(key);
    else if (state.blockedReadOnly) blocked.push(key);
  }
  return { runnable, blocked };
}
