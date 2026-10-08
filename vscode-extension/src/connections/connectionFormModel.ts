// Connection form values <-> profile. No 'vscode' import (unit-testable).

import { AuthKind, CONNECTION_COLORS, ConnectionColor, ConnectionProfile, envPlaceholderError, isConnectionColor, validateProfile } from './profile';

export interface FormValues {
  name: string;
  auth: AuthKind;
  server: string;
  database: string;
  user: string;
  /** Webview -> extension only: never pre-filled, never sent back. */
  password: string;
  rawConnectionString: string;
  encrypt: ConnectionProfile['encrypt'];
  trustServerCertificate: boolean;
  readOnly: boolean;
  insights: boolean;
  open: boolean;
  ddlHistory: boolean;
  /** '' = no color. */
  color: ConnectionColor | '';
}

export type FormErrors = Partial<Record<keyof FormValues, string>>;

export interface FormContext {
  /** The profile being edited; undefined when adding. */
  existing?: ConnectionProfile;
  /** True when a password is stored for `existing` (an empty password field then keeps it). */
  hasSavedPassword: boolean;
  existingNames: string[];
}

export const AUTH_OPTIONS: ReadonlyArray<{ kind: AuthKind; label: string }> = [
  { kind: 'windows', label: 'Windows integrated' },
  { kind: 'sql', label: 'SQL login (user + password)' },
  { kind: 'entraInteractive', label: 'Microsoft Entra - interactive' },
  { kind: 'entraDefault', label: 'Microsoft Entra - default credential' },
  { kind: 'raw', label: 'Raw connection string' },
];

export const ENCRYPTION_OPTIONS: ReadonlyArray<{ value: ConnectionProfile['encrypt']; label: string }> = [
  { value: 'mandatory', label: 'Mandatory' },
  { value: 'optional', label: 'Optional (no encryption)' },
  { value: 'strict', label: 'Strict (TDS 8)' },
];

/** Help text under the encryption select, per level. */
export const ENCRYPTION_HELP: Record<ConnectionProfile['encrypt'], string> = {
  mandatory: 'Encrypted. With "Trust server certificate" off, the server certificate must be trusted by this machine. With it on, the certificate is not verified: safe against sniffing, not against a man-in-the-middle. Use on trusted networks.',
  optional: 'Traffic may be unencrypted, including credentials on SQL login. Legacy servers only.',
  strict: 'TLS 1.3 from the first byte; requires SQL Server 2022+ with a trusted certificate.',
};

export const TRUST_HELP = 'Skips certificate verification (self-signed / on-prem servers).';

export const COLOR_OPTIONS: ReadonlyArray<{ value: ConnectionColor | ''; label: string }> = [
  { value: '', label: 'None' },
  ...(Object.keys(CONNECTION_COLORS) as ConnectionColor[]).map(c => ({ value: c, label: CONNECTION_COLORS[c].label })),
];

export const COLOR_HELP = 'Colors the connection name in the tree and the tabs of its query, DDL and data windows (e.g. red for production).';

export const DDL_HISTORY_LABEL = 'DDL history (audit trigger)';
export const DDL_HISTORY_HELP = "Records every schema change in dbo.DDL_AuditLog via the DDL_Audit database trigger, so you can diff an object's history. If they are missing, they are created on read-write connections (you are asked first).";

const AUTH_KINDS = AUTH_OPTIONS.map(a => a.kind);
const ENCRYPT_KINDS = ENCRYPTION_OPTIONS.map(o => o.value);

export function defaultFormValues(): FormValues {
  return {
    name: '', auth: 'windows', server: '', database: '', user: '', password: '', rawConnectionString: '',
    encrypt: 'optional', trustServerCertificate: true, readOnly: true, insights: true, open: true, ddlHistory: false, color: '',
  };
}

export function profileToFormValues(p: ConnectionProfile): FormValues {
  return {
    name: p.name, auth: p.auth, server: p.server, database: p.database, user: p.user ?? '', password: '',
    rawConnectionString: p.rawConnectionString ?? '',
    encrypt: p.encrypt, trustServerCertificate: p.trustServerCertificate,
    readOnly: p.readOnly, insights: p.insights, open: p.open, ddlHistory: p.ddlHistory === true,
    color: isConnectionColor(p.color) ? p.color : '',
  };
}

const RAW_PASSWORD_RE = /\b(password|pwd)\s*=/i;

function nameError(name: string): string | undefined {
  const errs = validateProfile({ name, server: 'x', database: 'x', auth: 'windows', readOnly: true, insights: true, open: true, encrypt: 'mandatory', trustServerCertificate: false });
  return errs[0];
}

/**
 * Validates the form and builds the profile. Errors are keyed by field so the webview can show them inline.
 * `password` is returned only for SQL login and only when typed (empty = keep the saved one).
 */
export function formToProfile(v: FormValues, ctx: FormContext): { profile?: ConnectionProfile; password?: string; errors: FormErrors } {
  const errors: FormErrors = {};

  const name = ctx.existing ? ctx.existing.name : v.name.trim();
  if (!ctx.existing) {
    const err = nameError(name);
    if (err) errors.name = err;
    else if (ctx.existingNames.some(n => n.toLowerCase() === name.toLowerCase())) errors.name = 'A connection with this name already exists.';
  }

  const server = v.server.trim();
  const database = v.database.trim();
  const user = v.user.trim();
  const raw = v.rawConnectionString.trim();
  const usesUser = v.auth === 'sql' || v.auth === 'entraInteractive';

  if (v.auth === 'raw') {
    if (!raw) errors.rawConnectionString = 'Connection string is required.';
    else if (RAW_PASSWORD_RE.test(raw)) errors.rawConnectionString = 'Remove the password - use another authentication type so it can be kept in secret storage.';
    else {
      const env = envPlaceholderError(raw);
      if (env) errors.rawConnectionString = env;
    }
  } else {
    const field = (key: 'server' | 'database' | 'user', value: string, required: boolean, label: string) => {
      const err = value ? envPlaceholderError(value) : required ? `${label} is required.` : undefined;
      if (err) errors[key] = err;
    };
    field('server', server, true, 'Server');
    field('database', database, true, 'Database');
    field('user', user, usesUser, 'User');
    if (v.auth === 'sql') {
      if (!v.password) {
        if (!ctx.hasSavedPassword) errors.password = 'Password is required.';
      } else {
        const env = envPlaceholderError(v.password);
        if (env) errors.password = env;
      }
    }
  }

  if (Object.keys(errors).length) return { errors };

  const profile: ConnectionProfile = {
    name,
    server: v.auth === 'raw' ? '' : server,
    database: v.auth === 'raw' ? '' : database,
    auth: v.auth,
    user: v.auth !== 'raw' && usesUser ? user : undefined,
    readOnly: v.readOnly,
    insights: v.insights,
    open: v.open,
    encrypt: v.encrypt,
    trustServerCertificate: v.trustServerCertificate,
    rawConnectionString: v.auth === 'raw' ? raw : undefined,
    ddlHistory: v.ddlHistory,
    ...(v.color ? { color: v.color } : {}),
  };
  // Final guard: the store throws on a profile validateProfile rejects.
  const leftover = validateProfile(profile);
  if (leftover.length) return { errors: { name: leftover[0] } };
  return { profile, password: v.auth === 'sql' && v.password ? v.password : undefined, errors };
}

/** Name of the throw-away profile used by Test connection / List databases (the form's own name may be empty). */
export const PROBE_NAME = 'probe';

/**
 * Validates the form for a probe (Test connection / List databases) and builds the throw-away profile.
 * `database` overrides the form's database (List databases connects to master). The name is not validated.
 */
export function formToProbeProfile(v: FormValues, o: { hasPassword: boolean; database?: string }): { profile?: ConnectionProfile; errors: FormErrors } {
  const r = formToProfile({ ...v, name: PROBE_NAME, database: o.database ?? v.database, open: true }, { hasSavedPassword: o.hasPassword, existingNames: [] });
  return { profile: r.profile, errors: r.errors };
}

/** A database of the form's server and its sys.databases state_desc (ONLINE, OFFLINE, RESTORING, ...). */
export interface DatabaseInfo {
  name: string;
  state: string;
}

export const ONLINE = 'ONLINE';
export const OFFLINE = 'OFFLINE';

/** Datalist label: the name, plus "(offline)" / "(restoring)" ... for a database that is not online. */
export function databaseLabel(d: DatabaseInfo): string {
  return isOnline(d.state) ? d.name : `${d.name} (${d.state.toLowerCase().replace(/_/g, ' ')})`;
}

export function isOnline(state: string | null | undefined): boolean {
  return (state ?? '').toUpperCase() === ONLINE;
}

/** Only an OFFLINE database can be brought online; other states (RESTORING, SUSPECT, ...) need a DBA. */
export function canBringOnline(state: string | null | undefined): boolean {
  return (state ?? '').toUpperCase() === OFFLINE;
}

/** Status line after List databases: "12 databases (2 not online). Pick one from the Database field." */
export function describeDatabaseList(dbs: readonly DatabaseInfo[]): string {
  const notOnline = dbs.filter(d => !isOnline(d.state)).length;
  return `${dbs.length} database(s) found${notOnline ? ` (${notOnline} not online)` : ''}. Pick one from the Database field.`;
}

/** The state of `name` in a listing (case-insensitive, like SQL Server's default collation), or undefined. */
export function stateOf(dbs: readonly DatabaseInfo[], name: string): string | undefined {
  const n = name.trim().toLowerCase();
  return n ? dbs.find(d => d.name.toLowerCase() === n)?.state : undefined;
}

/** Which server and login database states were learned for: a change invalidates them (another server's "Sales"). */
export function stateCacheKey(v: Pick<FormValues, 'auth' | 'server' | 'user' | 'rawConnectionString'>): string {
  return [v.auth, v.server.trim().toLowerCase(), v.user.trim().toLowerCase(), v.auth === 'raw' ? v.rawConnectionString.trim() : ''].join('|');
}

/**
 * True for SQL Server's "this database cannot be opened" errors (4060 cannot open database, 942 offline, 922 being
 * recovered, 927 restoring), which a database state lookup can explain. False for a failed login (18456): a second
 * login attempt would only count toward a lockout policy. Matches the English server messages the tools return.
 */
export function isDatabaseUnavailableError(message: string): boolean {
  return /cannot open database|cannot be opened|is being recovered|in the middle of a restore/i.test(message);
}

/** What Test should do with probe_test's data ({ok, message, databaseUnavailable, state}). */
export type OpenCheck =
  | { kind: 'ok' }
  | { kind: 'notOnline'; state: string; text: string }
  | { kind: 'failed'; message: string };

/**
 * Interprets probe_test. The server classified the failure by SQL error number (any message language) and looked up
 * the state only for a database that could not be opened, so a wrong password never costs a second login.
 */
export function interpretOpenCheck(data: unknown, database: string): OpenCheck {
  const d = (data && typeof data === 'object' ? data : {}) as Record<string, unknown>;
  if (d.ok === true) return { kind: 'ok' };
  const message = typeof d.message === 'string' && d.message ? d.message : 'The connection failed.';
  const state = typeof d.state === 'string' ? d.state : undefined;
  if (d.databaseUnavailable === true && state && !isOnline(state)) {
    return { kind: 'notOnline', state, text: `Database '${database || '(from the connection string)'}' is ${state}. ${message}` };
  }
  return { kind: 'failed', message };
}

/** An MCP "unknown tool" error: the server exe predates the tool (e.g. a custom msSqlMcp.serverPath). */
export function isUnknownToolError(message: string): boolean {
  return /unknown tool/i.test(message);
}

/** probe_list_databases data -> DatabaseInfo[] (any casing, malformed rows dropped). */
export function parseDatabaseList(data: unknown): DatabaseInfo[] {
  if (!Array.isArray(data)) return [];
  const out: DatabaseInfo[] = [];
  for (const row of data) {
    if (!row || typeof row !== 'object') continue;
    const r = row as Record<string, unknown>;
    const name = r.name ?? r.Name;
    const state = r.state ?? r.State;
    if (typeof name === 'string' && typeof state === 'string') out.push({ name, state });
  }
  return out;
}

export type FormMessage =
  | { type: 'cancel' }
  | { type: 'test' | 'listDatabases' | 'save' | 'bringOnline'; values: FormValues };

const STRING_FIELDS = ['name', 'server', 'database', 'user', 'password', 'rawConnectionString'] as const;
const BOOL_FIELDS = ['trustServerCertificate', 'readOnly', 'insights', 'open', 'ddlHistory'] as const;

/** Validates an untrusted webview message; undefined when it is not one of the known shapes. */
export function parseFormMessage(raw: unknown): FormMessage | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const m = raw as Record<string, unknown>;
  if (m.type === 'cancel') return { type: 'cancel' };
  if (m.type !== 'test' && m.type !== 'listDatabases' && m.type !== 'save' && m.type !== 'bringOnline') return undefined;
  const v = m.values as Record<string, unknown> | null | undefined;
  if (!v || typeof v !== 'object') return undefined;
  if (!STRING_FIELDS.every(k => typeof v[k] === 'string')) return undefined;
  if (!BOOL_FIELDS.every(k => typeof v[k] === 'boolean')) return undefined;
  if (!AUTH_KINDS.includes(v.auth as AuthKind)) return undefined;
  if (!ENCRYPT_KINDS.includes(v.encrypt as ConnectionProfile['encrypt'])) return undefined;
  // An older form page sends no color: treat it as none.
  const color = v.color === undefined || v.color === '' ? '' : isConnectionColor(v.color) ? v.color : undefined;
  if (color === undefined) return undefined;
  const values: FormValues = {
    name: v.name as string, auth: v.auth as AuthKind, server: v.server as string, database: v.database as string,
    user: v.user as string, password: v.password as string, rawConnectionString: v.rawConnectionString as string,
    encrypt: v.encrypt as ConnectionProfile['encrypt'], trustServerCertificate: v.trustServerCertificate as boolean,
    readOnly: v.readOnly as boolean, insights: v.insights as boolean, open: v.open as boolean,
    ddlHistory: v.ddlHistory as boolean, color,
  };
  return { type: m.type, values };
}
