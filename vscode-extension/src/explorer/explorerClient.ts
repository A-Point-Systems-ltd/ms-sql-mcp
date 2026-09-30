import * as path from 'path';
import * as vscode from 'vscode';
import { McpStdioClient } from '../client/mcpStdioClient';
import { pick } from '../client/parse';
import { ConnectionStore } from '../connections/store';
import { buildServerConnections, missingPasswordMessage, missingPasswords } from '../connections/serverEnv';
import { resolveExe } from '../exe';
import { Logger } from '../logger';

const RESET_DEBOUNCE_MS = 300;

/**
 * The object tree's private, read-only MssqlMcp process (separate from the agent-facing one).
 * Spawned lazily with a single-flight guard; every call carries `connection`. Only OPEN profiles are served.
 */
export class ExplorerClient implements vscode.Disposable {
  private client?: McpStdioClient;
  private starting?: Promise<McpStdioClient>;
  private generation = 0;
  private timer?: NodeJS.Timeout;
  private readonly sub: vscode.Disposable;

  constructor(private readonly context: vscode.ExtensionContext, private readonly store: ConnectionStore, private readonly log: Logger) {
    this.sub = store.onDidChange(() => this.scheduleReset());
  }

  /**
   * Calls `tool` on `connection` and returns the result's `data` (or the whole payload when it has none).
   * Throws Error(server error text) when the tool reports success=false.
   */
  async call<T = unknown>(connection: string, tool: string, args: Record<string, unknown>): Promise<T> {
    const client = await this.ensure();
    const payload = await client.callTool(tool, { ...args, connection });
    const data = pick(payload, 'data');
    return (data !== undefined ? data : payload) as T;
  }

  /** Restarts the process (lazily) so it picks up the current profile set. */
  reset(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.generation++;
    this.starting = undefined;
    const old = this.client;
    this.client = undefined;
    old?.dispose();
  }

  dispose(): void {
    this.sub.dispose();
    this.reset();
  }

  private scheduleReset(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.reset(), RESET_DEBOUNCE_MS);
  }

  private ensure(): Promise<McpStdioClient> {
    if (this.client) return Promise.resolve(this.client);
    if (!this.starting) {
      const starting = this.start(this.generation).finally(() => {
        if (this.starting === starting) this.starting = undefined;
      });
      this.starting = starting;
    }
    return this.starting;
  }

  private async start(generation: number): Promise<McpStdioClient> {
    const exe = resolveExe(this.context.extensionUri);
    if (!exe.ok) throw new Error(exe.reason);
    const profiles = this.store.list();
    const passwords = await this.store.passwords();
    const open = profiles.filter(p => p.open);
    const skipped = missingPasswords(profiles, passwords);
    // The server exits (FATAL) on an empty config, so fail fast instead of spawning it.
    if (open.length - skipped.length <= 0) {
      throw new Error(skipped.length
        ? skipped.map(missingPasswordMessage).join(' ')
        : 'No open connections. Add or open a connection first.');
    }
    const client = new McpStdioClient(exe.path, {
      MSSQL_CONNECTIONS: buildServerConnections(profiles, passwords, { forceReadOnly: true, insights: false }),
      USE_INSIGHTS_LAYER: 'false',
      MSSQL_ALLOW_ADHOC_CONNECTIONS: 'false',
      LOG_FILE_PATH: path.join(this.context.globalStorageUri.fsPath, 'logs') + path.sep,
    }, this.log, () => {
      if (this.client === client) this.client = undefined;
    });
    try {
      await client.initialize();
    } catch (err) {
      client.dispose();
      throw err;
    }
    if (generation !== this.generation) {
      // reset() ran while we were starting: this process carries a stale profile set.
      client.dispose();
      throw new Error('Connections changed while the explorer was starting; retry.');
    }
    this.client = client;
    return client;
  }
}
