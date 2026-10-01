// Credential masking for anything that reaches the log channel.
// No 'vscode' import — unit-testable with plain Node.

const SECRET_KEY = /^(pwd|password|pass)$/i;
const CONNECT_KEY = /connect/i;

/** Masks password / key fragments in SQL Server connection strings. */
export function redactConnectString(connect: string): string {
  return connect.replace(
    /(password|pwd|accountkey|sharedaccesskey)\s*=\s*("[^"]*"|'[^']*'|[^;]*)/gi,
    '$1=***',
  );
}

/** Deep-copy a payload, masking credential-bearing values. */
export function redact(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(redact);
  }
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [key, val] of Object.entries(value as Record<string, unknown>)) {
      if (SECRET_KEY.test(key)) {
        out[key] = '****';
      } else if (CONNECT_KEY.test(key) && typeof val === 'string') {
        out[key] = redactConnectString(val);
      } else {
        out[key] = redact(val);
      }
    }
    return out;
  }
  return value;
}
