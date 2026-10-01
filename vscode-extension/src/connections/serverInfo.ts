import { pick } from '../client/parse';

/** One-line summary of a get_server_info payload (data.server.productVersion / serverName). */
export function describeServerInfo(payload: unknown): string {
  const data = pick(payload, 'data') ?? payload;
  const server = pick(data, 'server');
  const name = pick(server, 'serverName');
  const version = pick(server, 'productVersion');
  if (typeof name !== 'string' && typeof version !== 'string') return 'Connected (server returned no version information).';
  return `Connected to ${typeof name === 'string' ? name : 'server'}${typeof version === 'string' ? ` — SQL Server ${version}` : ''}.`;
}

const TLS_HINT_RE = /certificate|SSL|TLS|security package|pre-login/i;

/** Appends a remediation tip when a connection error looks like a TLS / certificate problem. */
export function withConnectionHint(message: string): string {
  return TLS_HINT_RE.test(message)
    ? `${message} Tip: edit the connection and choose 'trust server certificate' or 'Optional' encryption (SQL Server 2008 R2 may only support TLS 1.0).`
    : message;
}
