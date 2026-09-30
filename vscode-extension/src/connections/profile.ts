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
}

const NAME_RE = /^[A-Za-z0-9_.-]{1,64}$/;

export function validateProfile(p: ConnectionProfile): string[] {
  const errors: string[] = [];
  if (!NAME_RE.test(p.name)) errors.push('Name must be 1-64 letters, digits, "-", "_" or ".".');
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
    'Application Name=MSSQL-MCP',
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
