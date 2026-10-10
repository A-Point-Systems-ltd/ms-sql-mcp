import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import { CallToolOptions, McpClient } from './mcpClient';
import { DOCUMENT_TOOLS, McpToolError, traceableArguments, traceablePayload, unwrapToolResult } from './parse';
import type { Logger } from '../logger';

/**
 * Server tools whose row results default to TOON (compact text for LLMs). The extension parses results as JSON, so
 * every call to them opts out with toon=false unless the caller set it.
 */
export const TOON_TOOLS: ReadonlySet<string> = new Set(['read_data', 'list_objects', 'list_insights', 'get_insight_history']);

/** `args` with toon=false added for a TOON tool (see {@link TOON_TOOLS}); unchanged otherwise. */
export function withJsonRows(name: string, args: Record<string, unknown>): Record<string, unknown> {
  return TOON_TOOLS.has(name) && args.toon === undefined ? { ...args, toon: false } : args;
}

const PROTOCOL_VERSION = '2024-11-05';
const DEFAULT_TIMEOUT_MS = 60_000;
const CANCEL_REASON = 'Cancelled by user';
/** How many cancelled / timed-out request ids are remembered so their late responses are dropped quietly. */
const MAX_ABANDONED_IDS = 256;

export interface McpStdioClientOptions {
  /** Timeout of every request that does not pass its own `timeoutMs` (default 60 000 ms). */
  defaultTimeoutMs?: number;
  /** Command-line arguments for the server executable (the tests run a node script). */
  args?: string[];
}

interface PendingRequest {
  method: string;
  startedAt: number;
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer?: NodeJS.Timeout;
  /** Detaches the AbortSignal listener. */
  unlisten?: () => void;
}

/**
 * A private instance of MssqlMcp.exe spoken to directly over newline-delimited
 * JSON-RPC 2.0 (stdio). Separate from the instance the agent uses; the caller
 * supplies the env (connection profiles, read-only flags).
 */
export class McpStdioClient implements McpClient {
  private child: ChildProcessWithoutNullStreams | undefined;
  private nextId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  /** Ids of requests given up on (cancelled / timed out); a late response for one of them is ignored. */
  private readonly abandoned = new Set<number>();
  private readonly defaultTimeoutMs: number;
  private stdoutBuffer = '';
  private initialized = false;
  private disposed = false;

  constructor(
    private readonly exePath: string,
    private readonly env: Record<string, string>,
    private readonly log: Logger,
    /** Called when the server dies on its own (not via dispose()). */
    private readonly onUnexpectedExit?: () => void,
    private readonly options: McpStdioClientOptions = {},
  ) {
    this.defaultTimeoutMs = options.defaultTimeoutMs ?? DEFAULT_TIMEOUT_MS;
  }

  async initialize(): Promise<void> {
    if (this.initialized) {
      return;
    }
    this.spawnProcess();

    await this.request('initialize', {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: 'ms-sql-mcp-vscode', version: '1.0.0' },
    });
    this.notify('notifications/initialized');

    this.initialized = true;
    this.log.info('client', 'MCP handshake complete; ready.');
  }

  /**
   * Calls a tool and returns its unwrapped payload.
   * - `timeoutMs` overrides the default timeout; `null` waits without a limit.
   * - Aborting `signal` sends `notifications/cancelled` to the server and rejects at once with
   *   `McpToolError('Cancelled.')` (`cancelled === true`); the server's late response is ignored.
   */
  async callTool(name: string, args: Record<string, unknown> = {}, opts: CallToolOptions = {}): Promise<unknown> {
    this.log.debug('tool', `→ ${name}`);
    this.log.trace('tool', `${name} arguments`, traceableArguments(name, args));
    const startedAt = Date.now();
    try {
      const result = await this.request('tools/call', { name, arguments: withJsonRows(name, args) }, opts);
      const payload = unwrapToolResult(result);
      this.log.debug('tool', `← ${name} ok (${Date.now() - startedAt} ms)`);
      this.log.trace('tool', `${name} result`, traceablePayload(name, payload));
      return payload;
    } catch (err) {
      if (err instanceof McpToolError && err.cancelled) {
        // IntelliSense requests are cancelled on almost every keystroke: debug, not info.
        const message = `← ${name} cancelled (${Date.now() - startedAt} ms)`;
        if (name === 'language_service') this.log.debug('tool', message);
        else this.log.info('tool', message);
      } else if (DOCUMENT_TOOLS.has(name)) {
        // The message can quote the document (client data): the error line has none; trace (documented as possibly
        // holding SQL text) gets it for troubleshooting.
        this.log.error('tool', `← ${name} FAILED (${Date.now() - startedAt} ms)`);
        this.log.trace('tool', `${name} failure`, err instanceof Error ? err.message : String(err));
      } else {
        this.log.error('tool', `← ${name} FAILED (${Date.now() - startedAt} ms)`, err);
      }
      throw err;
    }
  }

  /** Sends a JSON-RPC notification (no response expected). A no-op when the process is not running. */
  notify(method: string, params: Record<string, unknown> = {}): void {
    if (!this.child) {
      return;
    }
    this.log.trace('rpc', `→ notification ${method}`);
    this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
  }

  dispose(): void {
    this.disposed = true;
    this.failAll(new McpToolError('Client disposed.'));
    if (this.child && !this.child.killed) {
      this.log.debug('client', 'Terminating private server process.');
      this.child.kill();
    }
    this.child = undefined;
    this.initialized = false;
  }

  private spawnProcess(): void {
    this.log.info('client', `Spawning ${this.exePath}`);
    const child = spawn(this.exePath, this.options.args ?? [], { windowsHide: true, env: { ...process.env, ...this.env } });
    this.child = child;

    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => this.onStdout(chunk));
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk: string) => {
      const text = chunk.trimEnd();
      if (text.length > 0) {
        this.log.warn('server', text);
      }
    });
    child.on('exit', (code) => this.onExit(code));
    child.on('error', (err) => {
      this.log.error('client', 'Server process error', err);
      this.failAll(new McpToolError(`Server process error: ${err.message}`));
    });
  }

  private onStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    let newlineIndex: number;
    while ((newlineIndex = this.stdoutBuffer.indexOf('\n')) >= 0) {
      const line = this.stdoutBuffer.slice(0, newlineIndex).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(newlineIndex + 1);
      if (line.length > 0) {
        this.handleLine(line);
      }
    }
  }

  private handleLine(line: string): void {
    let message: { id?: unknown; result?: unknown; error?: { message?: string; code?: number } };
    try {
      message = JSON.parse(line);
    } catch {
      // The server must keep stdout clean; surface anything else as a warning.
      this.log.warn('server', `Ignoring non-JSON stdout: ${line}`);
      return;
    }
    if (typeof message.id !== 'number') {
      // Never the body: a notification or late message can carry document text or result data.
      const method = (message as { method?: unknown }).method;
      this.log.trace('rpc', `Notification or unmatched message (${typeof method === 'string' ? `method ${method}` : `id ${String(message.id)}`})`);
      return;
    }
    const pending = this.pending.get(message.id);
    if (!pending) {
      if (this.abandoned.delete(message.id)) {
        this.log.debug('rpc', `Ignoring late response for abandoned request #${message.id}`);
      } else {
        this.log.warn('rpc', `Response for unknown request id ${message.id}`);
      }
      return;
    }
    this.settle(message.id, pending);
    if (message.error) {
      pending.reject(
        new McpToolError(
          `${message.error.message ?? 'JSON-RPC error'}${
            message.error.code !== undefined ? ` (code ${message.error.code})` : ''
          }`,
        ),
      );
    } else {
      pending.resolve(message.result);
    }
  }

  private onExit(code: number | null): void {
    const unexpected = !this.disposed;
    if (unexpected) {
      this.log.warn('client', `Server process exited unexpectedly (code ${code ?? 'null'}).`);
    }
    this.failAll(new McpToolError(`Server process exited (code ${code ?? 'null'}).`));
    this.child = undefined;
    this.initialized = false;
    if (unexpected) {
      this.onUnexpectedExit?.();
    }
  }

  private failAll(error: Error): void {
    for (const [id, pending] of [...this.pending]) {
      this.settle(id, pending);
      pending.reject(error);
    }
  }

  /** Removes a pending request and releases its timer and abort listener; the caller resolves or rejects it. */
  private settle(id: number, pending: PendingRequest): void {
    this.pending.delete(id);
    clearTimeout(pending.timer);
    pending.unlisten?.();
  }

  /** Gives up on a pending request: tells the server to stop working on it and drops its late response. */
  private abandon(id: number, pending: PendingRequest, reason: string, error: McpToolError): void {
    this.settle(id, pending);
    this.abandoned.add(id);
    if (this.abandoned.size > MAX_ABANDONED_IDS) {
      this.abandoned.delete(this.abandoned.values().next().value as number);
    }
    // MCP forbids cancelling `initialize`; its late response is still dropped quietly.
    if (pending.method !== 'initialize') this.notify('notifications/cancelled', { requestId: id, reason });
    pending.reject(error);
  }

  private request(method: string, params: Record<string, unknown>, opts: CallToolOptions = {}): Promise<unknown> {
    if (opts.signal?.aborted) {
      return Promise.reject(new McpToolError('Cancelled.', { cancelled: true }));
    }
    if (!this.child) {
      return Promise.reject(new McpToolError('Server process is not running.'));
    }
    const id = this.nextId++;
    const timeoutMs = opts.timeoutMs === undefined ? this.defaultTimeoutMs : opts.timeoutMs;
    const payload = JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n';
    return new Promise<unknown>((resolve, reject) => {
      const pending: PendingRequest = { method, startedAt: Date.now(), resolve, reject };
      if (timeoutMs !== null) {
        pending.timer = setTimeout(() => {
          this.log.error('rpc', `Request #${id} (${method}) timed out after ${timeoutMs} ms.`);
          this.abandon(id, pending, `Timed out after ${timeoutMs} ms`, new McpToolError(`Timed out after ${timeoutMs} ms calling ${method}.`));
        }, timeoutMs);
      }
      const signal = opts.signal;
      if (signal) {
        const onAbort = () => {
          if (this.pending.get(id) !== pending) return;
          this.log.debug('rpc', `Request #${id} (${method}) cancelled.`);
          this.abandon(id, pending, CANCEL_REASON, new McpToolError('Cancelled.', { cancelled: true }));
        };
        signal.addEventListener('abort', onAbort, { once: true });
        pending.unlisten = () => signal.removeEventListener('abort', onAbort);
      }
      this.pending.set(id, pending);
      this.child!.stdin.write(payload, (err) => {
        if (err && this.pending.get(id) === pending) {
          this.settle(id, pending);
          this.log.error('rpc', `Failed to write request #${id} (${method})`, err);
          reject(new McpToolError(`Failed to write to server: ${err.message}`));
        }
      });
    });
  }
}
