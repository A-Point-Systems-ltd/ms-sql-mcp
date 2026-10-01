// DDL history (dbo.DDL_AuditLog, filled by the DDL_Audit database trigger): result parsing, the install decision,
// quick pick items, diff titles and `mssql-history:` URIs. No 'vscode' import: unit-testable with plain Node.
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import type { ObjectRef } from '../explorer/catalog';
import { parseUriQuery, qEnc, qualified, sqlString } from '../explorer/sqlText';
import { findProfile } from '../query/editorState';
import type { QueryAssociation } from '../query/queryDocuments';
import { profileTarget, titlePart } from '../query/sqlDocNames';

export const HISTORY_SCHEME = 'mssql-history';

/** The server tool (runner process only) behind every history call. */
export const HISTORY_TOOL = 'ddl_history';

/** Entries requested per list (the server clamps to 1..500). */
export const LIST_TOP = 100;

/** Script types whose DDL events the trigger records under their own name, so Show DDL History is offered. */
export const HISTORY_TYPES = ['Table', 'View', 'StoredProcedure', 'TableFunction', 'ScalarFunction', 'TableTrigger', 'DatabaseTrigger', 'Type'] as const;

/** Modules: they have a current T-SQL definition to compare the latest recorded version with. */
export const MODULE_TYPES = ['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction', 'TableTrigger', 'DatabaseTrigger'] as const;

export function supportsHistory(scriptType: string | undefined): boolean {
  return !!scriptType && (HISTORY_TYPES as readonly string[]).includes(scriptType);
}

export function isModuleType(scriptType: string | undefined): boolean {
  return !!scriptType && (MODULE_TYPES as readonly string[]).includes(scriptType);
}

// ---- Messages (user-facing text) ----

export const OPEN_FIRST_MESSAGE = 'Open the connection to set up DDL history.';
export const NOT_SET_UP_MESSAGE = 'DDL history was not set up; you can enable it later from Edit Connection.';
export const CREATE_BUTTON = 'Create';
export const SET_UP_BUTTON = 'Set up…';
export const COMPARE_CURRENT_LABEL = '$(diff) Compare latest recorded version with the current definition';
export const NO_EARLIER_TEXT = '-- No earlier version recorded';
export const NOT_LOADED_TEXT = `-- No earlier version loaded (only the newest ${LIST_TOP} entries are listed)`;
export const CURRENT_UNAVAILABLE_TEXT = '-- The current definition is not available (the object no longer exists, or it is a CLR or encrypted module).';

export const disabledWarning = (where: string): string => `DDL_Audit exists on ${where} but is disabled; changes are not recorded.`;
export const readOnlyWarning = (where: string): string =>
  `DDL history needs dbo.DDL_AuditLog and the DDL_Audit database trigger on ${where}. This connection is read-only, so they were not created — ask a DBA or set it up from a read-write connection.`;
export const setUpMessage = (where: string): string => `DDL history is set up on ${where}.`;
export const noHistoryMessage = (obj: string, connection: string): string => `No DDL history recorded for ${obj} on '${connection}'.`;

/** `<server>/<database>` of a profile, as in its tab titles (raw connection strings are parsed best-effort). */
export function targetText(profile: ConnectionProfile): string {
  const t = profileTarget(profile, profile.name);
  return `${t.server}/${t.database}`;
}

/** The server's list / get error when dbo.DDL_AuditLog is missing. */
export function isNotInstalledError(message: string): boolean {
  return /DDL history is not installed/i.test(message);
}

// ---- status / install ----

export interface HistoryStatus {
  tableExists: boolean;
  tableCompatible: boolean;
  triggerExists: boolean;
  triggerEnabled: boolean;
  canInstall: boolean;
}

/** `ddl_history status` data; a missing or non-boolean flag reads as false. */
export function parseHistoryStatus(data: unknown): HistoryStatus {
  const flag = (key: string) => pick(data, key) === true;
  return {
    tableExists: flag('tableExists'),
    tableCompatible: flag('tableCompatible'),
    triggerExists: flag('triggerExists'),
    triggerEnabled: flag('triggerEnabled'),
    canInstall: flag('canInstall'),
  };
}

export type InstallDecision = 'none' | 'warnDisabled' | 'warnReadOnly' | 'confirmInstall';

/**
 * What the form does after a save: nothing when the table and an enabled trigger exist; a warning when the trigger
 * exists but is disabled (it is never enabled for the user); when something is missing, a warning on a read-only
 * connection, else a modal confirmation before `install`.
 */
export function installDecision(status: HistoryStatus, readOnly: boolean): InstallDecision {
  if (status.tableExists && status.triggerExists) return status.triggerEnabled ? 'none' : 'warnDisabled';
  return readOnly ? 'warnReadOnly' : 'confirmInstall';
}

/** The modal text: names server/db and lists exactly what will be created. */
export function installPrompt(status: HistoryStatus, where: string): { message: string; detail: string } {
  const parts = [
    ...(status.tableExists ? [] : ['table dbo.DDL_AuditLog']),
    ...(status.triggerExists ? [] : ['database trigger DDL_Audit']),
  ];
  return {
    message: `Create DDL history on ${where}?`,
    detail: `This creates ${parts.join(' and ')}. The trigger records every DDL change in this database.`,
  };
}

/**
 * Whether a save runs the install flow: `ddlHistory` is on in the saved profile and was not on in the form's baseline
 * (undefined for a new connection; the form opened by "Set up…" uses false so its save always runs it).
 */
export function runsHistorySetup(baseline: boolean | undefined, saved: ConnectionProfile): boolean {
  return saved.ddlHistory === true && baseline !== true;
}

// ---- list / get ----

/** One `ddl_history list` row (newest first from the server). */
export interface HistoryEntry {
  id: number;
  /** Server local time, `yyyy-MM-ddTHH:mm:ss.fff`. */
  postTime: string;
  loginName?: string;
  hostName?: string;
  programName?: string;
  eventType?: string;
  objectType?: string;
  schemaName?: string;
  /** Length of the command text. */
  length?: number;
}

const str = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined);
const num = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);

/** `ddl_history list` data; rows without an integer id are dropped. */
export function parseHistoryEntries(data: unknown): HistoryEntry[] {
  if (!Array.isArray(data)) return [];
  return data.flatMap((row): HistoryEntry[] => {
    const id = num(pick(row, 'id'));
    if (id === undefined || !Number.isInteger(id)) return [];
    const e: HistoryEntry = { id, postTime: str(pick(row, 'postTime')) ?? '' };
    for (const key of ['loginName', 'hostName', 'programName', 'eventType', 'objectType', 'schemaName'] as const) {
      const v = str(pick(row, key));
      if (v !== undefined) e[key] = v;
    }
    const length = num(pick(row, 'length'));
    if (length !== undefined) e.length = length;
    return [e];
  });
}

/** `ddl_history get` data: the command text (empty when the server has none). */
export function parseHistoryCommand(data: unknown): { id: number | undefined; postTime: string; commandText: string } {
  return { id: num(pick(data, 'id')), postTime: str(pick(data, 'postTime')) ?? '', commandText: str(pick(data, 'commandText')) ?? '' };
}

/** `dd/MM/yyyy HH:mm:ss` read straight from the server's text (no time zone conversion); other text is shown as is. */
export function formatPostTime(postTime: string): string {
  if (!postTime) return '(no time)';
  const m = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})/.exec(postTime);
  return m ? `${m[3]}/${m[2]}/${m[1]} ${m[4]}:${m[5]}:${m[6]}` : postTime;
}

export type HistoryPickAction = { kind: 'entry'; index: number } | { kind: 'current' };

export interface HistoryPickItem {
  label: string;
  description?: string;
  detail?: string;
  action: HistoryPickAction;
}

/** Quick pick items, newest first; a module's list starts with "Compare … with the current definition". */
export function historyPickItems(entries: readonly HistoryEntry[], isModule: boolean): HistoryPickItem[] {
  if (!entries.length) return [];
  const items = entries.map((e, index): HistoryPickItem => ({
    label: `${formatPostTime(e.postTime)} · ${e.eventType ?? '?'}`,
    description: e.loginName ?? '',
    detail: `${e.hostName ?? '?'} · ${e.programName ?? '?'} · ${e.length ?? '?'} chars`,
    action: { kind: 'entry', index },
  }));
  return isModule ? [{ label: COMPARE_CURRENT_LABEL, action: { kind: 'current' } }, ...items] : items;
}

/** The entry recorded just before `entries[index]` (the list is newest first); undefined for the oldest. */
export function previousEntry(entries: readonly HistoryEntry[], index: number): HistoryEntry | undefined {
  return index >= 0 && index + 1 < entries.length ? entries[index + 1] : undefined;
}

/** `<obj>: <prevTime> ↔ <time> (<LoginName>)`. */
export function entryDiffTitle(obj: string, entries: readonly HistoryEntry[], index: number): string {
  const e = entries[index];
  const prev = previousEntry(entries, index);
  return `${obj}: ${prev ? formatPostTime(prev.postTime) : '(none)'} ↔ ${formatPostTime(e.postTime)} (${e.loginName ?? '?'})`;
}

/** `<obj>: <time> ↔ current`. */
export function currentDiffTitle(obj: string, latest: HistoryEntry): string {
  return `${obj}: ${formatPostTime(latest.postTime)} ↔ current`;
}

/**
 * The read_data query for a module's current definition. Database triggers are not schema-scoped (OBJECT_ID does not
 * find them), so they are read from sys.triggers by name. Every name is an escaped N'...' literal.
 */
export function currentDefinitionSql(ref: Pick<ObjectRef, 'scriptType' | 'schema' | 'name'>): string {
  if (ref.scriptType === 'DatabaseTrigger') {
    return 'SELECT m.definition AS d FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id '
      + `WHERE t.parent_class = 0 AND t.name = ${sqlString(ref.name)}`;
  }
  return `SELECT OBJECT_DEFINITION(OBJECT_ID(${sqlString(qualified(ref.schema, ref.name))})) AS d`;
}

// ---- mssql-history: URIs ----

/** What an `mssql-history:` document shows. `label` is display only (the path); the query is the source of truth. */
export type HistoryDocRef =
  | { kind: 'entry'; connection: string; id: number; label: string }
  | { kind: 'empty'; label: string; more: boolean }
  | { kind: 'current'; connection: string; object: ObjectRef; label: string; nonce: string };

/**
 * `mssql-history:/<label>.sql?v=entry&c=..&i=..`, `?v=empty&m=0|1`, or `?v=current&c=..&t=..&n=..[&s=..]&r=..`.
 * Query values use sqlText's URI-unreserved encoding (as ddlUri). `r` (current) makes each comparison a new document,
 * so the definition is read again instead of a cached copy.
 */
export function historyUri(ref: HistoryDocRef): string {
  let q: string;
  switch (ref.kind) {
    case 'entry': q = `v=entry&c=${qEnc(ref.connection)}&i=${ref.id}`; break;
    case 'empty': q = `v=empty&m=${ref.more ? 1 : 0}`; break;
    case 'current': {
      const o = ref.object;
      q = `v=current&c=${qEnc(ref.connection)}&t=${qEnc(o.scriptType)}&n=${qEnc(o.name)}`
        + `${o.schema !== undefined ? `&s=${qEnc(o.schema)}` : ''}&r=${qEnc(ref.nonce)}`;
      break;
    }
  }
  return `${HISTORY_SCHEME}:/${encodeURIComponent(titlePart(ref.label))}.sql?${q}`;
}

/** The ref of an `mssql-history:` uri string (any of URI.toString(), toString(true)); undefined when malformed. */
export function parseHistoryUri(uri: string): HistoryDocRef | undefined {
  const params = parseUriQuery(uri);
  if (!params) return undefined;
  const label = labelOf(uri);
  const connection = params.get('c') ?? '';
  switch (params.get('v')) {
    case 'entry': {
      const raw = params.get('i') ?? '';
      if (!connection || !/^\d{1,10}$/.test(raw)) return undefined;
      return { kind: 'entry', connection, id: Number(raw), label };
    }
    case 'empty':
      return { kind: 'empty', label, more: params.get('m') === '1' };
    case 'current': {
      const scriptType = params.get('t') ?? '';
      const name = params.get('n') ?? '';
      if (!connection || !scriptType || !name) return undefined;
      const object: ObjectRef = { connection, scriptType, ...(params.has('s') ? { schema: params.get('s')! } : {}), name };
      return { kind: 'current', connection, object, label, nonce: params.get('r') ?? '' };
    }
    default:
      return undefined;
  }
}

function labelOf(uri: string): string {
  const colon = uri.indexOf(':');
  const end = uri.search(/[?#]/);
  const path = uri.slice(colon + 1, end < 0 ? uri.length : end);
  const base = path.slice(path.lastIndexOf('/') + 1).replace(/\.sql$/, '');
  try { return decodeURIComponent(base); } catch { return base; }
}

// ---- editor title key ----

/**
 * The `msSqlMcp.historyDocs` context key: uri keys of open object documents (`mssql-sql:/object`, via their binding)
 * and `mssql-ddl:` documents whose object has a history type and whose connection is open with `ddlHistory`.
 */
export function historyDocKeys(
  objectDocs: readonly (readonly [string, QueryAssociation])[],
  ddlDocs: readonly (readonly [string, ObjectRef])[],
  profiles: readonly ConnectionProfile[],
): string[] {
  const enabled = (connection: string) => {
    const p = findProfile(profiles, connection);
    return !!p && p.open && p.ddlHistory === true;
  };
  const keys: string[] = [];
  for (const [key, assoc] of objectDocs) {
    if (assoc.kind === 'object' && assoc.object && supportsHistory(assoc.object.scriptType) && enabled(assoc.connection)) keys.push(key);
  }
  for (const [key, ref] of ddlDocs) {
    if (supportsHistory(ref.scriptType) && enabled(ref.connection)) keys.push(key);
  }
  return keys;
}
