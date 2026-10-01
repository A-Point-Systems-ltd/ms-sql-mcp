import * as path from 'path';
import * as vscode from 'vscode';
import type { CallToolOptions } from './mcpClient';
import { McpStdioClient } from './mcpStdioClient';
import { pick } from './parse';
import { ConnectionStore } from '../connections/store';
import { buildServerConnections, explorerProcessEnv, missingPasswordMessage, missingPasswords } from '../connections/serverEnv';
import { resolveExe } from '../exe';
import { Logger } from '../logger';

const RESET_DEBOUNCE_MS = 300;

export interface ServerProcessOptions {
  /** Log scope and error-message prefix, e.g. 'explorer' or 'runner'. */
  label: string;
  /** Serve every profile read-only (explorer) instead of with its own read-only flag (runner). */
  forceReadOnly: boolean;
  /** Extra env for the process, e.g. `{ MSSQL_SCRIPT_RUNNER: 'true' }`. */
  extraEnv?: Record<string, string>;
}

/**
 * The query runner process: each profile keeps its own read-only flag, and the extension-only `run_script` tool is
 * enabled. Never give MSSQL_SCRIPT_RUNNER to an agent-facing server.
 */
export const RUNNER_OPTIONS: Readonly<ServerProcessOptions> = Object.freeze({
  label: 'runner',
  forceReadOnly: false,
  extraEnv: Object.freeze({ MSSQL_SCRIPT_RUNNER: 'true' }),
});

/** A start that was overtaken by reset()/dispose() while spawning. */
class SupersededError extends Error {}

/**
 * A private MssqlMcp process of the extension (separate from the agent-facing one), e.g. the object tree's
 * read-only explorer or the query runner. Spawned lazily with a single-flight guard; every call carries
 * `connection`. Only OPEN profiles are served; the Insights layer is off.
 */
export class ServerProcessClient implements vscode.Disposable {
  private client?: McpStdioClient;
  private starting?: Promise<McpStdioClient>;
  private generation = 0;
  private timer?: NodeJS.Timeout;
  private disposed = false;
  private readonly resetEmitter = new vscode.EventEmitter<void>();
  /** Fired after the process was reset (profile set changed); tree views should refresh. */
  readonly onDidReset = this.resetEmitter.event;
  private readonly sub: vscode.Disposable;
  /** 'Explorer' for label 'explorer': used in error messages. */
  private readonly title: string;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly store: ConnectionStore,
    private readonly log: Logger,
    private readonly options: ServerProcessOptions,
  ) {
    this.title = options.label.charAt(0).toUpperCase() + options.label.slice(1);
    this.sub = store.onDidChange(() => this.scheduleReset());
  }

  /**
   * Calls `tool` on `connection` and returns the result's `data` (or the whole payload when it has none).
   * Throws Error(server error text) when the tool reports success=false.
   */
  async call<T = unknown>(connection: string, tool: string, args: Record<string, unknown>, opts?: CallToolOptions): Promise<T> {
    const payload = await this.callResult(connection, tool, args, opts);
    const data = pick(payload, 'data');
    return (data !== undefined ? data : payload) as T;
  }

  /**
   * Like call(), but returns the whole tool payload ({success, data, truncated?, maxRows?, ...}),
   * for results whose top-level fields matter (read_data truncation). `opts` ({timeoutMs, signal}) go to callTool.
   */
  async callResult(connection: string, tool: string, args: Record<string, unknown>, opts?: CallToolOptions): Promise<unknown> {
    // A pending debounced reset means the running process has a stale profile set: apply it first.
    if (this.timer) this.reset();
    const client = await this.ensure();
    this.log.debug(this.options.label, `${tool} connection='${connection}'`);
    return client.callTool(tool, { ...args, connection }, opts);
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
    if (!this.disposed) this.resetEmitter.fire();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.sub.dispose();
    this.reset();
    this.resetEmitter.dispose();
  }

  private scheduleReset(): void {
    if (this.disposed) return;
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.reset(), RESET_DEBOUNCE_MS);
  }

  private async ensure(): Promise<McpStdioClient> {
    for (let attempt = 0; ; attempt++) {
      if (this.disposed) throw new Error(`${this.title} client is disposed.`);
      if (this.client) return this.client;
      if (!this.starting) {
        const starting = this.start(this.generation).finally(() => {
          if (this.starting === starting) this.starting = undefined;
        });
        this.starting = starting;
      }
      try {
        return await this.starting;
      } catch (err) {
        // The profile set changed while starting: retry once against the new one.
        if (err instanceof SupersededError && attempt === 0) continue;
        throw err;
      }
    }
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
    const client = new McpStdioClient(exe.path, explorerProcessEnv(
      buildServerConnections(profiles, passwords, { forceReadOnly: this.options.forceReadOnly, insights: false }),
      { LOG_FILE_PATH: path.join(this.context.globalStorageUri.fsPath, 'logs') + path.sep, ...this.options.extraEnv },
    ), this.log, () => {
      if (this.client === client) this.client = undefined;
    });
    try {
      await client.initialize();
    } catch (err) {
      client.dispose();
      throw err;
    }
    if (generation !== this.generation || this.disposed) {
      // reset()/dispose() ran while we were starting: this process carries a stale profile set.
      client.dispose();
      throw new SupersededError(`${this.title} restarted while starting.`);
    }
    this.client = client;
    return client;
  }
}
