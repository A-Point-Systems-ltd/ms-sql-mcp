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
