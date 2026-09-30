// Manual end-to-end check (not shipped): spawns the built MssqlMcp.exe and calls get_server_info.
// Usage: RUN_DCDEV_EXT_CHECK=1 [DCDEV_SERVER=DC\DEV] node scripts/check-dcdev.mjs [path-to-MssqlMcp.exe]
import { McpStdioClient } from '../out/client/mcpStdioClient.js';
import { buildServerConnections, explorerProcessEnv } from '../out/connections/serverEnv.js';
import { describeServerInfo } from '../out/connections/serverInfo.js';

if (process.env.RUN_DCDEV_EXT_CHECK !== '1') { console.log('Skipped (set RUN_DCDEV_EXT_CHECK=1).'); process.exit(0); }
const exe = process.argv[2] ?? new URL('../../MssqlMcp/bin/Release/net10.0/win-x64/MssqlMcp.exe', import.meta.url).pathname.replace(/^\/(\w:)/, '$1');
// Target server: DCDEV_SERVER (e.g. DC\DEV for the 2008 R2 check), default LocalDB. Read-only, master only.
const server = process.env.DCDEV_SERVER || '(localdb)\\MSSQLLocalDB';
const profile = { name: 'dcdev', server, database: 'master', auth: 'windows', readOnly: true, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true };
const log = { info() {}, debug() {}, trace() {}, warn: (_s, m) => console.error('warn:', m), error: (_s, m) => console.error('error:', m) };
const client = new McpStdioClient(exe, explorerProcessEnv(buildServerConnections([profile], new Map(), { forceReadOnly: true, insights: false })), log);
try {
  await client.initialize();
  console.log(describeServerInfo(await client.callTool('get_server_info', { connection: 'dcdev' })));
} finally { client.dispose(); }
