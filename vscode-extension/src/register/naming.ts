// Pure naming helpers for external-client registration.

/**
 * Server key used in every client config. Cursor shows an extension-registered server as `extension-<name>`
 * (the prefix is added by Cursor), so it lists this one as `extension-APoint-ms-sql`.
 */
export const SERVER_KEY = 'APoint-ms-sql';

/** The key earlier versions registered; removed from client configs when registering and flagged as a duplicate. */
export const LEGACY_SERVER_KEY = 'ms-sql';

/** Uppercase, every character outside [A-Z0-9_] becomes '_'. */
export function normalizeName(name: string): string {
  return name.toUpperCase().replace(/[^A-Z0-9_]/g, '_');
}

/** Environment variable a `${env:...}` password placeholder refers to. */
export function passwordEnvVar(profileName: string): string {
  return `MSSQLMCP_PWD_${normalizeName(profileName)}`;
}

/** yyyyMMddHHmmss in local time. */
export function timestamp(d: Date): string {
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${p(d.getFullYear(), 4)}${p(d.getMonth() + 1)}${p(d.getDate())}${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

/** `<path>.<yyyyMMddHHmmss>.bak`; `attempt` > 0 adds `-<attempt+1>` so two backups in one second do not collide. */
export function backupPath(configPath: string, now: Date, attempt = 0): string {
  return `${configPath}.${timestamp(now)}${attempt > 0 ? `-${attempt + 1}` : ''}.bak`;
}
