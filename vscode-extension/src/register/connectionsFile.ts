import { ConnectionProfile } from '../connections/profile';
import { buildServerConnections } from '../connections/serverEnv';
import { passwordEnvVar } from './naming';

export interface ConnectionsFileResult {
  /** Pretty-printed MSSQL_CONNECTIONS-shaped JSON array (open profiles only, no `default`). */
  json: string;
  /** Environment variables the client must define (placeholder mode only). */
  envVars: string[];
  /** Open profiles left out, with the reason. */
  skipped: { name: string; reason: string }[];
}

const RAW_SECRET_RE = /(^|;)\s*(password|pwd)\s*=/i;

/** True when an open profile carries a secret, so the user must choose how the file stores it. */
export function needsSecretDecision(profiles: ConnectionProfile[]): boolean {
  return profiles.some(p => p.open && (p.auth === 'sql' || (p.auth === 'raw' && RAW_SECRET_RE.test(p.rawConnectionString ?? ''))));
}

/**
 * Content of connections.json. With `includePasswords=false`, SQL auth becomes `Password="${env:MSSQLMCP_PWD_<NAME>}"`;
 * raw connection strings that embed a password cannot be rewritten safely and are skipped.
 * SECURITY: with `includePasswords=true` the result contains clear-text passwords; never log it.
 */
export function buildConnectionsFile(profiles: ConnectionProfile[], passwords: Map<string, string>, includePasswords: boolean): ConnectionsFileResult {
  const skipped: { name: string; reason: string }[] = [];
  const envVars: string[] = [];
  const seen = new Map<string, string>();
  const effective = new Map(passwords);
  const usable: ConnectionProfile[] = [];

  for (const p of profiles.filter(x => x.open)) {
    if (p.auth === 'raw' && !includePasswords && RAW_SECRET_RE.test(p.rawConnectionString ?? '')) {
      skipped.push({ name: p.name, reason: 'raw connection string contains a password; it cannot be replaced by a placeholder' });
      continue;
    }
    if (p.auth === 'sql') {
      if (includePasswords) {
        if (!passwords.has(p.name)) {
          skipped.push({ name: p.name, reason: 'no saved password' });
          continue;
        }
      } else {
        const v = passwordEnvVar(p.name);
        const clash = seen.get(v);
        if (clash) throw new Error(`Connections '${clash}' and '${p.name}' map to the same variable ${v}; rename one of them.`);
        seen.set(v, p.name);
        envVars.push(v);
        effective.set(p.name, '${env:' + v + '}');
      }
    }
    usable.push(p);
  }
  // Quote the placeholder so the server doubles any '"' in the substituted password (see ConnectionConfigLoader.ExpandEnv).
  const arr = (JSON.parse(buildServerConnections(usable, effective)) as { connectionString: string }[]).map(c => ({
    ...c,
    connectionString: c.connectionString.replace(/Password=(\$\{env:[A-Za-z_][A-Za-z0-9_]*\})/, 'Password="$1"'),
  }));
  return { json: JSON.stringify(arr, null, 2) + '\n', envVars, skipped };
}

const PLACEHOLDER_RE = /\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}/g;

/**
 * Why an automatic refresh of connections.json must not be written silently, or undefined when it may be.
 * External clients read the file at start-up and the server exits (FATAL) on an unset variable or an empty
 * config, so a refresh that needs a variable the previous file did not reference, or that has no connections,
 * needs the user's confirmation (re-registering).
 */
export function refreshConfirmationReason(previousJson: string | undefined, next: ConnectionsFileResult): string | undefined {
  const count = (() => {
    try { return (JSON.parse(next.json) as unknown[]).length; } catch { return 0; }
  })();
  if (count === 0) return 'the updated connections file would contain no connections, and registered clients would fail to start';
  const before = new Set([...(previousJson ?? '').matchAll(PLACEHOLDER_RE)].map(m => m[1]));
  const added = next.envVars.filter(v => !before.has(v));
  if (added.length) return `the updated connections file needs environment variable(s) that registered clients do not have yet: ${added.join(', ')}`;
  return undefined;
}
