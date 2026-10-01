import { ConnectionProfile } from '../connections/profile';
import { buildServerConnections } from '../connections/serverEnv';
import * as fs from 'fs';
import { atomicWriteFile } from './configWriter';
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

/** A profile left out of a refresh because its password placeholder needs a variable registered clients do not have yet. */
export interface PendingProfile { name: string; envVar: string }

export type RefreshPlan =
  | { action: 'write'; json: string; pending: PendingProfile[] }
  | { action: 'delete'; pending: PendingProfile[] };

/**
 * Plan for an automatic refresh of an existing connections.json after a profile change. Narrowing changes (close,
 * remove, read-only) must always reach the file, so the file is always rebuilt from the current profiles; only
 * profiles whose placeholder variable is new since the last write are left out (external clients would FATAL on an
 * unset variable) and reported as pending. With no connections left the file is deleted rather than written empty.
 */
export function planConnectionsFileRefresh(
  profiles: ConnectionProfile[], passwords: Map<string, string>, includePasswords: boolean, previousJson: string | undefined,
): RefreshPlan {
  const known = new Set([...(previousJson ?? '').matchAll(PLACEHOLDER_RE)].map(m => m[1]));
  const pending: PendingProfile[] = includePasswords
    ? []
    : profiles.filter(x => x.open && x.auth === 'sql').map(x => ({ name: x.name, envVar: passwordEnvVar(x.name) })).filter(x => !known.has(x.envVar));
  const pendingNames = new Set(pending.map(x => x.name));
  const result = buildConnectionsFile(profiles.filter(x => !pendingNames.has(x.name)), passwords, includePasswords);
  const count = (JSON.parse(result.json) as unknown[]).length;
  return count === 0 ? { action: 'delete', pending } : { action: 'write', json: result.json, pending };
}

/**
 * Applies {@link planConnectionsFileRefresh} to `file`. Never creates the file: returns `none` when it does not exist.
 * SECURITY: with `includePasswords=true` the written file contains clear-text passwords.
 */
export function refreshConnectionsFileOnDisk(
  file: string, profiles: ConnectionProfile[], passwords: Map<string, string>, includePasswords: boolean,
): RefreshPlan | { action: 'none' } {
  if (!fs.existsSync(file)) return { action: 'none' };
  let previous: string | undefined;
  try { previous = fs.readFileSync(file, 'utf8'); } catch { previous = undefined; }
  const plan = planConnectionsFileRefresh(profiles, passwords, includePasswords, previous);
  if (plan.action === 'delete') fs.rmSync(file, { force: true });
  else atomicWriteFile(file, plan.json);
  return plan;
}
