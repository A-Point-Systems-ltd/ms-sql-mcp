import { ConnectionProfile, buildConnectionString } from './profile';

export interface ServerEnvOptions { forceReadOnly?: boolean; insights?: boolean }

/** True when the profile uses SQL auth and has no stored password, so it cannot be exported. */
function lacksPassword(p: ConnectionProfile, passwords: Map<string, string>): boolean {
  return p.auth === 'sql' && !passwords.has(p.name);
}

/** Names of open SQL-auth profiles that {@link buildServerConnections} skips for want of a saved password. */
export function missingPasswords(profiles: ConnectionProfile[], passwords: Map<string, string>): string[] {
  return profiles.filter(p => p.open && lacksPassword(p, passwords)).map(p => p.name);
}

/** User-facing message for a profile skipped because it has no saved password. */
export function missingPasswordMessage(name: string): string {
  return `Connection '${name}' has no saved password — edit it to set one.`;
}

/**
 * MSSQL_CONNECTIONS payload (server README "Multiple connections"). There is no default connection.
 * Open SQL-auth profiles without a stored password are skipped (see {@link missingPasswords}).
 *
 * SECURITY: the returned JSON contains full connection strings including passwords.
 * It must never be logged, traced or shown to the user; pass it only to a child-process env or a config file.
 */
export function buildServerConnections(profiles: ConnectionProfile[], passwords: Map<string, string>, opts: ServerEnvOptions = {}): string {
  return JSON.stringify(profiles.filter(p => p.open && !lacksPassword(p, passwords)).map(p => {
    const readOnly = opts.forceReadOnly ? true : p.readOnly;
    return {
      name: p.name,
      connectionString: buildConnectionString({ ...p, readOnly }, passwords.get(p.name)),
      readOnly,
      insights: opts.insights === false ? false : p.insights,
    };
  }));
}
