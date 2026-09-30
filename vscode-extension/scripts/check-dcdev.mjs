// Manual end-to-end check (not shipped): spawns the built MssqlMcp.exe and calls get_server_info.
// Usage: RUN_DCDEV_EXT_CHECK=1 node scripts/check-dcdev.mjs [path-to-MssqlMcp.exe]
import { McpStdioClient } from '../out/client/mcpStdioClient.js';
import { buildServerConnections } from '../out/connections/serverEnv.js';
import { describeServerInfo } from '../out/connections/serverInfo.js';

if (process.env.RUN_DCDEV_EXT_CHECK !== '1') { console.log('Skipped (set RUN_DCDEV_EXT_CHECK=1).'); process.exit(0); }
const exe = process.argv[2] ?? new URL('../../MssqlMcp/bin/Release/net10.0/win-x64/MssqlMcp.exe', import.meta.url).pathname.replace(/^\/(\w:)/, '$1');
const profile = { name: 'dcdev', server: 'DC\DEV', database: 'master', auth: 'windows', readOnly: true, insights: false, open: true, encrypt: 'mandatory', trustServerCertificate: true };
const log = { info() {}, debug() {}, trace() {}, warn: (_s, m) => console.error('warn:', m), error: (_s, m) => console.error('error:', m) };
const client = new McpStdioClient(exe, {
  MSSQL_CONNECTIONS: buildServerConnections([profile], new Map(), { forceReadOnly: true, insights: false }),
  USE_INSIGHTS_LAYER: 'false', MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
}, log);
try {
  await client.initialize();
  console.log(describeServerInfo(await client.callTool('get_server_info', { connection: 'dcdev' })));
} finally { client.dispose(); }
