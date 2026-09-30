import * as vscode from 'vscode';
import { McpStdioClient } from '../client/mcpStdioClient';
import { resolveExe } from '../exe';
import { Logger } from '../logger';
import { ConnectionProfile } from './profile';
import { buildServerConnections, missingPasswordMessage, missingPasswords } from './serverEnv';
import { describeServerInfo } from './serverInfo';

/**
 * Connects with a short-lived, read-only, insights-free server process and returns a one-line
 * summary from get_server_info. Throws with a user-presentable message on failure.
 * Task 5 can replace the body with a call through the shared explorer client.
 */
export async function probeConnection(
  extensionUri: vscode.Uri,
  profile: ConnectionProfile,
  passwords: Map<string, string>,
  log: Logger,
): Promise<string> {
  const exe = resolveExe(extensionUri);
  if (!exe.ok) throw new Error(exe.reason);
  const target = { ...profile, open: true };
  if (missingPasswords([target], passwords).length) throw new Error(missingPasswordMessage(profile.name));

  const client = new McpStdioClient(exe.path, {
    MSSQL_CONNECTIONS: buildServerConnections([target], passwords, { forceReadOnly: true, insights: false }),
    USE_INSIGHTS_LAYER: 'false',
    MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
  }, log);
  try {
    await client.initialize();
    return describeServerInfo(await client.callTool('get_server_info', { connection: profile.name }));
  } finally {
    client.dispose();
  }
}
