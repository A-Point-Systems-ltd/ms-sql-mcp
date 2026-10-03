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

export type FormMessage =
  | { type: 'cancel' }
  | { type: 'test' | 'listDatabases' | 'save'; values: FormValues };

const STRING_FIELDS = ['name', 'server', 'database', 'user', 'password', 'rawConnectionString'] as const;
const BOOL_FIELDS = ['trustServerCertificate', 'readOnly', 'insights', 'open', 'ddlHistory'] as const;

/** Validates an untrusted webview message; undefined when it is not one of the known shapes. */
export function parseFormMessage(raw: unknown): FormMessage | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const m = raw as Record<string, unknown>;
  if (m.type === 'cancel') return { type: 'cancel' };
  if (m.type !== 'test' && m.type !== 'listDatabases' && m.type !== 'save') return undefined;
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
