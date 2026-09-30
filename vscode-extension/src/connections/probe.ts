import * as vscode from 'vscode';
import { McpStdioClient } from '../client/mcpStdioClient';
import { resolveExe } from '../exe';
import { Logger } from '../logger';
import { ConnectionProfile } from './profile';
import { buildServerConnections, explorerProcessEnv, missingPasswordMessage, missingPasswords } from './serverEnv';
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
  signal?: AbortSignal,
): Promise<string> {
  const exe = resolveExe(extensionUri);
  if (!exe.ok) throw new Error(exe.reason);
  const target = { ...profile, open: true };
  if (missingPasswords([target], passwords).length) throw new Error(missingPasswordMessage(profile.name));

  if (signal?.aborted) throw signal.reason;
  const client = new McpStdioClient(exe.path, explorerProcessEnv(
    buildServerConnections([target], passwords, { forceReadOnly: true, insights: false }),
  ), log);
  // Aborting disposes the client, which fails the in-flight request and kills the process.
  const onAbort = () => client.dispose();
  signal?.addEventListener('abort', onAbort, { once: true });
  try {
    await client.initialize();
    return describeServerInfo(await client.callTool('get_server_info', { connection: profile.name }));
  } catch (err) {
    if (signal?.aborted) throw signal.reason;
    throw err;
  } finally {
    signal?.removeEventListener('abort', onAbort);
    client.dispose();
  }
}
