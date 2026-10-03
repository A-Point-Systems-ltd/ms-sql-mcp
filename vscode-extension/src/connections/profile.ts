export type AuthKind = 'windows' | 'sql' | 'entraInteractive' | 'entraDefault' | 'raw';

export interface ConnectionProfile {
  name: string;
  server: string;
  database: string;
  auth: AuthKind;
  user?: string;
  readOnly: boolean;
  insights: boolean;
  open: boolean;
  encrypt: 'mandatory' | 'optional' | 'strict';
  trustServerCertificate: boolean;
  rawConnectionString?: string;
  /**
   * Offer the DDL history (dbo.DDL_AuditLog + the DDL_Audit database trigger) for this connection's objects; a profile
   * without it is treated as false. Extension-only: not part of the agent server env nor of its definition version.
   */
  ddlHistory?: boolean;
  /** Label / tab color that tells connections apart (e.g. prod vs dev). Extension-only, like `ddlHistory`. */
  color?: ConnectionColor;
}

export type ConnectionColor = 'red' | 'orange' | 'yellow' | 'green' | 'blue' | 'purple';

/** The connection colors: theme color id (tree labels, editor tabs) and a fixed hex (Data View tab icon). */
export const CONNECTION_COLORS: Readonly<Record<ConnectionColor, { label: string; themeColor: string; hex: string }>> = {
  red: { label: 'Red', themeColor: 'charts.red', hex: '#f14c4c' },
  orange: { label: 'Orange', themeColor: 'charts.orange', hex: '#d18616' },
  yellow: { label: 'Yellow', themeColor: 'charts.yellow', hex: '#cca700' },
  green: { label: 'Green', themeColor: 'charts.green', hex: '#388a34' },
  blue: { label: 'Blue', themeColor: 'charts.blue', hex: '#3794ff' },
  purple: { label: 'Purple', themeColor: 'charts.purple', hex: '#b180d7' },
};

export function isConnectionColor(v: unknown): v is ConnectionColor {
  return typeof v === 'string' && Object.prototype.hasOwnProperty.call(CONNECTION_COLORS, v);
}

const NAME_RE = /^[A-Za-z0-9_.-]{1,64}$/;
const ENV_PLACEHOLDER_RE = /\$\{env:/i;

/**
 * The server expands `${env:NAME}` in connection strings from its own environment, so a profile value containing
 * one could read (and send to a SQL Server) any variable of the server process. Such input is rejected.
 */
export function envPlaceholderError(value: string | undefined): string | undefined {
  return value !== undefined && ENV_PLACEHOLDER_RE.test(value) ? 'The text "${env:" is not allowed here.' : undefined;
}

export function validateProfile(p: ConnectionProfile): string[] {
  const errors: string[] = [];
  if (!NAME_RE.test(p.name)) errors.push('Name must be 1-64 letters, digits, "-", "_" or ".".');
  for (const v of [p.server, p.database, p.user, p.rawConnectionString]) {
    const envError = envPlaceholderError(v);
    if (envError) { errors.push(envError); break; }
  }
  if (p.auth === 'raw') {
    if (!p.rawConnectionString?.trim()) errors.push('Connection string is required.');
    return errors;
  }
  if (!p.server.trim()) errors.push('Server is required.');
  if (!p.database.trim()) errors.push('Database is required.');
  if ((p.auth === 'sql' || p.auth === 'entraInteractive') && !p.user?.trim()) errors.push('User is required for this authentication type.');
  return errors;
}

/** Quotes a value the way SqlConnectionStringBuilder does when it contains ; = " ' or leading/trailing spaces. */
function quote(value: string): string {
  if (!/[;="']|^\s|\s$/.test(value)) return value;
  return `"${value.replace(/"/g, '""')}"`;
}

export function buildConnectionString(p: ConnectionProfile, password: string | undefined): string {
  if (p.auth === 'raw') return p.rawConnectionString ?? '';
  const parts: string[] = [
    `Data Source=${quote(p.server)}`,
    `Initial Catalog=${quote(p.database)}`,
    `Encrypt=${p.encrypt === 'optional' ? 'False' : p.encrypt === 'strict' ? 'Strict' : 'True'}`,
    `Trust Server Certificate=${p.trustServerCertificate ? 'True' : 'False'}`,
    'Application Name=APoint-ms-sql',
  ];
  switch (p.auth) {
    case 'windows': parts.push('Integrated Security=True'); break;
    case 'sql': parts.push(`User ID=${quote(p.user ?? '')}`, `Password=${quote(password ?? '')}`); break;
    case 'entraInteractive': parts.push('Authentication="Active Directory Interactive"', `User ID=${quote(p.user ?? '')}`); break;
    case 'entraDefault': parts.push('Authentication="Active Directory Default"'); break;
  }
  if (p.readOnly) parts.push('Application Intent=ReadOnly');
  return parts.join(';');
}
