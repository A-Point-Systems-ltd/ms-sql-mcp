import { createHash } from 'crypto';
import { ConnectionProfile, buildConnectionString } from './profile';

export interface ServerEnvOptions { forceReadOnly?: boolean; insights?: boolean }

/** The `msSqlMcp.*` settings that shape the agent-facing server. */
export interface AgentServerSettings { insights: boolean; allowAdhocConnections: boolean; serverPath: string }

/**
 * Env for the VS Code MCP server definition. The server also reads CONNECTION_STRING and MSSQL_CONNECTIONS_FILE, which
 * the editor would otherwise pass through from its own environment and silently add connections; `null` removes them.
 * `MSSQL_SCRIPT_RUNNER` (the extension-only `run_script` switch) is removed the same way.
 */
export function agentProviderEnv(connectionsJson: string, s: AgentServerSettings): Record<string, string | null> {
  return {
    MSSQL_CONNECTIONS: connectionsJson,
    CONNECTION_STRING: null,
    MSSQL_CONNECTIONS_FILE: null,
    USE_INSIGHTS_LAYER: s.insights ? 'true' : 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: s.allowAdhocConnections ? 'true' : 'false',
    MSSQL_SCRIPT_RUNNER: null,
  };
}

/**
 * Env for the server the extension registers with Cursor's own API. Cursor's env type has no null, so inherited
 * sources are blanked with '' (the server treats empty as unset). `MSSQL_SCRIPT_RUNNER` is the extension-only
 * `run_script` switch; an agent-facing server must never inherit it from the editor's environment.
 */
export function cursorServerEnv(connectionsJson: string, s: Pick<AgentServerSettings, 'insights' | 'allowAdhocConnections'>): Record<string, string> {
  return {
    MSSQL_CONNECTIONS: connectionsJson,
    CONNECTION_STRING: '',
    MSSQL_CONNECTIONS_FILE: '',
    USE_INSIGHTS_LAYER: s.insights ? 'true' : 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: s.allowAdhocConnections ? 'true' : 'false',
    MSSQL_SCRIPT_RUNNER: '',
  };
}

/**
 * Env for the explorer / probe child processes, spread over process.env. Inherited connection sources are blanked
 * (the server ignores empty values), the Insights layer and ad-hoc connections are off.
 */
export function explorerProcessEnv(connectionsJson: string, extra: Record<string, string> = {}): Record<string, string> {
  return {
    MSSQL_CONNECTIONS: connectionsJson,
    CONNECTION_STRING: '',
    MSSQL_CONNECTIONS_FILE: '',
    USE_INSIGHTS_LAYER: 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
    ...extra,
  };
}

/** Env for Cursor / Claude Desktop / Claude Code entries: only the connections file, other sources blanked. */
export function externalClientEnv(connectionsFile: string, s: Pick<AgentServerSettings, 'insights' | 'allowAdhocConnections'>): Record<string, string> {
  return {
    MSSQL_CONNECTIONS_FILE: connectionsFile,
    CONNECTION_STRING: '',
    MSSQL_CONNECTIONS: '',
    USE_INSIGHTS_LAYER: s.insights ? 'true' : 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: s.allowAdhocConnections ? 'true' : 'false',
    MSSQL_SCRIPT_RUNNER: '',
  };
}

/**
 * MCP server definition version: `<pkgVersion>+<8 hex>`. VS Code restarts / offers to restart a running server when it
 * changes, so the hash covers every non-secret input of the server config: the open profiles (name, readOnly, insights,
 * auth, user, server, database, encrypt, trustServerCertificate, whether it is exported) and the relevant settings.
 * Passwords and raw connection strings are never hashed.
 */
export function definitionVersion(pkgVersion: string, profiles: ConnectionProfile[], s: AgentServerSettings, passwords: Map<string, string>): string {
  const open = profiles.filter(p => p.open).sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)).map(p => [
    p.name, p.readOnly, p.insights, p.auth, p.user ?? '', p.server, p.database, p.encrypt, p.trustServerCertificate, !lacksPassword(p, passwords),
  ]);
  const input = JSON.stringify({ open, insights: s.insights, adhoc: s.allowAdhocConnections, serverPath: s.serverPath });
  return `${pkgVersion}+${createHash('sha1').update(input).digest('hex').slice(0, 8)}`;
}

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
