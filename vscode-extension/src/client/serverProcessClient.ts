import * as path from 'path';
import * as vscode from 'vscode';
import { CallCounter, acquireCurrent, cancelledError } from './callTracking';
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
  /**
   * Start the process even when no connection is open (the runner: `format_sql` needs none, and the server accepts an
   * empty connection set when MSSQL_SCRIPT_RUNNER is on). Bound calls still fail fast without an open connection.
   */
  allowNoConnections?: boolean;
}

/**
 * The query runner process: each profile keeps its own read-only flag, and the extension-only `run_script` tool is
 * enabled. Never give MSSQL_SCRIPT_RUNNER to an agent-facing server.
 */
export const RUNNER_OPTIONS: Readonly<ServerProcessOptions> = Object.freeze({
  label: 'runner',
  forceReadOnly: false,
  extraEnv: Object.freeze({ MSSQL_SCRIPT_RUNNER: 'true' }),
  allowNoConnections: true,
});

const NO_OPEN_CONNECTIONS = 'No open connections. Add or open a connection first.';

/** A start that was overtaken by reset()/dispose() while spawning. */
class SupersededError extends Error {}

/**
 * A private MssqlMcp process of the extension (separate from the agent-facing one), e.g. the object tree's
 * read-only explorer or the query runner. Spawned lazily with a single-flight guard; every call carries
 * `connection`. Only OPEN profiles are served; the Insights layer is off.
 *
 * A restart (profile set or server path changed) never kills a running call: new calls go to a fresh process at
 * once, and the old one is retired when its last in-flight call ends. Only dispose() kills at once.
 */
export class ServerProcessClient implements vscode.Disposable {
  private client?: McpStdioClient;
  /** In-flight calls per process. */
  private readonly calls = new WeakMap<McpStdioClient, CallCounter>();
  /** Replaced processes that still have calls running; disposed when idle (or by dispose()). */
  private readonly retiring = new Set<McpStdioClient>();
  /** Processes already disposed by this client (each one is disposed once only). */
  private readonly killed = new WeakSet<McpStdioClient>();
  private starting?: Promise<McpStdioClient>;
  /** Per process: open profiles left out of it for a missing password (lower-case names), known at start. */
  private readonly skippedNames = new WeakMap<McpStdioClient, ReadonlySet<string>>();
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
    if (opts?.signal?.aborted) throw cancelledError();
    // The runner may run without this connection: say why here instead of a server "unknown connection" error.
    if (this.options.allowNoConnections && !this.store.list().some(p => p.open)) throw new Error(NO_OPEN_CONNECTIONS);
    return this.invoke(tool, { ...args, connection }, `connection='${connection}'`, opts, connection);
  }

  /**
   * Calls a tool that is bound to no connection (the runner's `format_sql`) and returns the whole payload. Works with
   * no connection open when the process allows it ({@link ServerProcessOptions.allowNoConnections}).
   */
  async callUnbound(tool: string, args: Record<string, unknown>, opts?: CallToolOptions): Promise<unknown> {
    if (opts?.signal?.aborted) throw cancelledError();
    return this.invoke(tool, args, 'unbound', opts);
  }

  private async invoke(tool: string, args: Record<string, unknown>, what: string, opts?: CallToolOptions, connection?: string): Promise<unknown> {
    // A pending debounced reset means the running process has a stale profile set: apply it first.
    if (this.timer) this.reset();
    // Starting the process can take seconds: a Cancel during that time rejects at once (the start goes on).
    // The process must still be the current one when the call is counted, or a reset in between would dispose it.
    const client = await acquireCurrent(() => this.ensure(), c => c === this.client, opts?.signal);
    // Counted synchronously after the check: no reset can run in between.
    if (connection !== undefined && this.skippedNames.get(client)?.has(connection.toLowerCase())) {
      throw new Error(missingPasswordMessage(connection));
    }
    const end = this.counterOf(client).begin();
    this.log.debug(this.options.label, `${tool} ${what}`);
    try {
      return await client.callTool(tool, args, opts);
    } finally {
      end();
    }
  }

  /**
   * Restarts the process (lazily) so it picks up the current profile set. Calls still running on the old process
   * finish there; it is disposed after the last one.
   */
  reset(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.generation++;
    this.starting = undefined;
    const old = this.client;
    this.client = undefined;
    if (old) this.retire(old);
    if (!this.disposed) this.resetEmitter.fire();
  }

  /** Kills the process at once, including replaced processes whose calls are still running. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.sub.dispose();
    this.reset();
    for (const client of this.retiring) this.kill(client);
    this.retiring.clear();
    this.resetEmitter.dispose();
  }

  private counterOf(client: McpStdioClient): CallCounter {
    let counter = this.calls.get(client);
    if (!counter) {
      counter = new CallCounter();
      this.calls.set(client, counter);
    }
    return counter;
  }

  /** Disposes a replaced process once no call is running on it. */
  private retire(client: McpStdioClient): void {
    const counter = this.counterOf(client);
    if (counter.inFlight > 0) {
      this.log.info(this.options.label, `Restart: the old process is kept until its ${counter.inFlight} running call(s) end.`);
      this.retiring.add(client);
    }
    counter.whenIdle(() => {
      this.retiring.delete(client);
      this.kill(client);
    });
  }

  /** Disposes `client` once; later calls (dispose() first, then its last call ending) do nothing. */
  private kill(client: McpStdioClient): void {
    if (this.killed.has(client)) return;
    this.killed.add(client);
    client.dispose();
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
    if (open.length - skipped.length <= 0 && !this.options.allowNoConnections) {
      throw new Error(skipped.length ? skipped.map(missingPasswordMessage).join(' ') : NO_OPEN_CONNECTIONS);
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
    this.skippedNames.set(client, new Set(skipped.map(n => n.toLowerCase())));
    this.client = client;
    return client;
  }
}
