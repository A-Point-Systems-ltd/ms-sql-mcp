import * as vscode from 'vscode';
import { McpStdioClient } from '../client/mcpStdioClient';
import { resolveExe } from '../exe';
import { Logger } from '../logger';
import { ConnectionProfile } from './profile';
import { buildServerConnections, explorerProcessEnv, missingPasswordMessage, missingPasswords } from './serverEnv';
import { describeServerInfo } from './serverInfo';

/** Time limit for a one-off connection test or database listing. */
export const PROBE_TIMEOUT_MS = 30_000;

/**
 * Runs `fn` against a short-lived, read-only, insights-free server process serving only `profile` (open, under its own
 * name), then disposes the process. Throws with a user-presentable message when the process cannot be started or the
 * password is missing; aborting `signal` kills the process and rethrows the abort reason.
 */
export async function withProbeClient<T>(
  extensionUri: vscode.Uri,
  profile: ConnectionProfile,
  passwords: Map<string, string>,
  log: Logger,
  signal: AbortSignal | undefined,
  fn: (client: McpStdioClient) => Promise<T>,
): Promise<T> {
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
    return await fn(client);
  } catch (err) {
    if (signal?.aborted) throw signal.reason;
    throw err;
  } finally {
    signal?.removeEventListener('abort', onAbort);
    client.dispose();
  }
}

/**
 * Connects with a short-lived probe process and returns a one-line summary from get_server_info.
 * Throws with a user-presentable message on failure.
 */
export function probeConnection(
  extensionUri: vscode.Uri,
  profile: ConnectionProfile,
  passwords: Map<string, string>,
  log: Logger,
  signal?: AbortSignal,
): Promise<string> {
  return withProbeClient(extensionUri, profile, passwords, log, signal,
    async client => describeServerInfo(await client.callTool('get_server_info', { connection: profile.name })));
}
