import { ConnectionProfile, buildConnectionString } from './profile';

export interface ServerEnvOptions { forceReadOnly?: boolean; insights?: boolean }

/** MSSQL_CONNECTIONS payload (server README "Multiple connections"). There is no default connection. */
export function buildServerConnections(profiles: ConnectionProfile[], passwords: Map<string, string>, opts: ServerEnvOptions = {}): string {
  return JSON.stringify(profiles.filter(p => p.open).map(p => {
    const readOnly = opts.forceReadOnly ? true : p.readOnly;
    return {
      name: p.name,
      connectionString: buildConnectionString({ ...p, readOnly }, passwords.get(p.name)),
      readOnly,
      insights: opts.insights === false ? false : p.insights,
    };
  }));
}
