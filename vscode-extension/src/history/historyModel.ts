// DDL history (dbo.DDL_AuditLog, filled by the DDL_Audit database trigger): result parsing, the install decision,
// quick pick items, diff titles and `mssql-history:` URIs. No 'vscode' import: unit-testable with plain Node.
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import type { ObjectRef } from '../explorer/catalog';
import { parseUriQuery, qEnc, qualified, sqlString } from '../explorer/sqlText';
import { findProfile } from '../query/editorState';
import type { QueryAssociation } from '../query/queryDocuments';
import { profileTarget, titlePart } from '../query/sqlDocNames';
import { targetMismatch, targetOf } from '../query/targetGuard';

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
export const incompatibleWarning = (where: string): string =>
  `dbo.DDL_AuditLog on ${where} does not have the columns (or the column types and widths) the DDL_Audit trigger writes, so DDL history cannot be set up here. Ask a DBA to align or rename the existing table.`;
export const triggerMissingWarning = (where: string): string =>
  `The DDL_Audit trigger is not installed on ${where}, so changes are not recorded.`;
export const readOnlyWarning = (where: string): string =>
  `DDL history needs dbo.DDL_AuditLog and the DDL_Audit database trigger on ${where}. This connection is read-only, so they were not created — ask a DBA or set it up from a read-write connection.`;
export const setUpMessage = (where: string): string => `DDL history is set up on ${where}.`;
export const noHistoryMessage = (obj: string, connection: string): string => `No DDL history recorded for ${obj} on '${connection}'.`;

/** `<server>\<database>` of a profile, as in its tab titles (raw connection strings are parsed best-effort). */
export function targetText(profile: ConnectionProfile): string {
  const t = profileTarget(profile, profile.name);
  return `${t.server}\\${t.database}`;
}

/**
 * `<server>\<db>` as the server reports them (`ddl_history status`: @@SERVERNAME and DB_NAME()), so messages name the
 * database the connection actually reached. A part the status lacks falls back to the profile (see {@link targetText}).
 */
export function statusTargetText(status: Pick<HistoryStatus, 'serverName' | 'databaseName'>, profile: ConnectionProfile): string {
  if (status.serverName && status.databaseName) return `${status.serverName}\\${status.databaseName}`;
  const t = profileTarget(profile, profile.name);
  return `${status.serverName ?? t.server}\\${status.databaseName ?? t.database}`;
}

/** Whether the oldest listed entry may have an earlier one that was not loaded: the server returned a full page. */
export function moreNotLoaded(listed: number): boolean {
  return listed >= LIST_TOP;
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
  /** The server's @@SERVERNAME, when reported. */
  serverName?: string;
  /** The server's DB_NAME(), when reported. */
  databaseName?: string;
  /**
   * The server's notes: compatible but lossy choices of an existing table (for example a varchar(max) CommandText), or
   * why an existing DDL_Audit_Writer must not be used (then canInstall is false).
   */
  warnings?: string[];
  /** dbo.DDL_AuditLog has DML triggers, so the installed DDL_Audit records nothing. */
  loggingSuppressed?: boolean;
}

/** `ddl_history status` data; a missing or non-boolean flag reads as false, and a missing or blank name is left out. */
export function parseHistoryStatus(data: unknown): HistoryStatus {
  const flag = (key: string) => pick(data, key) === true;
  const name = (key: string) => {
    const v = pick(data, key);
    return typeof v === 'string' && v.trim() ? v.trim() : undefined;
  };
  const serverName = name('serverName');
  const rawWarnings = pick(data, 'warnings');
  const warnings = (Array.isArray(rawWarnings) ? rawWarnings : []).filter((w): w is string => typeof w === 'string' && !!w.trim());
  const databaseName = name('databaseName');
  return {
    tableExists: flag('tableExists'),
    tableCompatible: flag('tableCompatible'),
    triggerExists: flag('triggerExists'),
    triggerEnabled: flag('triggerEnabled'),
    canInstall: flag('canInstall'),
    ...(serverName ? { serverName } : {}),
    ...(databaseName ? { databaseName } : {}),
    ...(warnings.length ? { warnings } : {}),
    ...(flag('loggingSuppressed') ? { loggingSuppressed: true } : {}),
  };
}

export type InstallDecision = 'none' | 'warnDisabled' | 'warnReadOnly' | 'warnIncompatible' | 'warnBlocked' | 'confirmInstall';

/** Shown whenever status reports loggingSuppressed. */
export const LOGGING_SUPPRESSED_WARNING = 'dbo.DDL_AuditLog has triggers, so DDL_Audit does not record changes.';

/**
 * What the form does after a save, in order:
 * - an existing table the trigger cannot insert into: a warning (whether or not the trigger exists); nothing is created;
 * - table and trigger exist: nothing when the trigger is enabled, else a warning (it is never enabled for the user);
 * - something is missing: a modal confirmation before `install` only when the server reports `canInstall`; on a
 *   read/write connection whose status says why it cannot (`warnings`, for example an over-privileged existing
 *   DDL_Audit_Writer), those warnings and no modal; otherwise the read-only warning (the server refuses to install on
 *   connections it serves read-only).
 */
export function installDecision(status: HistoryStatus, readOnly: boolean): InstallDecision {
  if (status.tableExists && !status.tableCompatible) return 'warnIncompatible';
  if (status.tableExists && status.triggerExists) return status.triggerEnabled ? 'none' : 'warnDisabled';
  if (!readOnly && status.canInstall) return 'confirmInstall';
  return !readOnly && status.warnings?.length ? 'warnBlocked' : 'warnReadOnly';
}

/** Why an object's history list came back empty: the trigger is missing or disabled, or nothing was recorded yet. */
export function emptyHistoryOutcome(status: HistoryStatus): 'triggerMissing' | 'triggerDisabled' | 'loggingSuppressed' | 'noHistory' {
  if (!status.triggerExists) return 'triggerMissing';
  if (status.triggerEnabled && status.loggingSuppressed) return 'loggingSuppressed';
  return status.triggerEnabled ? 'noHistory' : 'triggerDisabled';
}

/** How the trigger behaves and how to remove it (the install modal and the README say the same). */
export const INSTALL_SAFETY_TEXT =
  'The trigger runs as the low-privilege user DDL_Audit_Writer (INSERT/SELECT on dbo.DDL_AuditLog only). If logging fails, the DDL statement still runs and is not logged, except when another trigger on DDL_AuditLog rolls back. To remove: DROP TRIGGER [DDL_Audit] ON DATABASE; DROP USER [DDL_Audit_Writer].';

/** Added to the modal detail when the table already exists: the install grants on it. */
export const EXISTING_TABLE_GRANT_TEXT =
  'This grants INSERT and SELECT on the existing dbo.DDL_AuditLog to the new user DDL_Audit_Writer.';

/** The modal text: names server/db and the connection, and lists exactly what will be created. */
export function installPrompt(status: HistoryStatus, where: string, connection: string): { message: string; detail: string } {
  const parts = [
    ...(status.tableExists ? [] : ['table dbo.DDL_AuditLog']),
    ...(status.triggerExists ? [] : ['database trigger DDL_Audit']),
  ];
  return {
    message: `Create DDL history on ${where} (connection '${connection}')?`,
    detail: [
      `This creates ${parts.join(' and ')}. The trigger records every DDL change in this database.`,
      ...(status.tableExists ? [EXISTING_TABLE_GRANT_TEXT] : []),
      INSTALL_SAFETY_TEXT,
      // The server's compatible-but-lossy notes about an existing table (status.warnings).
      ...(status.warnings ?? []),
    ].join(' '),
  };
}

/**
 * Whether a save runs the install flow: `ddlHistory` is on in the saved profile, and it was not on in the form's
 * baseline (undefined for a new connection; the form opened by "Set up…" uses false so its save always runs it), or
 * the edit moved the connection to another server or database (`previous`: the profile before the edit).
 */
export function runsHistorySetup(baseline: boolean | undefined, saved: ConnectionProfile, previous?: ConnectionProfile): boolean {
  if (saved.ddlHistory !== true) return false;
  if (baseline !== true) return true;
  return !!previous && targetMismatch(targetOf(previous), saved);
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

/**
 * An audit entry id (the server's bigint-safe ID): a non-negative safe integer given as a number or as a string of
 * digits. Anything else, including integers beyond Number.MAX_SAFE_INTEGER (which JSON numbers cannot carry exactly),
 * is undefined.
 */
export function parseAuditId(v: unknown): number | undefined {
  if (typeof v === 'string') v = /^\d{1,16}$/.test(v.trim()) ? Number(v.trim()) : undefined;
  return typeof v === 'number' && Number.isSafeInteger(v) && v >= 0 ? v : undefined;
}

/** `ddl_history list` data; rows without an integer id are dropped. */
export function parseHistoryEntries(data: unknown): HistoryEntry[] {
  if (!Array.isArray(data)) return [];
  return data.flatMap((row): HistoryEntry[] => {
    const id = parseAuditId(pick(row, 'id'));
    if (id === undefined) return [];
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
  return { id: parseAuditId(pick(data, 'id')), postTime: str(pick(data, 'postTime')) ?? '', commandText: str(pick(data, 'commandText')) ?? '' };
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

/** The audit `ObjectType` values (EVENTDATA) of each script type. Unknown script types have no filter. */
export const AUDIT_OBJECT_TYPES: Readonly<Record<string, readonly string[]>> = Object.freeze({
  Table: ['TABLE'],
  View: ['VIEW'],
  StoredProcedure: ['PROCEDURE'],
  TableFunction: ['FUNCTION'],
  ScalarFunction: ['FUNCTION'],
  TableTrigger: ['TRIGGER'],
  DatabaseTrigger: ['TRIGGER'],
  Type: ['TYPE'],
});

/**
 * The entries that belong to an object of `scriptType` (the server matches by name and schema only): the audit
 * ObjectType must be one of {@link AUDIT_OBJECT_TYPES} (case-insensitive; entries with no ObjectType are kept), and a
 * database trigger's entries have no schema. Order is kept (newest first).
 */
export function filterEntries(entries: readonly HistoryEntry[], scriptType: string): HistoryEntry[] {
  const types = AUDIT_OBJECT_TYPES[scriptType];
  return entries.filter(e => {
    if (types && e.objectType && !types.includes(e.objectType.toUpperCase())) return false;
    if (scriptType === 'DatabaseTrigger' && e.schemaName) return false;
    return true;
  });
}

/** The newest entry that is not a DROP (its command text is a definition), for "Compare with current". */
export function latestDefinitionEntry(entries: readonly HistoryEntry[]): HistoryEntry | undefined {
  return entries.find(e => !/^DROP/i.test(e.eventType ?? ''));
}

/**
 * Quick pick items, newest first. A module's list starts with "Compare … with the current definition" when it has a
 * non-DROP entry to compare.
 */
export function historyPickItems(entries: readonly HistoryEntry[], isModule: boolean): HistoryPickItem[] {
  if (!entries.length) return [];
  const items = entries.map((e, index): HistoryPickItem => ({
    label: `${formatPostTime(e.postTime)} · ${e.eventType ?? '?'}`,
    description: e.loginName ?? '',
    detail: `${e.hostName ?? '?'} · ${e.programName ?? '?'} · ${e.length ?? '?'} chars`,
    action: { kind: 'entry', index },
  }));
  return isModule && latestDefinitionEntry(entries) ? [{ label: COMPARE_CURRENT_LABEL, action: { kind: 'current' } }, ...items] : items;
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
  | { kind: 'entry'; connection: string; target: string; id: number; label: string }
  | { kind: 'empty'; label: string; more: boolean }
  | { kind: 'current'; connection: string; target: string; object: ObjectRef; label: string; nonce: string };

/**
 * `mssql-history:/<label>.sql?v=entry&c=..&w=..&i=..`, `?v=empty&m=0|1`, or `?v=current&c=..&w=..&t=..&n=..[&s=..]&r=..`.
 * Query values use sqlText's URI-unreserved encoding (as ddlUri). `w` is the connection's `<server>/<db>` target, so an
 * edited connection never reuses text VS Code cached for the old database; `r` (current) makes each comparison a new
 * document, so the definition is read again instead of a cached copy.
 */
export function historyUri(ref: HistoryDocRef): string {
  let q: string;
  switch (ref.kind) {
    case 'entry': q = `v=entry&c=${qEnc(ref.connection)}&w=${qEnc(ref.target)}&i=${ref.id}`; break;
    case 'empty': q = `v=empty&m=${ref.more ? 1 : 0}`; break;
    case 'current': {
      const o = ref.object;
      q = `v=current&c=${qEnc(ref.connection)}&w=${qEnc(ref.target)}&t=${qEnc(o.scriptType)}&n=${qEnc(o.name)}`
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
  const target = params.get('w') ?? '';
  switch (params.get('v')) {
    case 'entry': {
      const id = parseAuditId(params.get('i') ?? '');
      if (!connection || id === undefined) return undefined;
      return { kind: 'entry', connection, target, id, label };
    }
    case 'empty':
      return { kind: 'empty', label, more: params.get('m') === '1' };
    case 'current': {
      const scriptType = params.get('t') ?? '';
      const name = params.get('n') ?? '';
      if (!connection || !scriptType || !name) return undefined;
      const object: ObjectRef = { connection, scriptType, ...(params.has('s') ? { schema: params.get('s')! } : {}), name };
      return { kind: 'current', connection, target, object, label, nonce: params.get('r') ?? '' };
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
